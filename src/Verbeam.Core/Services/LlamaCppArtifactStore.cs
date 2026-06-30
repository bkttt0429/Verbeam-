using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class LlamaCppArtifactStore
{
    private readonly LlamaCppOptions _options;
    private readonly ModelCatalogService _catalogs;
    private readonly string _contentRootPath;
    private readonly HttpClient _httpClient;
    private readonly LlamaCppDownloadTracker _downloads;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LlamaCppArtifactStore(
        LlamaCppOptions options,
        ModelCatalogService catalogs,
        string contentRootPath,
        HttpClient httpClient,
        LlamaCppDownloadTracker? downloads = null)
    {
        _options = options;
        _catalogs = catalogs;
        _contentRootPath = contentRootPath;
        _httpClient = httpClient;
        _downloads = downloads ?? new LlamaCppDownloadTracker();
    }

    public async Task<IReadOnlyList<LlamaCppArtifactStatus>> GetStatusesAsync(
        CancellationToken cancellationToken = default)
        => await GetStatusesAsync(verifySha256: true, cancellationToken);

    public async Task<IReadOnlyList<LlamaCppArtifactStatus>> GetStatusesAsync(
        bool verifySha256,
        CancellationToken cancellationToken = default)
    {
        var models = _catalogs.GetCurrent().Models
            .Where(model => model.Runtimes.LlamaCpp is not null && model.Artifact is not null)
            .OrderBy(model => model.Rank)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var statuses = new List<LlamaCppArtifactStatus>(models.Length);
        foreach (var model in models)
        {
            statuses.Add(await GetStatusAsync(model, verifySha256, cancellationToken));
        }

        return statuses;
    }

    public Task<LlamaCppArtifactStatus> GetStatusAsync(
        string modelId,
        CancellationToken cancellationToken = default)
        => GetStatusAsync(FindModel(modelId), verifySha256: true, cancellationToken);

    public async Task<LlamaCppArtifactDownloadResult> EnsureModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var model = FindModel(modelId);
            var status = await GetStatusAsync(model, verifySha256: true, cancellationToken);
            if (status.Verified)
            {
                return new LlamaCppArtifactDownloadResult(
                    model.Id,
                    status.LocalPath,
                    Verified: true,
                    status.ActualSizeBytes ?? 0,
                    status.ActualSha256,
                    "Model artifact is already verified.");
            }

            var artifact = RequireArtifact(model);
            var imported = await TryImportLocalArtifactAsync(model, cancellationToken);
            if (imported is not null)
            {
                return imported;
            }

            if (string.IsNullOrWhiteSpace(artifact.DownloadUrl))
            {
                throw new InvalidOperationException($"Model '{model.Id}' does not declare an artifact downloadUrl and no verified local artifact was found.");
            }

            var localPath = ResolveLocalPath(model);
            var directory = Path.GetDirectoryName(localPath)
                ?? throw new InvalidOperationException($"Could not resolve artifact directory for model '{model.Id}'.");
            Directory.CreateDirectory(directory);

            var tempPath = localPath + ".download";

            try
            {
                var result = await DownloadToTempAsync(model, artifact, tempPath, cancellationToken);
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }

                File.Move(tempPath, localPath);
                return result with
                {
                    LocalPath = localPath,
                    Verified = true,
                    Message = "Model artifact downloaded and verified."
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

    private async Task<LlamaCppArtifactDownloadResult?> TryImportLocalArtifactAsync(
        ModelCatalogEntry model,
        CancellationToken cancellationToken)
    {
        var artifact = RequireArtifact(model);
        foreach (var sourcePath in ResolveLocalSourcePaths(artifact))
        {
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var sourceInfo = new FileInfo(sourcePath);
            if (sourceInfo.Length != artifact.SizeBytes)
            {
                continue;
            }

            var targetPath = ResolveLocalPath(model);
            if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                var sourceSha = await ComputeSha256Async(sourcePath, cancellationToken);
                if (!sourceSha.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new LlamaCppArtifactDownloadResult(
                    model.Id,
                    targetPath,
                    Verified: true,
                    sourceInfo.Length,
                    sourceSha,
                    "Model artifact is already verified.");
            }

            var directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException($"Could not resolve artifact directory for model '{model.Id}'.");
            Directory.CreateDirectory(directory);
            var tempPath = targetPath + ".import";

            try
            {
                var importedSha = await CopyAndHashAsync(sourcePath, tempPath, cancellationToken);
                var importedBytes = new FileInfo(tempPath).Length;
                if (importedBytes != artifact.SizeBytes ||
                    !importedSha.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteIfExists(tempPath);
                    continue;
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
                return new LlamaCppArtifactDownloadResult(
                    model.Id,
                    targetPath,
                    Verified: true,
                    importedBytes,
                    importedSha,
                    $"Model artifact imported from local path: {sourcePath}");
            }
            catch
            {
                DeleteIfExists(tempPath);
                throw;
            }
        }

        return null;
    }

    private async Task<LlamaCppArtifactDownloadResult> DownloadToTempAsync(
        ModelCatalogEntry model,
        ModelArtifact artifact,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (existingBytes >= artifact.SizeBytes)
        {
            DeleteIfExists(tempPath);
            existingBytes = 0;
        }

        var session = _downloads.Begin("model", model.Id, artifact.SizeBytes, existingBytes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.PauseToken);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUrl);
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
            if (bytesWritten != artifact.SizeBytes)
            {
                throw new InvalidDataException(
                    $"Downloaded artifact size mismatch for '{model.Id}': expected {artifact.SizeBytes}, got {bytesWritten}.");
            }

            if (!sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded artifact sha256 mismatch for '{model.Id}': expected {artifact.Sha256}, got {sha256}.");
            }

            session.Complete("Model artifact downloaded and verified.");
            return new LlamaCppArtifactDownloadResult(
                model.Id,
                tempPath,
                Verified: true,
                bytesWritten,
                sha256,
                "Model artifact downloaded and verified.");
        }
        catch (OperationCanceledException) when (session.PauseToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            session.Pause("Download paused. Resume to continue from the partial file.");
            throw new LlamaCppDownloadPausedException($"Download paused for model '{model.Id}'.");
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

    private IEnumerable<string> ResolveLocalSourcePaths(ModelArtifact artifact)
    {
        foreach (var localPath in artifact.LocalPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                continue;
            }

            var value = localPath.Trim();
            yield return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : PathResolver.Resolve(_contentRootPath, value));
        }
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        await using var output = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        return ToHex(hash.GetHashAndReset());
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

    private async Task<LlamaCppArtifactStatus> GetStatusAsync(
        ModelCatalogEntry model,
        bool verifySha256,
        CancellationToken cancellationToken)
    {
        var artifact = RequireArtifact(model);
        var localPath = ResolveStatusPath(model, artifact);
        var exists = File.Exists(localPath);
        long? actualSizeBytes = null;
        var actualSha256 = string.Empty;
        var sizeMatches = false;
        var sha256Matches = false;

        if (exists)
        {
            var info = new FileInfo(localPath);
            actualSizeBytes = info.Length;
            sizeMatches = actualSizeBytes == artifact.SizeBytes;
            if (sizeMatches && verifySha256)
            {
                actualSha256 = await ComputeSha256Async(localPath, cancellationToken);
                sha256Matches = actualSha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        var verified = exists && sizeMatches && sha256Matches;
        return new LlamaCppArtifactStatus(
            model.Id,
            model.Runtimes.LlamaCpp?.ModelAlias ?? model.Id,
            model.DisplayName,
            artifact.Filename,
            localPath,
            artifact.DownloadUrl,
            artifact.SizeBytes,
            artifact.Sha256,
            exists,
            sizeMatches,
            sha256Matches,
            verified,
            actualSizeBytes,
            actualSha256,
            BuildStatusMessage(exists, sizeMatches, sha256Matches));
    }

    private string ResolveStatusPath(ModelCatalogEntry model, ModelArtifact artifact)
    {
        var defaultPath = ResolveLocalPath(model);
        var candidates = new[] { defaultPath }
            .Concat(ResolveLocalSourcePaths(artifact))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in candidates)
        {
            if (File.Exists(path) && new FileInfo(path).Length == artifact.SizeBytes)
            {
                return path;
            }
        }

        return candidates.FirstOrDefault(File.Exists) ?? defaultPath;
    }

    private ModelCatalogEntry FindModel(string modelId)
    {
        var requested = string.IsNullOrWhiteSpace(modelId)
            ? _options.Model
            : modelId.Trim();
        var model = _catalogs.GetCurrent().Models.FirstOrDefault(item =>
            item.Runtimes.LlamaCpp is not null &&
            (item.Id.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
             item.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
             item.Runtimes.LlamaCpp.ModelAlias.Equals(requested, StringComparison.OrdinalIgnoreCase)));

        return model ?? throw new InvalidOperationException($"Model '{requested}' does not declare a llama.cpp artifact.");
    }

    private ModelArtifact RequireArtifact(ModelCatalogEntry model)
        => model.Artifact
            ?? throw new InvalidOperationException($"Model '{model.Id}' does not declare a GGUF artifact.");

    public string ResolveLocalPath(ModelCatalogEntry model)
    {
        var artifact = RequireArtifact(model);
        if (string.IsNullOrWhiteSpace(artifact.Filename))
        {
            throw new InvalidOperationException($"Model '{model.Id}' does not declare a GGUF artifact filename.");
        }

        var modelsDirectory = Path.GetFullPath(PathResolver.Resolve(_contentRootPath, _options.ModelsDirectory));
        var localPath = Path.GetFullPath(Path.Combine(modelsDirectory, artifact.Filename));
        if (!IsPathInside(modelsDirectory, localPath))
        {
            throw new InvalidOperationException($"Model '{model.Id}' artifact filename resolves outside models directory.");
        }

        return localPath;
    }

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
        bool exists,
        bool sizeMatches,
        bool sha256Matches)
    {
        if (!exists)
        {
            return "Model artifact is not installed.";
        }

        if (!sizeMatches)
        {
            return "Model artifact exists but size does not match catalog.";
        }

        return sha256Matches
            ? "Model artifact is installed and verified."
            : "Model artifact exists but sha256 does not match catalog.";
    }

    private static bool IsPathInside(string directory, string path)
    {
        var normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(directory, StringComparison.OrdinalIgnoreCase);
    }

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
