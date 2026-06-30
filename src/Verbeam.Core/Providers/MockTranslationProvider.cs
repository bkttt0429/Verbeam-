using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class MockTranslationProvider : ITranslationProvider
{
    public ProviderDescriptor Descriptor { get; } = new(
        "mock",
        "Mock Provider",
        "test",
        "mock",
        RequiresNetwork: false,
        IsLocal: true);

    /// <summary>
    /// Test hook: any input containing this marker makes the provider throw, so
    /// callers can exercise per-block translation-failure handling.
    /// </summary>
    public const string FailureMarker = "__MOCK_FAIL__";

    /// <summary>
    /// Test hook: any input containing this marker makes the provider append the
    /// memory-context block it received, so callers can assert what context the
    /// prompt would carry (e.g. the realtime rolling window).
    /// </summary>
    public const string EchoContextMarker = "__MOCK_ECHO_CONTEXT__";

    public Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Text.Contains(FailureMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"mock translation failure for: {request.Text}");
        }

        var text = $"[mock {request.Source}->{request.Target} {request.Mode}] {request.Text}";
        if (request.Text.Contains(EchoContextMarker, StringComparison.Ordinal))
        {
            text += $" | context: {request.MemoryContext.ReplaceLineEndings(" / ")}";
        }

        return Task.FromResult(new ProviderTranslationResult(text, "mock")
        {
            TokenUsage = TokenUsageEstimator.EstimateTextPair(request.Text, text, "mock:estimated")
        });
    }
}
