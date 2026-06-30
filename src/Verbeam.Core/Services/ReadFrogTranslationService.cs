using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class ReadFrogTranslationService
{
    private readonly VerbeamOptions _options;
    private readonly TranslationService _translationService;

    public ReadFrogTranslationService(
        VerbeamOptions options,
        TranslationService translationService)
    {
        _options = options;
        _translationService = translationService;
    }

    public Task<ReadFrogTranslationOutcome> TranslateAsync(
        ReadFrogTranslateRequest request,
        CancellationToken cancellationToken = default)
        => TranslateAsync(request, allowSharedMemory: false, cancellationToken);

    public async Task<ReadFrogTranslationOutcome> TranslateAsync(
        ReadFrogTranslateRequest request,
        bool allowSharedMemory,
        CancellationToken cancellationToken = default)
    {
        var translatedRequest = ToMortRequest(request) with { AllowSharedMemory = allowSharedMemory };
        var outcome = await _translationService.TranslateAsync(translatedRequest, cancellationToken);
        return new ReadFrogTranslationOutcome(translatedRequest, outcome);
    }

    public async Task<ReadFrogTranslationOutcome> TranslateAsync(
        ReadFrogTranslateRequest request,
        bool allowSharedMemory,
        string principalId,
        CancellationToken cancellationToken = default)
    {
        var translatedRequest = ToMortRequest(request) with
        {
            AllowSharedMemory = allowSharedMemory,
            PrincipalId = principalId
        };
        var outcome = await _translationService.TranslateAsync(translatedRequest, cancellationToken);
        return new ReadFrogTranslationOutcome(translatedRequest, outcome);
    }

    public MortTranslateRequest ToMortRequest(ReadFrogTranslateRequest request)
    {
        var contextItems = BuildContextItems(request);
        return new MortTranslateRequest
        {
            Name = Pick(request.Name, "read-frog"),
            Text = request.Text,
            Source = Pick(request.Source, Pick(request.LangConfig?.SourceCode, _options.DefaultSource)),
            Target = Pick(request.Target, Pick(request.LangConfig?.TargetCode, _options.DefaultTarget)),
            Mode = Pick(request.Mode, "web_article"),
            Glossary = request.Glossary,
            Provider = request.Provider,
            Model = request.Model,
            Profile = Pick(request.Profile, "read-frog"),
            SessionId = request.SessionId,
            Context = request.Context,
            ContextItems = contextItems.Count == 0 ? null : contextItems,
            SkipMemoryContext = request.SkipMemoryContext,
            TraceId = request.TraceId,
            ItemId = request.ItemId,
            ChunkId = request.ChunkId,
            ClientQueuedAtUnixMs = request.ClientQueuedAtUnixMs,
            ClientRequestStartedAtUnixMs = request.ClientRequestStartedAtUnixMs,
            BackgroundReceivedAtUnixMs = request.BackgroundReceivedAtUnixMs,
            BackgroundFetchStartedAtUnixMs = request.BackgroundFetchStartedAtUnixMs
        };
    }

    private static IReadOnlyList<string> BuildContextItems(ReadFrogTranslateRequest request)
    {
        var items = new List<string>();
        if (request.ContextItems is not null)
        {
            items.AddRange(request.ContextItems.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        var webContext = BuildWebPageContext(request);
        if (!string.IsNullOrWhiteSpace(webContext))
        {
            items.Add(webContext);
        }

        if (!string.IsNullOrWhiteSpace(request.LangConfig?.Level))
        {
            items.Add($"Read Frog learner level: {request.LangConfig.Level.Trim()}");
        }

        return items;
    }

    private static string BuildWebPageContext(ReadFrogTranslateRequest request)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "WEB_TITLE", request.WebTitle);
        AppendSection(builder, "WEB_SUMMARY", request.WebSummary);
        AppendSection(builder, "WEB_CONTENT", request.WebContent);
        return builder.ToString().Trim();
    }

    private static void AppendSection(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(label);
        builder.AppendLine(":");
        builder.Append(value.Trim());
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
