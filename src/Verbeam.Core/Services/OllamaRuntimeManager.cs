using System.Diagnostics;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class OllamaRuntimeManager : IDisposable
{
    private readonly OllamaOptions _options;
    private readonly string _contentRootPath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _isReady;
    private bool _startedByVerbeam;
    private string _resolvedExecutablePath = string.Empty;
    private string _resolvedModelsDirectory = string.Empty;
    private string _lastError = string.Empty;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastProbeAt;

    public OllamaRuntimeManager(
        OllamaOptions options,
        string contentRootPath,
        HttpClient httpClient)
    {
        _options = options;
        _contentRootPath = contentRootPath;
        _httpClient = httpClient;
    }

    public OllamaRuntimeStatus GetStatus()
        => new(
            _options.BaseUrl,
            _options.AutoStart,
            _isReady,
            _startedByVerbeam,
            _resolvedExecutablePath,
            _resolvedModelsDirectory,
            _startedAt,
            _lastProbeAt,
            _lastError);

    public async Task<OllamaRuntimeStatus> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await ProbeAsync(cancellationToken))
            {
                _lastError = string.Empty;
                return GetStatus();
            }

            if (!_options.AutoStart)
            {
                _lastError = "Ollama auto-start is disabled.";
                return GetStatus();
            }

            if (!IsLocalBaseUrl(_options.BaseUrl))
            {
                _lastError = $"Ollama auto-start only supports localhost base URLs: {_options.BaseUrl}";
                return GetStatus();
            }

            _resolvedExecutablePath = ResolveExecutablePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = _resolvedExecutablePath,
                WorkingDirectory = _contentRootPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.Environment["OLLAMA_HOST"] = NormalizeBaseUrl(_options.BaseUrl).GetLeftPart(UriPartial.Authority);
            ConfigureModelsDirectory(startInfo);

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                _lastError = $"Could not start Ollama via {_resolvedExecutablePath}.";
                return GetStatus();
            }

            _startedByVerbeam = true;
            _startedAt = DateTimeOffset.UtcNow;
            await WaitForReadyAsync(cancellationToken);
            return GetStatus();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _isReady = false;
            _lastError = ex.Message;
            return GetStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process?.Dispose();
        }
    }

    public static IReadOnlyList<string> BuildExecutableCandidates(
        string contentRootPath,
        string? configuredPath,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(ResolveConfiguredPath(contentRootPath, configuredPath.Trim()));
        }

        candidates.AddRange(BuildPathCandidates("ollama", environment));

        AddKnownWindowsPath(candidates, environment, "LOCALAPPDATA", "Programs", "Ollama", "ollama.exe");
        AddKnownWindowsPath(candidates, environment, "LOCALAPPDATA", "Ollama", "ollama.exe");
        AddKnownWindowsPath(candidates, environment, "APPDATA", "Ollama", "ollama.exe");
        AddKnownWindowsPath(candidates, environment, "ProgramFiles", "Ollama", "ollama.exe");
        AddKnownWindowsPath(candidates, environment, "ProgramFiles(x86)", "Ollama", "ollama.exe");

        return candidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(_options.StartupTimeoutSeconds, 1, 120));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                _isReady = false;
                _lastError = $"Ollama exited before it became ready (exit code {_process.ExitCode}).";
                return;
            }

            if (await ProbeAsync(cancellationToken))
            {
                _lastError = string.Empty;
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        _lastError = $"Ollama did not become ready at {_options.BaseUrl} within {_options.StartupTimeoutSeconds} seconds.";
    }

    private void ConfigureModelsDirectory(ProcessStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelsDirectory))
        {
            _resolvedModelsDirectory = startInfo.Environment.TryGetValue("OLLAMA_MODELS", out var inherited)
                ? inherited ?? string.Empty
                : string.Empty;
            return;
        }

        var modelsDirectory = Path.GetFullPath(PathResolver.Resolve(_contentRootPath, _options.ModelsDirectory.Trim()));
        Directory.CreateDirectory(modelsDirectory);
        startInfo.Environment["OLLAMA_MODELS"] = modelsDirectory;
        _resolvedModelsDirectory = modelsDirectory;
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        _lastProbeAt = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ModelDiscoveryTimeoutSeconds, 1, 10)));
            using var response = await _httpClient.GetAsync(BuildTagsEndpoint(_options.BaseUrl), timeout.Token);
            _isReady = response.IsSuccessStatusCode;
            return _isReady;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _isReady = false;
            return false;
        }
        catch (HttpRequestException)
        {
            _isReady = false;
            return false;
        }
    }

    private string ResolveExecutablePath()
    {
        foreach (var candidate in BuildExecutableCandidates(_contentRootPath, _options.ExecutablePath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Ollama executable was not found. Install Ollama or set Verbeam:Ollama:ExecutablePath.",
            string.IsNullOrWhiteSpace(_options.ExecutablePath) ? "ollama" : _options.ExecutablePath);
    }

    private static Uri BuildTagsEndpoint(string baseUrl)
        => new(NormalizeBaseUrl(baseUrl), "api/tags");

    private static Uri NormalizeBaseUrl(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://localhost:11434/"
            : baseUrl.TrimEnd('/') + "/";
        return new Uri(normalized);
    }

    private static bool IsLocalBaseUrl(string baseUrl)
    {
        var uri = NormalizeBaseUrl(baseUrl);
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveConfiguredPath(string contentRootPath, string configuredPath)
    {
        if (LooksLikePath(configuredPath))
        {
            return PathResolver.Resolve(contentRootPath, configuredPath);
        }

        return configuredPath;
    }

    private static bool LooksLikePath(string value)
        => Path.IsPathRooted(value) ||
           value.Contains(Path.DirectorySeparatorChar) ||
           value.Contains(Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> BuildPathCandidates(
        string commandName,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (!environment.TryGetValue("PATH", out var pathValue) || string.IsNullOrWhiteSpace(pathValue))
        {
            return [];
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : [string.Empty];
        return pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, commandName + extension)));
    }

    private static void AddKnownWindowsPath(
        List<string> candidates,
        IReadOnlyDictionary<string, string?> environment,
        string environmentKey,
        params string[] parts)
    {
        if (!environment.TryGetValue(environmentKey, out var root) || string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        candidates.Add(Path.Combine([root, .. parts]));
    }
}

public sealed record OllamaRuntimeStatus(
    string BaseUrl,
    bool AutoStart,
    bool IsReady,
    bool StartedByVerbeam,
    string ExecutablePath,
    string ModelsDirectory,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastProbeAt,
    string LastError);
