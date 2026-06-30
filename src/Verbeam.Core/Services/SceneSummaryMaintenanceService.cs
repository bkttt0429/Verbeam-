using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class SceneSummaryMaintenanceService
{
    private const int MaxInlineLength = 120;

    private readonly ITranslationEventStore _eventStore;
    private readonly ISceneSummaryStore _sceneSummaryStore;
    private readonly MemoryOptions _options;

    public SceneSummaryMaintenanceService(
        ITranslationEventStore eventStore,
        ISceneSummaryStore sceneSummaryStore,
        VerbeamOptions options)
    {
        _eventStore = eventStore;
        _sceneSummaryStore = sceneSummaryStore;
        _options = options.Memory;
    }

    public async Task<SceneSummary?> MaintainAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (!_options.SceneSummaryMaintenanceEnabled || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var threshold = Math.Clamp(_options.SceneSummaryEventThreshold, 2, 100);
        var maxEvents = Math.Clamp(_options.SceneSummaryMaxEvents, threshold, 200);
        var events = await _eventStore.ListSessionSuccessEventsAsync(
            profileId,
            sessionId,
            sourceLanguage,
            targetLanguage,
            mode,
            maxEvents,
            cancellationToken);

        var selected = events
            .Where(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.SourceLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.Mode, mode, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.ErrorCode == "0")
            .Where(item => !string.IsNullOrWhiteSpace(item.TranslatedText))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length < threshold)
        {
            return null;
        }

        var summary = BuildSummary(
            selected,
            Math.Clamp(_options.SceneSummaryMaxCharacters, 200, 4000));
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var id = "scene_" + RagSecurityPolicy.ComputeSourceHash(
            "scene-summary",
            profileId,
            sessionId,
            sourceLanguage,
            targetLanguage,
            mode)[..32];

        return await _sceneSummaryStore.AddOrUpdateAsync(
            new SceneSummaryUpsertRequest
            {
                Id = id,
                Profile = profileId,
                SessionId = sessionId,
                SummaryText = summary,
                StartEventId = selected[0].Id,
                EndEventId = selected[^1].Id
            },
            cancellationToken);
    }

    private static string BuildSummary(
        IReadOnlyList<TranslationEvent> events,
        int maxCharacters)
    {
        var builder = new StringBuilder("Recent session summary:");
        foreach (var item in events)
        {
            builder.AppendLine()
                .Append("- ")
                .Append(Inline(item.SourceText))
                .Append(" => ")
                .Append(Inline(item.TranslatedText));
        }

        var value = builder.ToString().Trim();
        return value.Length <= maxCharacters
            ? value
            : value[..maxCharacters].TrimEnd() + " [...truncated]";
    }

    private static string Inline(string value)
    {
        var sanitized = RagSecurityPolicy.SanitizePromptData(value)
            .ReplaceLineEndings(" ")
            .Trim();
        return sanitized.Length <= MaxInlineLength
            ? sanitized
            : sanitized[..MaxInlineLength].TrimEnd() + " [...truncated]";
    }
}
