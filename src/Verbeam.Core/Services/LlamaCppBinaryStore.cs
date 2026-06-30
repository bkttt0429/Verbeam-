using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class LlamaCppBinaryStore
{
    /// <summary>Marker written into an install dir once all dependency archives extracted OK.</summary>
    private const string DependencyMarkerName = ".deps-complete";

    private readonly LlamaCppOptions _options;
    private readonly ModelCatalogService _catalogs;
    private readonly string _contentRootPath;
    private readonly HttpClient _httpClient;
    private readonly LlamaCppDownloadTracker _downloads;
    private readonly HardwareProbe _hardwareProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LlamaCppBinaryStore(
        LlamaCppOptions options,
        ModelCatalogService catalogs,
        string contentRootPath,
        HttpClient httpClient,
        LlamaCppDownloadTracker? downloads = null,
        HardwareProbe? hardwareProbe = null)
    {
        _options = options;
        _catalogs = catalogs;
        _contentRootPath = contentRootPath;
        _httpClient = httpClient;
        _downloads = downloads ?? new LlamaCppDownloadTracker();
        _hardwareProbe = hardwareProbe ?? new HardwareProbe();
    }

    /// <summary>The backend flavor that will actually be used, after "auto" resolution.</summary>
    public string ResolveEffectiveFlavor()
        => PickPreferredBinary()?.Flavor ?? _options.BinaryFlavor.Trim();

    /// <summary>
    /// Ordered flavor preference for this host: the hardware-resolved list when the
    /// configured flavor is "auto", otherwise the explicit flavor followed by "cpu".
    /// Always ends at "cpu" so selection degrades instead of failing.
    /// </summary>
    public IReadOnlyList<string> GetFlavorPreferences()
    {
        var configured = LlamaCppBackendResolver.EffectiveFlavor(_options.ComputeTarget, _options.BinaryFlavor);
        return configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? LlamaCppBackendResolver.ResolveFlavorPreferences(_hardwareProbe.Detect())
            : [configured, "cpu"];
    }

    public async Task<IReadOnlyList<LlamaCppBinaryStatus>> GetStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var binaries = _catalogs.GetCurrent().LlamaCppBinaries
            .OrderBy(binary => binary.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binary => binary.Flavor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binary => binary.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binary => binary.Architecture, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var statuses = new List<LlamaCppBinaryStatus>(binaries.Length);
        foreach (var binary in binaries)
        {
            statuses.Add(await GetStatusAsync(binary, verifySha256: true, cancellationToken));
        }

        return statuses;
    }

    public async Task<LlamaCppBinaryDownloadResult> EnsureBinaryAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var binary = PickPreferredBinary()
                ?? throw new InvalidOperationException("No pinned llama.cpp binary artifact matches this platform. Configure Verbeam:LlamaCpp:ExecutablePath or add a matching catalog binary.");
            var status = await GetStatusAsync(binary, verifySha256: true, cancellationToken);
            if (status.Ready)
            {
                return new LlamaCppBinaryDownloadResult(
                    binary.Version,
                    binary.Flavor,
                    status.ExecutablePath,
                    Ready: true,
                    status.ActualSizeBytes ?? 0,
                    status.ActualSha256,
                    "llama.cpp binary is already installed and verified.");
            }

            var archivePath = ResolveArchivePath(binary);
            var directory = Path.GetDirectoryName(archivePath)
                ?? throw new InvalidOperationException($"Could not resolve llama.cpp binary directory for {Describe(binary)}.");
            Directory.CreateDirectory(directory);
            var tempPath = archivePath + ".download";

            try
            {
                var result = await DownloadToTempAsync(binary, tempPath, cancellationToken);
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }

                File.Move(tempPath, archivePath);
                ExtractOrInstall(binary, archivePath);
                await EnsureDependencyArchivesAsync(binary, cancellationToken);
                var executablePath = ResolveExecutablePath(binary);
                if (!File.Exists(executablePath))
                {
                    throw new FileNotFoundException($"llama.cpp binary artifact did not contain executable: {executablePath}", executablePath);
                }

                return result with
                {
                    ExecutablePath = executablePath,
                    Ready = true,
                    Message = "llama.cpp binary downloaded, verified, and installed."
                };
            }
            catch (OperationCanceledException)
            {
                // Keep the partial temp file so a later attempt can resume from it.
                throw;
            }
            catch
            {
                DeleteIfExists(tempPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ResolveExecutablePathAsync(CancellationToken cancellationToken = default)
    {
        if (PickPreferredBinary() is null)
        {
            return ResolveExternalExecutablePath();
        }

        var result = await EnsureBinaryAsync(cancellationToken);
        return result.ExecutablePath;
    }

    private LlamaCppBinaryArtifact? PickPreferredBinary()
    {
        var platform = CurrentPlatform();
        var architecture = CurrentArchitecture();
        var version = _options.PinnedVersion.Trim();
        var matches = _catalogs.GetCurrent().LlamaCppBinaries
            .Where(binary =>
                binary.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&
                binary.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) &&
                binary.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // "auto" detects the host GPU and asks the resolver; an explicit flavor is
        // honored as-is. Either way the chain always ends at "cpu".
        foreach (var flavor in GetFlavorPreferences())
        {
            var match = matches.FirstOrDefault(binary => binary.Flavor.Equals(flavor, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return matches.FirstOrDefault(binary => binary.Flavor.Equals("cpu", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<LlamaCppBinaryStatus> GetStatusAsync(
        LlamaCppBinaryArtifact binary,
        bool verifySha256,
        CancellationToken cancellationToken)
    {
        var archivePath = ResolveArchivePath(binary);
        var executablePath = ResolveExecutablePath(binary);
        var archiveExists = File.Exists(archivePath);
        var executableExists = File.Exists(executablePath);
        long? actualSizeBytes = null;
        var actualSha256 = string.Empty;
        var archiveSizeMatches = false;
        var archiveSha256Matches = false;

        if (archiveExists)
        {
            var info = new FileInfo(archivePath);
            actualSizeBytes = info.Length;
            archiveSizeMatches = actualSizeBytes == binary.SizeBytes;
            if (string.IsNullOrWhiteSpace(binary.Sha256))
            {
                // Trust-on-first-use entry: size is the only integrity signal.
                archiveSha256Matches = archiveSizeMatches;
            }
            else if (archiveSizeMatches && verifySha256)
            {
                actualSha256 = await ComputeSha256Async(archivePath, cancellationToken);
                archiveSha256Matches = actualSha256.Equals(binary.Sha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        var dependenciesReady = binary.DependencyArchives.Count == 0 ||
            File.Exists(Path.Combine(ResolveInstallDirectory(binary), DependencyMarkerName));
        var ready = executableExists && archiveExists && archiveSizeMatches &&
            (!verifySha256 || archiveSha256Matches) && dependenciesReady;
        return new LlamaCppBinaryStatus(
            binary.Version,
            binary.Flavor,
            binary.Platform,
            binary.Architecture,
            binary.Filename,
            archivePath,
            executablePath,
            binary.DownloadUrl,
            binary.SizeBytes,
            binary.Sha256,
            archiveExists,
            archiveSizeMatches,
            archiveSha256Matches,
            executableExists,
            ready,
            actualSizeBytes,
            actualSha256,
            BuildStatusMessage(archiveExists, archiveSizeMatches, archiveSha256Matches, executableExists));
    }

    private async Task<LlamaCppBinaryDownloadResult> DownloadToTempAsync(
        LlamaCppBinaryArtifact binary,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var sha256 = await DownloadVerifiedAsync(
            binary.DownloadUrl,
            binary.Sha256,
            binary.SizeBytes,
            tempPath,
            $"{binary.Version}-{binary.Flavor}",
            Describe(binary),
            cancellationToken);

        return new LlamaCppBinaryDownloadResult(
            binary.Version,
            binary.Flavor,
            tempPath,
            Ready: true,
            binary.SizeBytes,
            sha256,
            "llama.cpp binary downloaded and verified.");
    }

    /// <summary>
    /// Resumable, sha256-verified download shared by the main binary and its
    /// dependency archives (cudart). Returns the verified hash; throws on size /
    /// sha mismatch so a corrupt or wrong artifact can never be installed.
    /// </summary>
    private async Task<string> DownloadVerifiedAsync(
        string downloadUrl,
        string expectedSha256,
        long expectedSize,
        string tempPath,
        string sessionLabel,
        string describe,
        CancellationToken cancellationToken)
    {
        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (existingBytes >= expectedSize)
        {
            DeleteIfExists(tempPath);
            existingBytes = 0;
        }

        var session = _downloads.Begin("runtime", sessionLabel, expectedSize, existingBytes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.PauseToken);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
            if (existingBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // Server ignored the range request; restart from scratch.
                DeleteIfExists(tempPath);
                existingBytes = 0;
                session.Report(0);
            }

            response.EnsureSuccessStatusCode();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (existingBytes > 0)
            {
                await AppendFileToHashAsync(tempPath, hash, linked.Token);
            }

            await using var input = await response.Content.ReadAsStreamAsync(linked.Token);
            await using var output = new FileStream(
                tempPath,
                existingBytes > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);
            var buffer = new byte[1024 * 1024];
            var bytesWritten = existingBytes;
            while (true)
            {
                var read = await input.ReadAsync(buffer, linked.Token);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                bytesWritten += read;
                session.Report(bytesWritten);
            }

            await output.FlushAsync(linked.Token);
            var sha256 = ToHex(hash.GetHashAndReset());
            if (bytesWritten != expectedSize)
            {
                throw new InvalidDataException(
                    $"Downloaded llama.cpp archive size mismatch for {describe}: expected {expectedSize}, got {bytesWritten}.");
            }

            // Empty expected sha = trust-on-first-use (size-only verification). Used
            // for cross-platform catalog entries whose hash was not pre-pinned; the
            // size check still guards against truncated/wrong-length downloads.
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded llama.cpp archive sha256 mismatch for {describe}: expected {expectedSha256}, got {sha256}.");
            }

            session.Complete("llama.cpp archive downloaded and verified.");
            return sha256;
        }
        catch (OperationCanceledException) when (session.PauseToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            session.Pause("Download paused. Resume to continue from the partial file.");
            throw new LlamaCppDownloadPausedException($"Download paused for llama.cpp archive {describe}.");
        }
        catch (OperationCanceledException)
        {
            session.Pause("Download interrupted. Start the download again to resume.");
            throw;
        }
        catch (Exception ex)
        {
            session.Fail(ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Downloads and extracts each dependency archive (e.g. the Windows cudart zip)
    /// into the same install directory as the main binary, then writes a marker so
    /// a later run knows the install is complete. Skips work when the marker exists.
    /// </summary>
    private async Task EnsureDependencyArchivesAsync(
        LlamaCppBinaryArtifact binary,
        CancellationToken cancellationToken)
    {
        if (binary.DependencyArchives.Count == 0)
        {
            return;
        }

        var installDirectory = ResolveInstallDirectory(binary);
        var root = ResolveBinaryRoot(binary);
        foreach (var dependency in binary.DependencyArchives)
        {
            if (string.IsNullOrWhiteSpace(dependency.Filename) || string.IsNullOrWhiteSpace(dependency.DownloadUrl))
            {
                throw new InvalidOperationException($"llama.cpp dependency archive for {Describe(binary)} is missing filename/url.");
            }

            var archivePath = Path.GetFullPath(Path.Combine(root, dependency.Filename));
            if (!IsPathInside(root, archivePath))
            {
                throw new InvalidOperationException($"llama.cpp dependency filename resolves outside binary directory: {dependency.Filename}");
            }

            var tempPath = archivePath + ".download";
            await DownloadVerifiedAsync(
                dependency.DownloadUrl,
                dependency.Sha256,
                dependency.SizeBytes,
                tempPath,
                $"{binary.Version}-{binary.Flavor}-dep",
                $"{Describe(binary)} dependency {dependency.Filename}",
                cancellationToken);
            DeleteIfExists(archivePath);
            File.Move(tempPath, archivePath);
            ArchiveExtractor.ExtractInto(archivePath, installDirectory);
        }

        File.WriteAllText(Path.Combine(installDirectory, DependencyMarkerName), DateTimeOffset.UtcNow.ToString("O"));
    }

    private static async Task AppendFileToHashAsync(
        string path,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }
    }

    private void ExtractOrInstall(LlamaCppBinaryArtifact binary, string archivePath)
    {
        var installDirectory = ResolveInstallDirectory(binary);
        if (Directory.Exists(installDirectory))
        {
            Directory.Delete(installDirectory, recursive: true);
        }

        Directory.CreateDirectory(installDirectory);
        if (ArchiveExtractor.IsSupportedArchive(binary.Filename))
        {
            ArchiveExtractor.ExtractInto(archivePath, installDirectory);
            return;
        }

        var executablePath = ResolveExecutablePath(binary);
        var executableDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException($"Could not resolve executable directory for {Describe(binary)}.");
        Directory.CreateDirectory(executableDirectory);
        File.Copy(archivePath, executablePath, overwrite: true);
    }

    private string ResolveArchivePath(LlamaCppBinaryArtifact binary)
    {
        var root = ResolveBinaryRoot(binary);
        var archivePath = Path.GetFullPath(Path.Combine(root, binary.Filename));
        if (!IsPathInside(root, archivePath))
        {
            throw new InvalidOperationException($"llama.cpp binary filename resolves outside binary directory: {Describe(binary)}");
        }

        return archivePath;
    }

    private string ResolveExecutablePath(LlamaCppBinaryArtifact binary)
    {
        var installDirectory = ResolveInstallDirectory(binary);
        var executablePath = Path.GetFullPath(Path.Combine(installDirectory, binary.ExecutableRelativePath));
        if (!IsPathInside(installDirectory, executablePath))
        {
            throw new InvalidOperationException($"llama.cpp binary executableRelativePath resolves outside install directory: {Describe(binary)}");
        }

        return executablePath;
    }

    private string ResolveBinaryRoot(LlamaCppBinaryArtifact binary)
    {
        var binariesDirectory = Path.GetFullPath(PathResolver.Resolve(_contentRootPath, _options.BinariesDirectory));
        var root = Path.GetFullPath(Path.Combine(binariesDirectory, binary.Version, binary.Flavor));
        if (!IsPathInside(binariesDirectory, root))
        {
            throw new InvalidOperationException($"llama.cpp binary root resolves outside binaries directory: {Describe(binary)}");
        }

        return root;
    }

    private string ResolveInstallDirectory(LlamaCppBinaryArtifact binary)
        => Path.Combine(ResolveBinaryRoot(binary), "extracted");

    private string ResolveExternalExecutablePath()
        => Path.IsPathRooted(_options.ExecutablePath) || File.Exists(PathResolver.Resolve(_contentRootPath, _options.ExecutablePath))
            ? PathResolver.Resolve(_contentRootPath, _options.ExecutablePath)
            : _options.ExecutablePath;

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }

        return ToHex(hash.GetHashAndReset());
    }

    private static string BuildStatusMessage(
        bool archiveExists,
        bool archiveSizeMatches,
        bool archiveSha256Matches,
        bool executableExists)
    {
        if (!archiveExists)
        {
            return "llama.cpp binary artifact is not installed.";
        }

        if (!archiveSizeMatches)
        {
            return "llama.cpp binary archive exists but size does not match catalog.";
        }

        if (!archiveSha256Matches)
        {
            return "llama.cpp binary archive exists but sha256 does not match catalog.";
        }

        return executableExists
            ? "llama.cpp binary is installed and verified."
            : "llama.cpp binary archive is verified but executable is missing.";
    }

    private static string CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return RuntimeInformation.OSDescription;
    }

    private static string CurrentArchitecture()
        => RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };

    private static bool IsPathInside(string directory, string path)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(path);
        var normalizedDirectory = fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(LlamaCppBinaryArtifact binary)
        => $"{binary.Version}/{binary.Flavor}/{binary.Platform}/{binary.Architecture}";

    private static string ToHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
