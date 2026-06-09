using LocalTranslateHub.Core.Models;

namespace LocalTranslateHub.Core.Providers;

public sealed class MockTranslationProvider : ITranslationProvider
{
    public ProviderDescriptor Descriptor { get; } = new(
        "mock",
        "Mock Provider",
        "test",
        "mock",
        RequiresNetwork: false,
        IsLocal: true);

    public Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = $"[mock {request.Source}->{request.Target} {request.Mode}] {request.Text}";
        return Task.FromResult(new ProviderTranslationResult(text, "mock"));
    }
}
