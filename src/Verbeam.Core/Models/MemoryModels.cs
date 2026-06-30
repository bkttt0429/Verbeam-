namespace Verbeam.Core.Models;

public sealed record MemoryItem(
    string Id,
    string ProfileId,
    string MemoryKind,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Note,
    int Priority,
    double Confidence,
    string TagsJson,
    string MetadataJson,
    string TrustLevel,
    string SourceUri,
    string SourceHash,
    string CreatedBy,
    string ApprovedBy,
    string SecurityFlagsJson,
    string Classification,
    string Visibility,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt,
    int UseCount);

public sealed record MemorySearchRequest(
    string ProfileId,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<string> MemoryKinds,
    int Limit,
    bool ActiveOnly = true,
    bool TrustedOnly = true,
    double MinimumConfidence = 0.0,
    bool IncludeShared = false);

public sealed record MemoryEmbedding(
    string MemoryId,
    string EmbeddingModel,
    int Dimensions,
    float[] Vector,
    string ContentHash,
    DateTimeOffset CreatedAt);

public sealed record MemoryEmbeddingMaintenanceRequest
{
    public string? Profile { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public int? Limit { get; init; }
}

public sealed record MemoryEmbeddingMaintenanceResult(
    string ProfileId,
    string SourceLanguage,
    string TargetLanguage,
    string EmbeddingModel,
    int CandidateCount,
    int CreatedCount,
    int UpdatedCount,
    int CurrentCount,
    int SkippedCount);

public sealed record MemoryMaintenanceJob(
    string Id,
    string JobKind,
    string Status,
    string ProfileId,
    string SessionId,
    string SourceLanguage,
    string TargetLanguage,
    string Mode,
    int Attempts,
    string ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record MemoryMaintenanceDrainRequest
{
    public int? Limit { get; init; }
}

public sealed record MemoryMaintenanceDrainResult(
    int ClaimedCount,
    int CompletedCount,
    int FailedCount);

public sealed record MemoryContext(
    string Text,
    string Hash,
    IReadOnlyList<string> MemoryIds)
{
    public string PolicyVersion { get; init; } = string.Empty;
    public IReadOnlyList<MemoryContextSnippet> Snippets { get; init; } = [];
    public IReadOnlyList<string> RecentEventIds { get; init; } = [];
    public IReadOnlyList<string> SceneSummaryIds { get; init; } = [];

    public static readonly MemoryContext Empty = new(string.Empty, string.Empty, []);
}

public sealed record MemoryContextSnippet(
    string MemoryId,
    string MemoryKind,
    string SnippetHash,
    string TrustLevel,
    string SourceHash);

public sealed record MemoryContextDebugResult(
    string ProfileId,
    string SessionId,
    string SourceLanguage,
    string TargetLanguage,
    string Mode,
    string SourceText,
    bool PromptContextEnabled,
    int CandidateCount,
    long RetrievalElapsedMs,
    string ContextHash,
    int ContextCharacterCount,
    int SelectedMemoryCount,
    int SelectedRecentEventCount,
    string PolicyVersion,
    string RenderedContext,
    IReadOnlyList<MemoryContextDebugItem> Items)
{
    public IReadOnlyList<RecentContextDebugItem> RecentEvents { get; init; } = [];
    public IReadOnlyList<SceneSummaryDebugItem> SceneSummaries { get; init; } = [];
}

public sealed record MemoryContextDebugItem(
    string Id,
    string MemoryKind,
    string SourceText,
    string TargetText,
    string Note,
    string TrustLevel,
    int Priority,
    double Confidence,
    int UseCount,
    string SourceHash,
    string SnippetHash,
    int Score,
    string Reason);

public sealed record RecentContextDebugItem(
    string Id,
    string RequestName,
    string SourceText,
    string TranslatedText,
    string Engine,
    string? TranslationKey,
    string SnippetHash,
    DateTimeOffset CreatedAt);

public sealed record MemoryContextAuditEntry(
    string Id,
    string RequestId,
    string ProfileId,
    string PrincipalId,
    string SessionId,
    string? TranslationKey,
    string MemoryId,
    string MemoryKind,
    string SnippetHash,
    string ContextHash,
    string TrustLevel,
    string SourceHash,
    string PolicyVersion,
    int ContextCharacterCount,
    int SelectedMemoryCount,
    int SelectedRecentEventCount,
    string Decision,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record MemoryContextRequest(
    string ProfileId,
    string SessionId,
    string SourceLanguage,
    string TargetLanguage,
    string Mode,
    string SourceText,
    bool AllowSharedMemory = false);

public record MemoryUpsertRequest
{
    public string? Profile { get; init; }
    public string? MemoryKind { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? SourceText { get; init; }
    public string? TargetText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? Origin { get; init; }
    public string? TrustLevel { get; init; }
    public string? SourceUri { get; init; }
    public string? SourceHash { get; init; }
    public string? CreatedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Classification { get; init; }
    public string? Visibility { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryTrustUpdateRequest
{
    public string? TrustLevel { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Classification { get; init; }
    public string? Visibility { get; init; }
    public bool? IsActive { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryReviewRequest
{
    public string? Action { get; init; }
    public string? ReviewedBy { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryReviewBatchRequest
{
    public IReadOnlyList<string>? Ids { get; init; }
    public string? Action { get; init; }
    public string? ReviewedBy { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryConflictResolveRequest
{
    public string? ReviewedBy { get; init; }
    public bool? ApproveWinner { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryConflictMergeRequest
{
    public string? TargetText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? ReviewedBy { get; init; }
    public bool? ApproveWinner { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryUpdateRequest
{
    public string? SourceText { get; init; }
    public string? TargetText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? TrustLevel { get; init; }
    public string? SourceUri { get; init; }
    public string? SourceHash { get; init; }
    public string? CreatedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Classification { get; init; }
    public string? Visibility { get; init; }
    public bool? IsActive { get; init; }
    public bool? AcknowledgeSecurityFlags { get; init; }
}

public sealed record MemoryExportPackage(
    string ProfileId,
    DateTimeOffset ExportedAt,
    bool IncludesInactive,
    IReadOnlyList<MemoryItem> Items);

public sealed record MemoryImportRequest
{
    public string? Profile { get; init; }
    public string? SourceUri { get; init; }
    public string? ImportedBy { get; init; }
    public IReadOnlyList<MemoryImportItem> Items { get; init; } = [];
}

public sealed record MemoryImportItem
{
    public string? Profile { get; init; }
    public string? ProfileId { get; init; }
    public string? MemoryKind { get; init; }
    public string? Source { get; init; }
    public string? SourceLanguage { get; init; }
    public string? Target { get; init; }
    public string? TargetLanguage { get; init; }
    public string? SourceText { get; init; }
    public string? TargetText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? TrustLevel { get; init; }
    public string? SourceUri { get; init; }
    public string? SourceHash { get; init; }
    public string? CreatedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Classification { get; init; }
    public string? Visibility { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record MemoryImportResult(
    int Total,
    int Imported,
    int Rejected,
    int Quarantined,
    IReadOnlyList<MemoryItem> Items,
    IReadOnlyList<MemoryImportError> Errors)
{
    public IReadOnlyList<MemoryImportConflict> Conflicts { get; init; } = [];
}

public sealed record MemoryImportError(
    int Index,
    string ErrorMessage);

public sealed record MemoryImportConflict(
    int Index,
    string ExistingMemoryId,
    string ProfileId,
    string MemoryKind,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string ExistingTargetText,
    string ImportedTargetText,
    string ExistingTrustLevel);

public sealed record MemoryPrincipalPermission(
    string PrincipalId,
    string ProfileId,
    string Role,
    bool CanReadSharedMemory,
    bool CanWriteMemory,
    bool CanApproveMemory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class MemoryPrincipalRoles
{
    public const string Blocked = "blocked";
    public const string Reader = "reader";
    public const string Contributor = "contributor";
    public const string Reviewer = "reviewer";
    public const string Admin = "admin";
    public const string Custom = "custom";

    public static string Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return Custom;
        }

        var normalized = role.Trim().ToLowerInvariant();
        return normalized switch
        {
            Blocked or Reader or Contributor or Reviewer or Admin or Custom => normalized,
            _ => throw new ArgumentException("role must be blocked, reader, contributor, reviewer, admin, or custom.")
        };
    }

    public static bool TryGetPreset(
        string role,
        out bool canReadSharedMemory,
        out bool canWriteMemory,
        out bool canApproveMemory)
    {
        (canReadSharedMemory, canWriteMemory, canApproveMemory) = role switch
        {
            Blocked => (false, false, false),
            Reader => (true, false, false),
            Contributor => (true, true, false),
            Reviewer => (true, false, true),
            Admin => (true, true, true),
            _ => (false, false, false)
        };
        return role is Blocked or Reader or Contributor or Reviewer or Admin;
    }

    public static string Infer(
        bool canReadSharedMemory,
        bool canWriteMemory,
        bool canApproveMemory)
        => (canReadSharedMemory, canWriteMemory, canApproveMemory) switch
        {
            (false, false, false) => Blocked,
            (true, false, false) => Reader,
            (true, true, false) => Contributor,
            (true, false, true) => Reviewer,
            (true, true, true) => Admin,
            _ => Custom
        };
}

public sealed record MemoryPrincipalPermissionUpsertRequest
{
    public string? Principal { get; init; }
    public string? PrincipalId { get; init; }
    public string? Profile { get; init; }
    public string? ProfileId { get; init; }
    public string? Role { get; init; }
    public bool? CanReadSharedMemory { get; init; }
    public bool? CanWriteMemory { get; init; }
    public bool? CanApproveMemory { get; init; }
}

public sealed record MemoryPrincipalSession(
    string Id,
    string PrincipalId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastSeenAt);

public sealed record MemoryPrincipalSessionCreateRequest
{
    public string? Principal { get; init; }
    public string? PrincipalId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record MemoryPrincipalSessionCreateResult(
    MemoryPrincipalSession Session,
    string SessionToken);

public sealed record MemoryOidcLoginStartResult(
    bool Enabled,
    string AuthorizationUrl,
    string State,
    DateTimeOffset ExpiresAt);

public sealed record MemoryOidcTokenResult(
    string TokenType,
    string AccessToken,
    string IdToken,
    string RefreshToken,
    int? ExpiresIn);

public sealed record MemoryOidcSessionResult(
    MemoryPrincipalSession Session,
    string SessionToken,
    string Principal,
    IReadOnlyList<string> Groups,
    string RefreshToken)
{
    public string RefreshTokenHandle { get; init; } = string.Empty;
}

public sealed record MemoryOidcRefreshTokenHandle(
    string Id,
    string PrincipalId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record MemoryOidcRefreshRequest
{
    public string? RefreshToken { get; init; }
    public string? RefreshTokenHandle { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record MemoryPrincipalCredential(
    string Id,
    string PrincipalId,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record MemoryPrincipalCredentialCreateRequest
{
    public string? Principal { get; init; }
    public string? PrincipalId { get; init; }
    public string? Label { get; init; }
    public string? Secret { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record MemoryPrincipalCredentialCreateResult(
    MemoryPrincipalCredential Credential,
    string Secret);

public sealed record MemoryPrincipalCredentialLoginRequest
{
    public string? Principal { get; init; }
    public string? PrincipalId { get; init; }
    public string? Secret { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record MemoryPrincipalDeprovisionRequest
{
    public string? Principal { get; init; }
    public string? PrincipalId { get; init; }
    public bool RevokeSessions { get; init; } = true;
    public bool RevokeCredentials { get; init; } = true;
    public bool RevokeOidcRefreshTokens { get; init; } = true;
    public bool DeletePermissions { get; init; } = false;
}

public sealed record MemoryPrincipalDeprovisionResult(
    string PrincipalId,
    int RevokedSessions,
    int RevokedCredentials,
    int RevokedOidcRefreshTokens,
    int DeletedPermissions);

public sealed record MemoryConflictGroup(
    string ProfileId,
    string MemoryKind,
    string SourceLanguage,
    string TargetLanguage,
    string SourceTextNormalized,
    IReadOnlyList<string> TargetTexts,
    IReadOnlyList<MemoryItem> Items);

public sealed record MemoryConflictResolveResult(
    MemoryItem Winner,
    IReadOnlyList<MemoryItem> Deactivated,
    IReadOnlyList<MemoryConflictGroup> RemainingConflicts);

public sealed record TranslationCorrectionRequest
{
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? EventId { get; init; }
    public string? CorrectedText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
}
