using System.Runtime.CompilerServices;
using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public interface ITranslationProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams the translation as incremental token deltas followed by a single
    /// terminal chunk carrying the full result. The default implementation falls
    /// back to the non-streaming call and emits the whole result at once, so only
    /// providers that genuinely stream (e.g. llama.cpp) need to override it.
    /// </summary>
    async IAsyncEnumerable<ProviderStreamChunk> TranslateStreamAsync(
        ProviderTranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await TranslateAsync(request, cancellationToken);
        yield return new ProviderStreamChunk(result.Text, result);
    }
}
