using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class LlamaCppRuntimeManager : IDisposable
{
    // The first managed inference builds the compute graph and compiles Vulkan
    // shaders, a one-time cost that can exceed the per-request timeout. Warmup
    // pays it up front with this generous budget.
    private const int WarmupTimeoutSeconds = 120;

    private readonly LlamaCppOptions _options;
    private readonly ModelCatalogService _catalogs;
    private readonly LlamaCppArtifactStore? _artifacts;
    private readonly LlamaCppBinaryStore? _binaries;
    private readonly string _contentRootPath;
    private readonly HttpClient _healthClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private Process? _process;
    private WindowsJobObject? _job;
    private int? _port;
    private string _runningModelAlias = string.Empty;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _lastUsedAt = DateTimeOffset.UtcNow;
    private bool _cachePromptEnabled = true;
    private string _lastError = string.Empty;

    public LlamaCppRuntimeManager(
        LlamaCppOptions options,
        ModelCatalogService catalogs,
        string contentRootPath,
        HttpClient healthClient)
        : this(options, catalogs, contentRootPath, healthClient, artifactStore: null, binaryStore: null)
    {
    }

    public LlamaCppRuntimeManager(
        LlamaCppOptions options,
        ModelCatalogService catalogs,
        string contentRootPath,
        HttpClient healthClient,
        LlamaCppArtifactStore? artifactStore)
        : this(options, catalogs, contentRootPath, healthClient, artifactStore, binaryStore: null)
    {
    }

    public LlamaCppRuntimeManager(
        LlamaCppOptions options,
        ModelCatalogService catalogs,
        string contentRootPath,
        HttpClient healthClient,
        LlamaCppArtifactStore? artifactStore,
        LlamaCppBinaryStore? binaryStore)
    {
        _options = options;
        _catalogs = catalogs;
        _artifacts = artifactStore;
        _binaries = binaryStore;
        _contentRootPath = contentRootPath;
        _healthClient = healthClient;
        _healthClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.StartupTimeoutSeconds, 1, 120));
        _idleTimer = new Timer(
            _ => _ = StopIfIdleAsync(),
            null,
            TimeSpan.FromSeconds(Math.Clamp(options.IdleTimeoutSeconds, 30, 3600)),
            TimeSpan.FromSeconds(Math.Clamp(options.IdleTimeoutSeconds, 30, 3600)));
    }

    public LlamaCppRuntimeStatus GetStatus()
        => new(
            _options.Mode,
            NormalizeOpenAiBaseUrl(_options.BaseUrl).ToString(),
            _process is { HasExited: false },
            _port,
            _runningModelAlias,
            _startedAt,
            _lastUsedAt,
            _cachePromptEnabled,
            _lastError);

    public async Task<LlamaCppRuntimeEndpoint> GetEndpointAsync(
        string requestedModel,
        CancellationToken cancellationToken = default)
    {
        _lastUsedAt = DateTimeOffset.UtcNow;
        var model = FindCatalogModel(requestedModel);
        var runtime = model?.Runtimes.LlamaCpp;
        var sampling = runtime?.Sampling ?? new ModelLlamaCppSampling
        {
            TimeoutSeconds = Math.Clamp(_options.RequestTimeoutSeconds, 1, 600)
        };
        var modelAlias = Pick(runtime?.ModelAlias, Pick(requestedModel, _options.Model));

        if (!_options.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase))
        {
            return new LlamaCppRuntimeEndpoint(
                NormalizeOpenAiBaseUrl(_options.BaseUrl),
                modelAlias,
                sampling,
                _cachePromptEnabled && sampling.CachePrompt);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Managed llama.cpp mode is currently supported on Windows only. Use remote mode on this platform.");
        }

        if (model is null || runtime is null)
        {
            throw new InvalidOperationException($"Model '{requestedModel}' does not declare a llama.cpp runtime profile.");
        }

        await EnsureManagedServerAsync(model, runtime, modelAlias, cancellationToken);
        return new LlamaCppRuntimeEndpoint(
            NormalizeOpenAiBaseUrl($"http://127.0.0.1:{_port}/v1"),
            modelAlias,
            sampling,
            _cachePromptEnabled && sampling.CachePrompt);
    }

    /// <summary>
    /// Starts the managed server (if configured) and runs one throwaway inference
    /// so the cold compute-graph / Vulkan-shader compilation is paid here instead
    /// of cancelling the user's first real translation. No-op in remote mode.
    /// Best-effort: failures are recorded in status, not thrown.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase) ||
            !OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = await GetEndpointAsync(_options.Model, cancellationToken);

        using var warmupClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(WarmupTimeoutSeconds)
        };
        var payload = new
        {
            model = endpoint.ModelAlias,
            stream = false,
            max_tokens = 1,
            temperature = 0,
            messages = new[] { new { role = "user", content = "ping" } }
        };

        try
        {
            using var response = await warmupClient.PostAsJsonAsync(
                new Uri(endpoint.OpenAiBaseUrl, "chat/completions"),
                payload,
                cancellationToken);
            // The response content is irrelevant; the request alone forces the
            // server to build the compute graph and compile Vulkan shaders.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _lastError = $"llama.cpp warmup did not complete: {ex.Message}";
        }
    }

    public void RecordCachePromptRejected(string message)
    {
        _cachePromptEnabled = false;
        _lastError = string.IsNullOrWhiteSpace(message)
            ? "llama.cpp endpoint rejected cache_prompt; future requests omit it."
            : message;
    }

    public static IReadOnlyList<string> BuildManagedArguments(
        ModelCatalogEntry model,
        ModelLlamaCppRuntime runtime,
        ModelLlamaCppProfile profile,
        string modelPath,
        string modelAlias,
        int port,
        string slotSavePath)
    {
        var values = new List<string>
        {
            "--model", modelPath,
            "--alias", modelAlias,
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "--ctx-size", profile.ContextSize.ToString(),
            "--parallel", profile.Parallel.ToString(),
            "--n-gpu-layers", profile.GpuLayers.ToString(),
            "--batch-size", profile.BatchSize.ToString(),
            "--ubatch-size", profile.MicroBatchSize.ToString(),
            "--slot-save-path", slotSavePath
        };

        if (!string.IsNullOrWhiteSpace(profile.CacheTypeK))
        {
            values.Add("--cache-type-k");
            values.Add(profile.CacheTypeK);
        }

        if (!string.IsNullOrWhiteSpace(profile.CacheTypeV))
        {
            values.Add("--cache-type-v");
            values.Add(profile.CacheTypeV);
        }

        if (profile.FlashAttention == true)
        {
            // b9590's --flash-attn takes a value (on|off|auto); a bare flag makes
            // the next argument be parsed as its value and the server aborts.
            values.Add("--flash-attn");
            values.Add("on");
        }

        if (profile.CacheReuse is not null)
        {
            values.Add("--cache-reuse");
            values.Add(profile.CacheReuse.Value.ToString());
        }

        if (profile.Fit is not null)
        {
            values.Add("--fit");
            values.Add(profile.Fit.Value ? "on" : "off");
        }

        if (!string.IsNullOrWhiteSpace(profile.Reasoning))
        {
            values.Add("--reasoning");
            values.Add(profile.Reasoning.Trim());
        }

        if (values.Any(value => value.Equals("auto", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"llama.cpp managed profile cannot contain auto values: {model.Id}/{profile.Name}");
        }

        return values;
    }

    /// <summary>
    /// Computes the GPU-selection environment variable for the active backend by
    /// running `llama-server --list-devices` and picking the discrete GPU. Returns
    /// empty for CPU/Metal (no selection var) or when detection fails — startup
    /// then falls back to the backend's own default device rather than aborting.
    /// </summary>
    private IReadOnlyDictionary<string, string> ResolveDeviceEnvironment(string executablePath)
    {
        var flavor = _binaries?.ResolveEffectiveFlavor() ?? _options.BinaryFlavor.Trim();
        var envKey = LlamaCppBackendResolver.DeviceEnvKeyForFlavor(flavor);
        if (envKey is null)
        {
            return new Dictionary<string, string>();
        }

        // Explicit override wins over auto device selection.
        if (_options.DeviceIndex is { } forced && forced >= 0)
        {
            return new Dictionary<string, string> { [envKey] = forced.ToString() };
        }

        var listed = LlamaCppBackendResolver.ParseListDevices(RunListDevices(executablePath));
        // "integrated" mode pins the iGPU (leaving the discrete card for a game/video);
        // every other target pins the discrete GPU as before.
        var integrated = (_options.ComputeTarget ?? "auto").Trim()
            .Equals("integrated", StringComparison.OrdinalIgnoreCase);
        var index = integrated
            ? LlamaCppBackendResolver.PickIntegratedDeviceIndex(listed)
            : LlamaCppBackendResolver.PickDeviceIndex(listed);
        return index is { } chosen
            ? new Dictionary<string, string> { [envKey] = chosen.ToString() }
            : new Dictionary<string, string>();
    }

    private static string? RunListDevices(string executablePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--list-devices",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            return output;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<string, string> BuildManagedEnvironment(ModelLlamaCppProfile profile)
    {
        return (profile.Environment ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        StopProcess();
        _gate.Dispose();
        _healthClient.Dispose();
    }

    private async Task EnsureManagedServerAsync(
        ModelCatalogEntry model,
        ModelLlamaCppRuntime runtime,
        string modelAlias,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } &&
                _runningModelAlias.Equals(modelAlias, StringComparison.OrdinalIgnoreCase) &&
                _port is not null)
            {
                return;
            }

            StopProcess();
            var profile = PickProfile(runtime);
            var modelPath = await ResolveManagedModelPathAsync(model, cancellationToken);

            var executablePath = await ResolveExecutablePathAsync(cancellationToken);
            var slotSavePath = PathResolver.Resolve(_contentRootPath, _options.SlotSaveDirectory);
            Directory.CreateDirectory(slotSavePath);
            var port = FindAvailablePort(_options.PortStart, _options.PortEnd);
            var arguments = BuildManagedArguments(model, runtime, profile, modelPath, modelAlias, port, slotSavePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = _contentRootPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var pair in BuildManagedEnvironment(profile))
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            // Pin the right GPU for the active backend. Each backend enumerates
            // devices differently (Vulkan lists the iGPU + virtual displays so the
            // discrete card may be index 1; CUDA lists only NVIDIA at 0), so we ask
            // the binary itself via --list-devices and pick the discrete GPU. This
            // overrides any static device env from the profile.
            foreach (var pair in ResolveDeviceEnvironment(executablePath))
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start llama-server.");
            _job = WindowsJobObject.TryAttach(process);
            _process = process;
            _port = port;
            _runningModelAlias = modelAlias;
            _startedAt = DateTimeOffset.UtcNow;
            _lastError = string.Empty;
            await WaitForHealthAsync(port, cancellationToken);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            StopProcess();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopIfIdleAsync()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        if (!ShouldStopForIdle(DateTimeOffset.UtcNow, _lastUsedAt, _options.IdleTimeoutSeconds))
        {
            return;
        }

        await StopAsync();
    }

    private async Task WaitForHealthAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.StartupTimeoutSeconds, 1, 120)));
        var healthUri = new Uri($"http://127.0.0.1:{port}/health");

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var response = await _healthClient.GetAsync(healthUri, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (timeout.IsCancellationRequested)
                {
                    break;
                }
            }

            await Task.Delay(250, timeout.Token);
        }

        throw new TimeoutException($"llama-server did not become healthy on port {port}.");
    }

    private ModelCatalogEntry? FindCatalogModel(string requestedModel)
    {
        var model = Pick(requestedModel, _options.Model);
        return _catalogs.GetCurrent().Models.FirstOrDefault(item =>
            item.Id.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            item.Runtimes.LlamaCpp?.ModelAlias.Equals(model, StringComparison.OrdinalIgnoreCase) == true) ??
            _catalogs.GetCurrent().Models.FirstOrDefault(item => item.Runtimes.LlamaCpp is not null);
    }

    private ModelLlamaCppProfile PickProfile(ModelLlamaCppRuntime runtime)
        => runtime.Profiles.FirstOrDefault(profile => profile.Name.Equals(_options.Profile, StringComparison.OrdinalIgnoreCase))
            ?? runtime.Profiles.First();

    private string ResolveModelPath(ModelCatalogEntry model)
    {
        if (model.Artifact is null || string.IsNullOrWhiteSpace(model.Artifact.Filename))
        {
            throw new InvalidOperationException($"Model '{model.Id}' does not declare a GGUF artifact filename.");
        }

        var modelsDirectory = PathResolver.Resolve(_contentRootPath, _options.ModelsDirectory);
        return Path.GetFullPath(Path.Combine(modelsDirectory, model.Artifact.Filename));
    }

    private async Task<string> ResolveManagedModelPathAsync(
        ModelCatalogEntry model,
        CancellationToken cancellationToken)
    {
        if (_artifacts is not null)
        {
            var result = await _artifacts.EnsureModelAsync(model.Id, cancellationToken);
            if (result.Verified && File.Exists(result.LocalPath))
            {
                return result.LocalPath;
            }

            throw new FileNotFoundException($"llama.cpp GGUF model file was not verified: {result.LocalPath}", result.LocalPath);
        }

        var modelPath = ResolveModelPath(model);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"llama.cpp GGUF model file was not found: {modelPath}", modelPath);
        }

        return modelPath;
    }

    private async Task<string> ResolveExecutablePathAsync(CancellationToken cancellationToken)
    {
        if (_binaries is not null)
        {
            return await _binaries.ResolveExecutablePathAsync(cancellationToken);
        }

        return Path.IsPathRooted(_options.ExecutablePath) || File.Exists(PathResolver.Resolve(_contentRootPath, _options.ExecutablePath))
            ? PathResolver.Resolve(_contentRootPath, _options.ExecutablePath)
            : _options.ExecutablePath;
    }

    public static bool ShouldStopForIdle(
        DateTimeOffset now,
        DateTimeOffset lastUsedAt,
        int idleTimeoutSeconds)
        => now - lastUsedAt >= TimeSpan.FromSeconds(Math.Clamp(idleTimeoutSeconds, 30, 3600));

    public static int FindAvailablePort(int start, int end)
    {
        for (var port = Math.Min(start, end); port <= Math.Max(start, end); port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                continue;
            }
        }

        throw new InvalidOperationException($"No available llama.cpp port in range {start}-{end}.");
    }

    private static Uri NormalizeOpenAiBaseUrl(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        if (!normalized.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "v1/";
        }

        return new Uri(normalized);
    }

    private void StopProcess()
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
            _job?.Dispose();
            _job = null;
            _process?.Dispose();
            _process = null;
            _port = null;
            _runningModelAlias = string.Empty;
            _startedAt = null;
        }
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class WindowsJobObject : IDisposable
    {
        private const int JobObjectInfoClassExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private readonly IntPtr _handle;

        private WindowsJobObject(IntPtr handle)
        {
            _handle = handle;
        }

        public static WindowsJobObject? TryAttach(Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var job = new WindowsJobObject(handle);
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectInfoClassExtendedLimitInformation, pointer, (uint)length) ||
                    !AssignProcessToJobObject(handle, process.Handle))
                {
                    job.Dispose();
                    return null;
                }

                return job;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int infoClass,
            IntPtr jobObjectInfo,
            uint jobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
