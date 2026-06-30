using System.Diagnostics;
using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Providers;

public sealed class ExternalCommandSpeechProvider : ISpeechProvider
{
    private readonly ExternalSpeechOptions _options;
    private readonly string _workingDirectory;

    public ExternalCommandSpeechProvider(ExternalSpeechOptions options, string workingDirectory)
    {
        _options = options;
        _workingDirectory = workingDirectory;
    }

    public SpeechProviderDescriptor Descriptor { get; } = new(
        "external",
        "External ASR Command",
        "process",
        "ja",
        RequiresExternalProcess: true,
        IsLocal: true);

    public async Task<SpeechProviderResult> TranscribeAsync(
        SpeechProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.FileName))
        {
            throw new InvalidOperationException("External ASR provider is not configured. Set Verbeam:Speech:External:FileName.");
        }

        var extension = ExtensionFromMimeType(request.AudioMimeType);
        var audioPath = Path.Combine(Path.GetTempPath(), $"verbeam-asr-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(audioPath, request.AudioBytes, cancellationToken);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.FileName,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            // Pass each templated argument as a discrete ArgumentList entry (.NET applies correct
            // Win32 escaping) so request-derived values can't inject extra arguments. See
            // ExternalCommandTemplate.
            foreach (var argument in ExternalCommandTemplate.BuildArguments(
                _options.Arguments,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["{audio}"] = audioPath,
                    ["{language}"] = request.Language
                }))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start ASR command '{_options.FileName}'.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException($"External ASR command timed out after {_options.TimeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"External ASR command failed with exit code {process.ExitCode}: {stderr.Trim()}");
            }

            return SpeechJsonResultParser.Parse(stdout, $"external:{Path.GetFileName(_options.FileName)}");
        }
        finally
        {
            TryDelete(audioPath);
        }
    }

    private static string ExtensionFromMimeType(string mimeType)
        => mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/m4a" => ".m4a",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            _ => ".audio"
        };

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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
