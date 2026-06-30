using System.Collections.Concurrent;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

/// <summary>
/// Bridges pdf2zh_next's per-segment OpenAI-compatible translation calls to Verbeam's own
/// translations + the user's per-block edits. When the auto exporter runs pdf2zh, it points
/// pdf2zh's <c>--openai-base-url</c> at Verbeam's internal shim, which delegates here: a
/// requested source segment first matches (normalized) against the job's block source texts —
/// returning the edited or originally-translated text — and otherwise falls back to a live
/// LLM translation. This injects both auto-translations and manual edits into pdf2zh's
/// layout-preserving, selectable-text render. See <see cref="TranslationService"/>.
/// </summary>
public sealed class Pdf2zhTranslationBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ContextTtl = TimeSpan.FromMinutes(30);

    private readonly IDocumentJobStore _jobs;
    private readonly IOcrBlockAnnotationStore _annotations;
    private readonly TranslationService _translation;
    private readonly ConcurrentDictionary<string, JobContext> _cache = new(StringComparer.Ordinal);

    public Pdf2zhTranslationBridge(
        IDocumentJobStore jobs,
        IOcrBlockAnnotationStore annotations,
        TranslationService translation)
    {
        _jobs = jobs;
        _annotations = annotations;
        _translation = translation;
    }

    private sealed record JobContext(
        string ProfileId,
        string Source,
        string Target,
        string Mode,
        string Provider,
        string Model,
        IReadOnlyDictionary<string, string> Map,
        DateTimeOffset BuiltAt);

    /// <summary>Pre-builds + caches a job's source→translation map before launching pdf2zh.</summary>
    public async Task RegisterJobAsync(string jobId, string profileId, CancellationToken cancellationToken)
        => _cache[jobId] = await BuildAsync(jobId, profileId, cancellationToken);

    /// <summary>
    /// Resolves a translation for a source segment pdf2zh asked to translate: an exact
    /// (normalized) block match returns the edited/translated text; anything else (sub-slices,
    /// batched paragraphs) falls back to the live LLM so nothing is left untranslated.
    /// </summary>
    public async Task<string> TranslateSegmentAsync(
        string jobId,
        string profileId,
        string segment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return segment ?? string.Empty;
        }

        if (!_cache.TryGetValue(jobId, out var context) || DateTimeOffset.UtcNow - context.BuiltAt > ContextTtl)
        {
            context = await BuildAsync(jobId, profileId, cancellationToken);
            _cache[jobId] = context;
        }

        if (context.Map.TryGetValue(TranslationCacheKey.NormalizeText(segment), out var hit))
        {
            return hit;
        }

        var outcome = await _translation.TranslateAsync(
            new MortTranslateRequest
            {
                Text = segment,
                Source = context.Source,
                Target = context.Target,
                Mode = context.Mode,
                Provider = context.Provider,
                Model = context.Model,
                Profile = profileId,
                SessionId = jobId
            },
            cancellationToken);
        return outcome.IsSuccess ? outcome.Text : segment;
    }

    private async Task<JobContext> BuildAsync(string jobId, string profileId, CancellationToken cancellationToken)
    {
        var request = await _jobs.GetRequestAsync(jobId, cancellationToken);
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var irArtifact = job?.Artifacts
            .FirstOrDefault(item => item.Kind.Equals("ocr-ir-json", StringComparison.OrdinalIgnoreCase));
        if (irArtifact is not null && File.Exists(irArtifact.Path))
        {
            OcrDocumentResult? document;
            await using (var stream = File.OpenRead(irArtifact.Path))
            {
                document = await JsonSerializer.DeserializeAsync<OcrDocumentResult>(stream, JsonOptions, cancellationToken);
            }

            foreach (var page in document?.Pages ?? [])
            {
                var docKey = $"{jobId}:{page.PageIndex}";
                var annotations = (await _annotations.ListByImageAsync(profileId, docKey, cancellationToken))
                    .ToDictionary(item => item.BlockId, StringComparer.Ordinal);
                foreach (var block in OcrBlockFlattener.Flatten(page))
                {
                    annotations.TryGetValue(block.Id, out var annotation);
                    if (annotation is not null && annotation.Status == OcrBlockStatuses.Ignored)
                    {
                        continue;
                    }

                    var source = string.IsNullOrEmpty(annotation?.EditedSource) ? block.SourceText : annotation!.EditedSource;
                    var translation = string.IsNullOrEmpty(annotation?.EditedTranslation) ? block.Text : annotation!.EditedTranslation;
                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
                    {
                        continue;
                    }

                    map[TranslationCacheKey.NormalizeText(source)] = translation;
                }
            }
        }

        return new JobContext(
            profileId,
            string.IsNullOrWhiteSpace(request?.Source) ? "auto" : request!.Source!,
            string.IsNullOrWhiteSpace(request?.Target) ? "zh-TW" : request!.Target!,
            request?.Mode ?? string.Empty,
            request?.TranslationProvider ?? string.Empty,
            request?.Model ?? string.Empty,
            map,
            DateTimeOffset.UtcNow);
    }
}
