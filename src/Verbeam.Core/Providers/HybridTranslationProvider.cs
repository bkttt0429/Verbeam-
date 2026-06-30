using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

/// <summary>
/// Hybrid translation: run the LOCAL provider first and, only if it does not finish within a soft
/// deadline (i.e. a decode spike), race a CLOUD provider and take whichever succeeds first. The
/// fast majority of lines stay fully local/private; only slow or failed lines' text is sent to the
/// cloud, which bounds the worst-case latency to roughly <c>deadline + cloud latency</c>. If the
/// cloud fails (e.g. no API key) the still-running local call is the backstop, so hybrid degrades
/// gracefully to "local, a bit slower" rather than erroring.
/// <para>
/// The inner providers are resolved LAZILY (through the registry) so this provider can itself live
/// in the same <see cref="TranslationProviderRegistry"/> without a construction-time cycle.
/// </para>
/// </summary>
public sealed class HybridTranslationProvider : ITranslationProvider
{
    private readonly Func<ITranslationProvider> _local;
    private readonly Func<ITranslationProvider> _cloud;
    private readonly int _deadlineMs;

    public HybridTranslationProvider(
        Func<ITranslationProvider> local,
        Func<ITranslationProvider> cloud,
        int deadlineMs)
    {
        _local = local;
        _cloud = cloud;
        _deadlineMs = Math.Max(1, deadlineMs);
    }

    public ProviderDescriptor Descriptor => new(
        "hybrid",
        "Hybrid (local first, cloud rescue)",
        "local-llm",
        "hybrid",
        RequiresNetwork: true,
        IsLocal: false);

    public async Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var local = _local();
        var cloud = _cloud();

        // The "hybrid" provider/model name only identifies the orchestrator; let each inner provider
        // fall back to its OWN configured model (managed llama-cpp uses the pinned runtime model;
        // DeepL uses its default tier — passing "hybrid" through would make DeepL reject the model).
        var inner = request with { Model = string.Empty };

        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localTask = RunSafeAsync(local, inner, localCts.Token);

        // Fast path: local finishes (success or failure) before the soft deadline. A plain timer
        // (no token) keeps this a pure deadline; the provider calls carry the real cancellation.
        if (await Task.WhenAny(localTask, Task.Delay(_deadlineMs)).ConfigureAwait(false) == localTask)
        {
            var quick = await localTask.ConfigureAwait(false);
            if (quick.Result is not null)
            {
                return quick.Result; // resolved within the deadline -> stays local / private
            }

            // Local failed fast -> cloud rescue only.
            var rescue = await RunSafeAsync(cloud, inner, cancellationToken).ConfigureAwait(false);
            return rescue.Result
                ?? throw new InvalidOperationException(
                    $"Hybrid translation failed: local '{quick.Error}', cloud '{rescue.Error}'.");
        }

        // Spike: local is still running past the deadline. Fire the cloud and take the first
        // success; keep the local call alive as a backstop in case the cloud fails.
        var cloudTask = RunSafeAsync(cloud, inner, cancellationToken);
        var racing = new List<Task<SafeRun>> { localTask, cloudTask };
        var errors = new List<string>();
        while (racing.Count > 0)
        {
            var finished = await Task.WhenAny(racing).ConfigureAwait(false);
            racing.Remove(finished);
            var run = await finished.ConfigureAwait(false);
            if (run.Result is not null)
            {
                localCts.Cancel(); // abandon the slower in-flight call
                return run.Result;
            }

            errors.Add(run.Error ?? "unknown error");
        }

        throw new InvalidOperationException(
            $"Hybrid translation failed on both providers: {string.Join("; ", errors)}.");
    }

    private static async Task<SafeRun> RunSafeAsync(
        ITranslationProvider provider,
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await provider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            return new SafeRun(result, null);
        }
        catch (OperationCanceledException)
        {
            return new SafeRun(null, "cancelled");
        }
        catch (Exception ex)
        {
            return new SafeRun(null, ex.Message);
        }
    }

    private readonly record struct SafeRun(ProviderTranslationResult? Result, string? Error);
}
