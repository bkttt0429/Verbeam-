namespace Verbeam.Core.Storage;

/// <summary>
/// Function-axis partition key for the physical database split. Combined with a
/// game-axis scope (gameId ≡ profileId) by <see cref="IDatabaseRouter"/> to resolve
/// the on-disk SQLite file. The whole schema is created in every file; tables a
/// given domain does not use simply stay empty. See rag-db-partition-design.
/// </summary>
public enum DbDomain
{
    /// <summary>Cross-cutting global data: profiles registry, principals/auth/oidc. → core.sqlite</summary>
    Global,

    /// <summary>Per-game realtime data: translations, memory items/embeddings, rag context
    /// audit, sessions/events, scene summaries, ocr corrections. → games/{gameId}/realtime.sqlite</summary>
    Realtime,

    /// <summary>Document/PDF editor data: document jobs, ocr block layout/annotations.
    /// → document.sqlite (global for now; flip via DatabaseRoutingOptions.DocumentPerGame).</summary>
    Document,

    /// <summary>Speech transcription data. → speech.sqlite (global for now; flip via SpeechPerGame).</summary>
    Speech,

    /// <summary>Cross-game OCR image-hash dedup cache. → ocr-cache.sqlite (global).</summary>
    OcrCache,
}
