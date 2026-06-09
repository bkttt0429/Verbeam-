using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class SpeechService
{
    private readonly VerbeamOptions _options;
    private readonly SpeechProviderRegistry _providers;
    private readonly GlossaryStore _glossaries;
    private readonly ISpeechEventStore _eventStore;

    public SpeechService(
        VerbeamOptions options,
        SpeechProviderRegistry providers,
        GlossaryStore glossaries,
        ISpeechEventStore eventStore)
    {
        _options = options;
        _providers = providers;
        _glossaries = glossaries;
        _eventStore = eventStore;
    }

    public async Task<SpeechResponse> RecognizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        var providerName = Pick(request.Provider, _options.Speech.DefaultProvider);
        var language = Pick(request.Language, _options.Speech.DefaultLanguage);
        var profileId = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
        var preferCaptions = request.PreferCaptions ?? _options.Speech.PreferCaptions;
        var stopwatch = Stopwatch.StartNew();

        if (!string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            var sourceUrl = request.SourceUrl.Trim();
            if (preferCaptions && IsYouTubeUrl(sourceUrl))
            {
                var captionSegments = await TryLoadYouTubeCaptionsAsync(sourceUrl, language, cancellationToken);
                if (captionSegments.Count > 0)
                {
                    stopwatch.Stop();
                    return await StoreAndBuildResponseAsync(
                        profileId,
                        sessionId,
                        sourceKind: "youtube-captions",
                        sourceUri: sourceUrl,
                        audioHash: ComputeSha256(Encoding.UTF8.GetBytes(sourceUrl)),
                        audioMimeType: "text/vtt",
                        language,
                        provider: "youtube-captions",
                        engine: "yt-dlp:captions",
                        segments: captionSegments,
                        captionsUsed: true,
                        stopwatch.ElapsedMilliseconds,
                        cancellationToken);
                }
            }

            if (IsYouTubeUrl(sourceUrl))
            {
                return await RunYouTubeAudioChunksAndStoreAsync(
                    request,
                    providerName,
                    language,
                    profileId,
                    sessionId,
                    sourceUrl,
                    stopwatch,
                    cancellationToken);
            }

            var sourceAudio = await DownloadAudioFromUrlAsync(sourceUrl, cancellationToken);
            stopwatch.Restart();
            return await RunProviderAndStoreAsync(
                request,
                providerName,
                language,
                profileId,
                sessionId,
                sourceAudio,
                stopwatch,
                cancellationToken);
        }

        var decoded = DecodeAudio(request.AudioBase64, request.AudioMimeType, _options.Speech.MaxAudioBytes);
        return await RunProviderAndStoreAsync(
            request,
            providerName,
            language,
            profileId,
            sessionId,
            decoded,
            stopwatch,
            cancellationToken);
    }

    private async Task<SpeechResponse> RunYouTubeAudioChunksAndStoreAsync(
        SpeechRequest request,
        string providerName,
        string language,
        string profileId,
        string sessionId,
        string sourceUrl,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var provider = _providers.GetRequired(providerName);
        var glossary = await _glossaries.GetOptionalAsync(request.Glossary, cancellationToken);
        var chunkSeconds = Math.Max(30, _options.Speech.YouTube.AudioChunkSeconds);
        var segments = new List<SpeechSegment>();
        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var chunk in DownloadYouTubeAudioChunksAsync(sourceUrl, chunkSeconds, cancellationToken))
        {
            if (chunk.AudioBytes.Length > _options.Speech.MaxAudioBytes)
            {
                throw new ArgumentException($"audio chunk is too large. Max size is {_options.Speech.MaxAudioBytes} bytes. Lower Verbeam:Speech:YouTube:AudioChunkSeconds.");
            }

            var providerRequest = new SpeechProviderRequest(
                chunk.AudioBytes,
                "audio/wav",
                language,
                $"{sourceUrl}#chunk={chunk.Index}",
                glossary.Terms);

            var result = await provider.TranscribeAsync(providerRequest, cancellationToken);
            engines.Add(result.Engine);
            var chunkSegments = NormalizeSegments(result.Text, result.Segments, language);
            if (chunkSegments.Count == 1 &&
                chunkSegments[0].StartSeconds <= 0 &&
                chunkSegments[0].EndSeconds <= 0)
            {
                chunkSegments = [chunkSegments[0] with { EndSeconds = EstimateWavDurationSeconds(chunk.AudioBytes, chunkSeconds) }];
            }

            foreach (var segment in chunkSegments)
            {
                segments.Add(segment with
                {
                    Index = segments.Count,
                    StartSeconds = chunk.StartSeconds + segment.StartSeconds,
                    EndSeconds = chunk.StartSeconds + Math.Max(segment.EndSeconds, segment.StartSeconds),
                    Language = string.IsNullOrWhiteSpace(segment.Language) ? language : segment.Language
                });
            }
        }

        stopwatch.Stop();
        var expandedSegments = ExpandLongSegments(segments, language);
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("YouTube audio was downloaded, but ASR produced no speech segments.");
        }

        var engine = engines.Count == 0
            ? $"{provider.Descriptor.Name}:chunks"
            : $"{string.Join("+", engines.Order(StringComparer.OrdinalIgnoreCase))}:chunks";

        return await StoreAndBuildResponseAsync(
            profileId,
            sessionId,
            sourceKind: "youtube-audio-chunks",
            sourceUri: sourceUrl,
            audioHash: ComputeSha256(Encoding.UTF8.GetBytes(sourceUrl)),
            audioMimeType: "audio/wav",
            language,
            provider.Descriptor.Name,
            engine,
            expandedSegments,
            captionsUsed: false,
            stopwatch.ElapsedMilliseconds,
            cancellationToken);
    }

    private async Task<SpeechResponse> RunProviderAndStoreAsync(
        SpeechRequest request,
        string providerName,
        string language,
        string profileId,
        string sessionId,
        SpeechAudioInput audio,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var provider = _providers.GetRequired(providerName);
        var glossary = await _glossaries.GetOptionalAsync(request.Glossary, cancellationToken);
        var providerRequest = new SpeechProviderRequest(
            audio.AudioBytes,
            audio.AudioMimeType,
            language,
            audio.SourceUri,
            glossary.Terms);

        var result = await provider.TranscribeAsync(providerRequest, cancellationToken);
        stopwatch.Stop();

        var segments = NormalizeSegments(result.Text, result.Segments, language);
        return await StoreAndBuildResponseAsync(
            profileId,
            sessionId,
            audio.SourceKind,
            audio.SourceUri,
            ComputeSha256(audio.AudioBytes),
            audio.AudioMimeType,
            language,
            provider.Descriptor.Name,
            result.Engine,
            segments,
            captionsUsed: false,
            stopwatch.ElapsedMilliseconds,
            cancellationToken);
    }

    private async Task<SpeechResponse> StoreAndBuildResponseAsync(
        string profileId,
        string sessionId,
        string sourceKind,
        string sourceUri,
        string audioHash,
        string audioMimeType,
        string language,
        string provider,
        string engine,
        IReadOnlyList<SpeechSegment> segments,
        bool captionsUsed,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        var text = JoinSegmentText(segments);
        var eventId = Guid.NewGuid().ToString("N");
        await _eventStore.AddEventAsync(
            new SpeechEvent(
                eventId,
                profileId,
                sessionId,
                sourceKind,
                sourceUri,
                audioHash,
                audioMimeType,
                language,
                provider,
                engine,
                text,
                segments,
                captionsUsed,
                latencyMs,
                DateTimeOffset.UtcNow),
            cancellationToken);

        return new SpeechResponse(
            eventId,
            text,
            segments,
            provider,
            engine,
            language,
            sourceKind,
            sourceUri,
            audioMimeType,
            captionsUsed,
            latencyMs);
    }

    public async Task<IReadOnlyList<SpeechSegment>> TryLoadYouTubeCaptionsAsync(
        string sourceUrl,
        string language,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"verbeam-captions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var outputTemplate = Path.Combine(tempDirectory, "caption.%(ext)s");
            var arguments = new List<string>
            {
                "--skip-download",
                "--write-subs",
                "--write-auto-subs",
                "--sub-format",
                "vtt",
                "--sub-langs",
                BuildCaptionLanguageList(language),
                "-o",
                outputTemplate,
                sourceUrl
            };

            var result = await RunToolAsync(
                _options.Speech.YouTube.YtDlpFileName,
                arguments,
                _options.Speech.YouTube.TimeoutSeconds,
                cancellationToken);

            var segments = await TryReadCaptionSegmentsAsync(tempDirectory, language, cancellationToken);
            if (segments.Count > 0)
            {
                return segments;
            }

            if (result.ExitCode != 0)
            {
                return Array.Empty<SpeechSegment>();
            }

            return Array.Empty<SpeechSegment>();
        }
        catch (Win32Exception)
        {
            return Array.Empty<SpeechSegment>();
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public async IAsyncEnumerable<SpeechAudioChunk> DownloadYouTubeAudioChunksAsync(
        string sourceUrl,
        int chunkSeconds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"verbeam-youtube-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var outputTemplate = Path.Combine(tempDirectory, "source.%(ext)s");
            var downloadArguments = new List<string>
            {
                "--no-playlist",
                "-f",
                Pick(_options.Speech.YouTube.AudioFormat, "bestaudio[abr<=64]/bestaudio/best"),
                "-o",
                outputTemplate,
                sourceUrl
            };

            var downloadResult = await RunToolAsync(
                _options.Speech.YouTube.YtDlpFileName,
                downloadArguments,
                _options.Speech.YouTube.TimeoutSeconds,
                cancellationToken);
            if (downloadResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp failed with exit code {downloadResult.ExitCode}: {downloadResult.Error.Trim()}");
            }

            var sourceAudioPath = Directory.EnumerateFiles(tempDirectory, "source.*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("yt-dlp did not produce an audio file.");

            var chunkTemplate = Path.Combine(tempDirectory, "chunk-%05d.wav");
            var ffmpegArguments = new List<string>
            {
                "-hide_banner",
                "-y",
                "-i",
                sourceAudioPath,
                "-ac",
                "1",
                "-ar",
                "16000",
                "-f",
                "segment",
                "-segment_time",
                chunkSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-reset_timestamps",
                "1",
                chunkTemplate
            };

            var ffmpegResult = await RunToolAsync(
                ResolveToolPath(_options.Speech.YouTube.FfmpegFileName),
                ffmpegArguments,
                _options.Speech.YouTube.TimeoutSeconds,
                cancellationToken);
            if (ffmpegResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg failed with exit code {ffmpegResult.ExitCode}: {ffmpegResult.Error.Trim()}");
            }

            var chunkIndex = 0;
            foreach (var chunkPath in Directory.EnumerateFiles(tempDirectory, "chunk-*.wav", SearchOption.TopDirectoryOnly).OrderBy(path => path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await ReadFileWithLimitAsync(chunkPath, _options.Speech.MaxAudioBytes, cancellationToken);
                yield return new SpeechAudioChunk(chunkIndex, chunkIndex * chunkSeconds, bytes);
                chunkIndex++;
            }

            if (chunkIndex == 0)
            {
                throw new InvalidOperationException("ffmpeg did not produce audio chunks.");
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task<SpeechAudioInput> DownloadAudioFromUrlAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Speech.FunAsrHttp.TimeoutSeconds))
        };

        using var response = await httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadStreamWithLimitAsync(stream, _options.Speech.MaxAudioBytes, cancellationToken);
        return new SpeechAudioInput(bytes, mimeType, "url-audio", sourceUrl);
    }

    private static SpeechAudioInput DecodeAudio(string? audioBase64, string? audioMimeType, int maxAudioBytes)
    {
        if (string.IsNullOrWhiteSpace(audioBase64))
        {
            throw new ArgumentException("audioBase64 or sourceUrl is required.");
        }

        var payload = audioBase64.Trim();
        var mimeType = Pick(audioMimeType, "application/octet-stream");

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = payload.IndexOf(',');
            if (commaIndex < 0)
            {
                throw new ArgumentException("audioBase64 data URI is missing a comma separator.");
            }

            var metadata = payload[5..commaIndex];
            var semicolonIndex = metadata.IndexOf(';');
            if (semicolonIndex > 0)
            {
                mimeType = metadata[..semicolonIndex];
            }
            else if (!string.IsNullOrWhiteSpace(metadata))
            {
                mimeType = metadata;
            }

            payload = payload[(commaIndex + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("audioBase64 must be valid base64.", ex);
        }

        if (bytes.Length == 0)
        {
            throw new ArgumentException("audioBase64 decoded to an empty payload.");
        }

        if (bytes.Length > maxAudioBytes)
        {
            throw new ArgumentException($"audio payload is too large. Max size is {maxAudioBytes} bytes.");
        }

        return new SpeechAudioInput(bytes, mimeType, "upload", string.Empty);
    }

    public static IReadOnlyList<SpeechSegment> NormalizeSegments(
        string text,
        IReadOnlyList<SpeechSegment> segments,
        string language)
    {
        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            segments = [new SpeechSegment(0, 0, 0, text.Trim(), 1.0, null, language)];
        }

        return segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .Select((segment, index) => segment with
            {
                Index = index,
                Text = segment.Text.Trim(),
                Language = string.IsNullOrWhiteSpace(segment.Language) ? language : segment.Language
            })
            .ToArray();
    }

    public static IReadOnlyList<SpeechSegment> ExpandLongSegments(
        IReadOnlyList<SpeechSegment> segments,
        string language)
    {
        var expanded = new List<SpeechSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var pieces = SplitTranscriptText(segment.Text, maxCharacters: 180);
            if (pieces.Count <= 1 || segment.EndSeconds <= segment.StartSeconds)
            {
                expanded.Add(segment with { Index = expanded.Count });
                continue;
            }

            var totalCharacters = pieces.Sum(piece => Math.Max(1, piece.Length));
            var cursor = segment.StartSeconds;
            var duration = segment.EndSeconds - segment.StartSeconds;
            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];
                var isLast = index == pieces.Count - 1;
                var pieceDuration = isLast
                    ? segment.EndSeconds - cursor
                    : duration * Math.Max(1, piece.Length) / totalCharacters;
                var end = isLast ? segment.EndSeconds : Math.Min(segment.EndSeconds, cursor + pieceDuration);
                expanded.Add(segment with
                {
                    Index = expanded.Count,
                    StartSeconds = cursor,
                    EndSeconds = Math.Max(cursor, end),
                    Text = piece,
                    Language = string.IsNullOrWhiteSpace(segment.Language) ? language : segment.Language
                });
                cursor = end;
            }
        }

        return expanded;
    }

    private static IReadOnlyList<string> SplitTranscriptText(string text, int maxCharacters)
    {
        var normalized = Regex.Replace(text.ReplaceLineEndings(" "), @"\s+", " ").Trim();
        if (normalized.Length <= maxCharacters)
        {
            return string.IsNullOrWhiteSpace(normalized) ? Array.Empty<string>() : [normalized];
        }

        var pieces = new List<string>();
        var builder = new StringBuilder();
        foreach (var ch in normalized)
        {
            builder.Append(ch);
            if (IsSentenceBoundary(ch) || builder.Length >= maxCharacters)
            {
                AddTranscriptPiece(pieces, builder);
            }
        }

        AddTranscriptPiece(pieces, builder);
        return pieces;
    }

    private static bool IsSentenceBoundary(char ch)
        => ch is '。' or '？' or '?' or '！' or '!' or '；' or ';' or '\n';

    private static void AddTranscriptPiece(List<string> pieces, StringBuilder builder)
    {
        var piece = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(piece))
        {
            pieces.Add(piece);
        }

        builder.Clear();
    }

    public static string JoinSegmentText(IReadOnlyList<SpeechSegment> segments)
        => string.Join(Environment.NewLine, segments.Select(segment => segment.Text).Where(value => !string.IsNullOrWhiteSpace(value)));

    private string BuildCaptionLanguageList(string language)
    {
        var values = new[] { language }
            .Concat(_options.Speech.YouTube.CaptionLanguages)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(",", values);
    }

    private static async Task<IReadOnlyList<SpeechSegment>> TryReadCaptionSegmentsAsync(
        string directory,
        string language,
        CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.vtt", SearchOption.AllDirectories).OrderBy(path => path))
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            var segments = TimedTextService.ParseVtt(text, language);
            if (segments.Count > 0)
            {
                return segments;
            }
        }

        return Array.Empty<SpeechSegment>();
    }

    private static string ResolveToolPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        if (Path.IsPathFullyQualified(fileName) || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return fileName;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Path.GetExtension(fileName))
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var directory in paths)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, fileName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }

    private static async Task<CommandResult> RunToolAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("External media tool is not configured.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"External media tool timed out after {timeoutSeconds} seconds.");
        }

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<byte[]> ReadFileWithLimitAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await ReadStreamWithLimitAsync(stream, maxBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                throw new ArgumentException($"audio payload is too large. Max size is {maxBytes} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public static bool IsYouTubeUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));

    public static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static double EstimateWavDurationSeconds(byte[] bytes, int fallbackSeconds)
    {
        const int sampleRate = 16000;
        const int bytesPerSample = 2;
        if (bytes.Length <= 44)
        {
            return fallbackSeconds;
        }

        return Math.Max(0, (bytes.Length - 44) / (double)(sampleRate * bytesPerSample));
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record SpeechAudioInput(
        byte[] AudioBytes,
        string AudioMimeType,
        string SourceKind,
        string SourceUri);

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
