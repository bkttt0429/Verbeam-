using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

/// <summary>
/// Persists per-block geometry + text-format overrides for the PDF overlay editor, keyed on
/// (profile, doc key, block id) where doc key is "{jobId}:{pageIndex}". Sibling to
/// <see cref="IOcrBlockAnnotationStore"/>.
/// </summary>
public interface IOcrBlockLayoutStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>All layout overrides for one document page, for the given profile.</summary>
    Task<IReadOnlyList<OcrBlockLayout>> ListByDocKeyAsync(
        string profileId,
        string docKey,
        CancellationToken cancellationToken = default);

    /// <summary>Insert or update a single block's layout override; returns the stored row.</summary>
    Task<OcrBlockLayout> UpsertLayoutAsync(
        OcrBlockLayout layout,
        CancellationToken cancellationToken = default);
}
