using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class LlamaCppInstallService
{
    private readonly VerbeamOptions _options;
    private readonly ModelCatalogService _catalogs;
    private readonly LlamaCppArtifactStore _artifacts;
    private readonly LlamaCppBinaryStore _binaries;
    private readonly LlamaCppRuntimeManager _runtime;
    private readonly LlamaCppRuntimeSettingsStore? _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LlamaCppInstallService(
        VerbeamOptions options,
        ModelCatalogService catalogs,
        LlamaCppArtifactStore artifacts,
        LlamaCppBinaryStore binaries,
        LlamaCppRuntimeManager runtime,
        LlamaCppRuntimeSettingsStore? settings = null)
    {
        _options = options;
        _catalogs = catalogs;
        _artifacts = artifacts;
        _binaries = binaries;
        _runtime = runtime;
        _settings = settings;
    }

    public async Task<LlamaCppInstallResult> InstallAndUseAsync(
        LlamaCppInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var model = FindModel(request.ModelId);
            var runtime = model.Runtimes.LlamaCpp
                ?? throw new InvalidOperationException($"Model '{model.Id}' does not declare a llama.cpp runtime.");
            var mode = NormalizeMode(request.Mode);
            var modelAlias = Pick(runtime.ModelAlias, model.Id);

            ConfigureCurrentSession(mode, modelAlias, runtime, request.UseForCurrentSession);

            LlamaCppArtifactDownloadResult? artifact = null;
            LlamaCppBinaryDownloadResult? binary = null;

            if (mode.Equals("managed", StringComparison.OrdinalIgnoreCase))
            {
                (artifact, binary) = await InstallManagedAsync(model, runtime, modelAlias, request.StartServer, cancellationToken);
            }
            else if (request.StartServer)
            {
                await _runtime.GetEndpointAsync(modelAlias, cancellationToken);
            }

            if (_settings is not null)
            {
                // Saved only after the install succeeded, so a failed managed
                // install never persists a mode the runtime cannot serve.
                await _settings.SaveAsync(_options.LlamaCpp, cancellationToken);
            }

            var status = _runtime.GetStatus();
            var ready = mode.Equals("managed", StringComparison.OrdinalIgnoreCase)
                ? artifact?.Verified == true &&
                  binary?.Ready == true &&
                  (!request.StartServer || status.IsManagedRunning)
                : true;

            return new LlamaCppInstallResult(
                "llama-cpp",
                model.Id,
                modelAlias,
                model.DisplayName,
                mode,
                request.UseForCurrentSession,
                request.StartServer && status.IsManagedRunning,
                ready,
                artifact,
                binary,
                status,
                BuildMessage(mode, request.StartServer, ready));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(LlamaCppArtifactDownloadResult Artifact, LlamaCppBinaryDownloadResult Binary)> InstallManagedAsync(
        ModelCatalogEntry model,
        ModelLlamaCppRuntime runtime,
        string modelAlias,
        bool startServer,
        CancellationToken cancellationToken)
    {
        if (startServer && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Managed llama.cpp mode is currently supported on Windows only. Use remote mode on this platform.");
        }

        // Under "auto", keep "auto" persisted so the backend is re-resolved each
        // boot rather than frozen to the concrete flavor we happened to try first.
        // An explicit flavor keeps the old behavior of recording the winning flavor.
        var originalFlavor = _options.LlamaCpp.BinaryFlavor;
        var keepAuto = originalFlavor.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
        var flavors = BuildManagedFlavorChain(runtime);
        var errors = new List<string>();
        for (var index = 0; index < flavors.Count; index++)
        {
            var flavor = flavors[index];
            _options.LlamaCpp.BinaryFlavor = flavor;
            try
            {
                var binary = await _binaries.EnsureBinaryAsync(cancellationToken);
                var artifact = await _artifacts.EnsureModelAsync(model.Id, cancellationToken);
                if (startServer)
                {
                    await _runtime.GetEndpointAsync(modelAlias, cancellationToken);
                }

                if (keepAuto)
                {
                    _options.LlamaCpp.BinaryFlavor = originalFlavor;
                }

                return (artifact, binary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{flavor}: {ex.Message}");
                await _runtime.StopAsync(cancellationToken);
            }
        }

        if (keepAuto)
        {
            _options.LlamaCpp.BinaryFlavor = originalFlavor;
        }

        var fallbackHint = runtime.RecommendedFallback.FirstOrDefault(item =>
            item.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase));
        var hint = string.IsNullOrWhiteSpace(fallbackHint)
            ? string.Empty
            : $" Next explicit fallback is {fallbackHint}.";
        throw new InvalidOperationException(
            $"Failed to install or start managed llama.cpp via {string.Join(" -> ", flavors)}.{hint} Attempts: {string.Join(" | ", errors)}");
    }

    private IReadOnlyList<string> BuildManagedFlavorChain(ModelLlamaCppRuntime runtime)
    {
        // Under "auto", try the hardware-resolved preference order (e.g. cuda ->
        // vulkan -> cpu) so Install-and-Use downloads the best available backend.
        // An explicit flavor keeps the model's declared default.
        var flavors = _options.LlamaCpp.BinaryFlavor.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? new List<string>(_binaries.GetFlavorPreferences())
            : new List<string> { Pick(runtime.BinaryFlavor, _options.LlamaCpp.BinaryFlavor) };

        foreach (var fallback in runtime.RecommendedFallback)
        {
            if (fallback.Equals("llama-cpp:managed-cpu", StringComparison.OrdinalIgnoreCase) ||
                fallback.Equals("llama-cpp:cpu", StringComparison.OrdinalIgnoreCase))
            {
                flavors.Add("cpu");
            }
        }

        return flavors
            .Where(flavor => !string.IsNullOrWhiteSpace(flavor))
            .Select(flavor => flavor.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private ModelCatalogEntry FindModel(string? modelId)
    {
        var hasExplicitModel = !string.IsNullOrWhiteSpace(modelId);
        var requested = Pick(modelId, _options.LlamaCpp.Model);
        var models = _catalogs.GetCurrent().Models
            .Where(model => model.Runtimes.LlamaCpp is not null)
            .ToArray();
        var model = models.FirstOrDefault(item =>
            item.Id.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            item.Runtimes.LlamaCpp!.ModelAlias.Equals(requested, StringComparison.OrdinalIgnoreCase));

        if (model is not null)
        {
            return model;
        }

        if (hasExplicitModel)
        {
            throw new InvalidOperationException($"Model '{requested}' does not declare a llama.cpp runtime.");
        }

        return models.FirstOrDefault()
            ?? throw new InvalidOperationException("No llama.cpp model runtime is available in the model catalog.");
    }

    private string NormalizeMode(string? mode)
    {
        var normalized = Pick(mode, _options.LlamaCpp.Mode).ToLowerInvariant();
        if (normalized is not ("remote" or "managed"))
        {
            throw new InvalidOperationException("llama.cpp mode must be 'remote' or 'managed'.");
        }

        return normalized;
    }

    private void ConfigureCurrentSession(
        string mode,
        string modelAlias,
        ModelLlamaCppRuntime runtime,
        bool useForCurrentSession)
    {
        _options.LlamaCpp.Mode = mode;
        _options.LlamaCpp.Model = modelAlias;

        if (!string.IsNullOrWhiteSpace(runtime.MinLlamaCppVersion))
        {
            _options.LlamaCpp.PinnedVersion = runtime.MinLlamaCppVersion.Trim();
        }

        // When the global flavor is "auto", leave it intact so hardware detection
        // keeps driving the choice; only an explicit config takes the per-model
        // default. Otherwise auto would be silently clobbered to the model's
        // declared flavor on every Install-and-Use.
        if (!string.IsNullOrWhiteSpace(runtime.BinaryFlavor) &&
            !_options.LlamaCpp.BinaryFlavor.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _options.LlamaCpp.BinaryFlavor = runtime.BinaryFlavor.Trim();
        }

        if (useForCurrentSession)
        {
            _options.DefaultProvider = "llama-cpp";
        }
    }

    private static string BuildMessage(string mode, bool startServer, bool ready)
    {
        if (mode.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp remote endpoint is selected for the current session.";
        }

        if (!ready)
        {
            return "llama.cpp managed runtime files are installed, but the server is not running.";
        }

        return startServer
            ? "llama.cpp managed runtime is installed and ready."
            : "llama.cpp managed runtime files are installed.";
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
