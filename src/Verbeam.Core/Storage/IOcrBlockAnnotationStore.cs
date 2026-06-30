using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

/// <summary>
/// Persists per-block manual state for the OCR block workbench, keyed on
/// (profile, image hash, block id), plus an append-only per-block history.
/// </summary>
public interface IOcrBlockAnnotationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>All annotations for one image (one OCR result), for the given profile.</summary>
    Task<IReadOnlyList<OcrBlockAnnotation>> ListByImageAsync(
        string profileId,
        string imageHash,
        CancellationToken cancellationToken = default);

    /// <summary>Insert or update a single block's annotation; returns the stored row.</summary>
    Task<OcrBlockAnnotation> UpsertAsync(
        OcrBlockAnnotation annotation,
        CancellationToken cancellationToken = default);

    /// <summary>Append one historical OCR/translation version of a block.</summary>
    Task AppendHistoryAsync(
        OcrBlockHistoryEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>Most-recent-first history for one block.</summary>
    Task<IReadOnlyList<OcrBlockHistoryEntry>> ListHistoryAsync(
        string profileId,
        string imageHash,
        string blockId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
