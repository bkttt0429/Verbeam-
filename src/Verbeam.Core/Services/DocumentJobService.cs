using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class DocumentJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly VerbeamOptions _options;
    private readonly IDocumentJobStore _jobs;
    private readonly OcrService _ocrService;
    private readonly TranslationService _translationService;
    private readonly string _contentRootPath;
    private readonly string _workRoot;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobs = new(StringComparer.OrdinalIgnoreCase);

    public DocumentJobService(
        VerbeamOptions options,
        IDocumentJobStore jobs,
        OcrService ocrService,
        TranslationService translationService,
        string contentRootPath)
    {
        _options = options;
        _jobs = jobs;
        _ocrService = ocrService;
        _translationService = translationService;
        _contentRootPath = contentRootPath;
        var cacheDirectory = Path.GetDirectoryName(PathResolver.Resolve(contentRootPath, options.CachePath)) ?? contentRootPath;
        _workRoot = Path.Combine(cacheDirectory, "document-jobs");
    }

    public async Task<DocumentJobStatus> StartAsync(
        DocumentJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            throw new ArgumentException("inputPath is required.");
        }

        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("Document input file was not found.", request.InputPath);
        }

        Directory.CreateDirectory(_workRoot);
        var jobId = Guid.NewGuid().ToString("N");
        var workDirectory = GetWorkDirectory(jobId);
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(GetArtifactsDirectory(jobId));
        Directory.CreateDirectory(GetCheckpointsDirectory(jobId));

        var inputFileName = SafeFileName(Pick(request.OriginalFileName, Path.GetFileName(request.InputPath)));
        var inputPath = Path.Combine(workDirectory, "input" + Path.GetExtension(inputFileName));
        var inputHash = await CopyWithHashAsync(request.InputPath, inputPath, cancellationToken);
        TryDelete(request.InputPath);

        var sourceKind = ResolveSourceKind(request.SourceKind, inputFileName, request.ContentType);
        var now = DateTimeOffset.UtcNow;
        var normalizedRequest = request with
        {
            InputPath = inputPath,
            OriginalFileName = inputFileName,
            SourceKind = sourceKind,
            ContentType = Pick(request.ContentType, GuessContentType(inputFileName))
        };
        var job = new DocumentJobStatus(
            jobId,
            "queued",
            Pick(request.Profile, "default"),
            Pick(request.SessionId, Guid.NewGuid().ToString("N")),
            sourceKind,
            inputFileName,
            normalizedRequest.ContentType ?? "application/octet-stream",
            inputHash,
            DocumentJobStages.Queued,
            TotalUnits: null,
            CompletedUnits: 0,
            Progress: 0,
            ArtifactCount: 0,
            WarningCount: 0,
            ErrorCode: string.Empty,
            ErrorMessage: string.Empty,
            now,
            StartedAt: null,
            CompletedAt: null,
            now);

        var cts = new CancellationTokenSource();
        if (!_activeJobs.TryAdd(job.Id, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException("Document job id collision.");
        }

        try
        {
            await _jobs.AddJobAsync(job, normalizedRequest, cancellationToken);
            await _jobs.AddEventAsync(job.Id, "job_queued", new { job }, cancellationToken);
        }
        catch
        {
            if (_activeJobs.TryRemove(job.Id, out var failedCts))
            {
                failedCts.Dispose();
            }

            throw;
        }

        _ = Task.Run(() => RunJobAsync(job, normalizedRequest, cts.Token), CancellationToken.None);
        return job;
    }

    public async Task<DocumentJobStatus?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null || IsTerminal(job.Status) || _activeJobs.ContainsKey(job.Id))
        {
            return job;
        }

        if (DateTimeOffset.UtcNow - job.UpdatedAt < TimeSpan.FromSeconds(10))
        {
            return job;
        }

        await TryResumeAsync(job, cancellationToken);
        return await _jobs.GetJobAsync(jobId, cancellationToken);
    }

    public Task<IReadOnlyList<DocumentJobStatus>> ListAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
        => _jobs.ListJobsAsync(profileId, limit, cancellationToken);

    public Task<IReadOnlyList<DocumentJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
        => _jobs.ListEventsAsync(jobId, afterSequence, limit, cancellationToken);

    public async Task<DocumentJobResult?> GetResultAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await GetAsync(jobId, cancellationToken);
        return job is null ? null : new DocumentJobResult(job, job.Artifacts, job.Warnings);
    }

    public async Task<DocumentJobArtifact?> GetArtifactAsync(
        string jobId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        return job?.Artifacts.FirstOrDefault(item => item.Id.Equals(artifactId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<OcrDocumentResult?> GetTranslatedDocumentAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        var artifact = job?.Artifacts.FirstOrDefault(item => item.Kind.Equals("ocr-ir-json", StringComparison.OrdinalIgnoreCase));
        if (artifact is null || !File.Exists(artifact.Path))
        {
            return null;
        }

        return await ReadJsonAsync<OcrDocumentResult>(artifact.Path, cancellationToken);
    }

    /// <summary>
    /// Layout-preserving "overlay" export: masks the original text under each block and draws
    /// the (edited) translation at the block's box on the original PDF, producing a new PDF that
    /// honors manual bbox/font/overflow overrides. Visual fidelity engine; for selectable text
    /// + formula handling use the pdf2zh path. <paramref name="specJson"/> is the per-page block
    /// spec (translated text + point-space boxes) built by the caller via OcrCoordinateMath.
    /// </summary>
    public async Task<DocumentJobArtifact?> ExportOverlayPdfAsync(
        string jobId,
        string specJson,
        string? targetLanguage,
        string variant,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var isDual = string.Equals(variant, "dual", StringComparison.OrdinalIgnoreCase);
        var outputFileName = isDual ? "translated-overlay-dual.pdf" : "translated-overlay.pdf";
        var artifactKind = isDual ? "translated-pdf-overlay-dual" : "translated-pdf-overlay";

        var sourcePdf = job.Artifacts
            .FirstOrDefault(item => item.Kind.Equals("source-pdf", StringComparison.OrdinalIgnoreCase))?.Path;
        if (string.IsNullOrWhiteSpace(sourcePdf) || !File.Exists(sourcePdf))
        {
            sourcePdf = Path.Combine(GetWorkDirectory(jobId), "input.pdf");
        }

        if (!File.Exists(sourcePdf))
        {
            throw new InvalidOperationException("Overlay export needs the original PDF, which was not found for this job.");
        }

        var specPath = Path.Combine(GetWorkDirectory(jobId), isDual ? "overlay-spec-dual.json" : "overlay-spec.json");
        await File.WriteAllTextAsync(specPath, specJson, new UTF8Encoding(false), cancellationToken);
        var outputPath = GetArtifactPath(jobId, outputFileName);

        var arguments = new List<string> { "--export", "--spec", specPath, "--output", outputPath };
        var fontPath = ResolveCjkFontPath(targetLanguage);
        if (!string.IsNullOrWhiteSpace(fontPath))
        {
            arguments.Add("--font");
            arguments.Add(fontPath);
        }

        arguments.Add(sourcePdf);
        await RunPdfHelperAsync(arguments, cancellationToken);

        var artifacts = job.Artifacts.ToList();
        UpsertArtifact(artifacts, artifactKind, outputPath, "application/pdf");
        await _jobs.UpdateJobAsync(job with { Artifacts = artifacts, ArtifactCount = artifacts.Count }, cancellationToken);
        return artifacts.FirstOrDefault(item =>
            item.Kind.Equals(artifactKind, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a CJK-capable font for the overlay exporter: a font bundled under scripts/fonts
    /// wins (deterministic, offline); otherwise a Windows system font chosen by target language.
    /// </summary>
    private string? ResolveCjkFontPath(string? targetLanguage)
    {
        var bundledDirectory = PathResolver.Resolve(_contentRootPath, "../../scripts/fonts");
        if (Directory.Exists(bundledDirectory))
        {
            var bundled = Directory.EnumerateFiles(bundledDirectory, "*.ttf")
                .Concat(Directory.EnumerateFiles(bundledDirectory, "*.otf"))
                .Concat(Directory.EnumerateFiles(bundledDirectory, "*.ttc"))
                .FirstOrDefault();
            if (bundled is not null)
            {
                return bundled;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var target = (targetLanguage ?? string.Empty).ToLowerInvariant();
        string[] candidates = target switch
        {
            _ when target.Contains("tw") || target.Contains("hant") || target.Contains("hk") => ["msjh.ttc", "mingliu.ttc"],
            _ when target.StartsWith("ja") => ["YuGothM.ttc", "msgothic.ttc"],
            _ when target.StartsWith("ko") => ["malgun.ttf"],
            _ => ["msyh.ttc", "simsun.ttc"]
        };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(fontsDirectory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// High-fidelity "auto" export via pdf2zh_next: it parses layout (DocLayout-YOLO), and for
    /// each text segment calls back into Verbeam's internal OpenAI shim at <paramref name="apiBaseUrl"/>
    /// (which returns the job's edited/translated text), producing layout-preserving, selectable-text
    /// mono + dual PDFs. The caller must have registered the job with the injection bridge first.
    /// </summary>
    public async Task<IReadOnlyList<DocumentJobArtifact>> ExportPdf2zhAsync(
        string jobId,
        string apiBaseUrl,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null)
        {
            return [];
        }

        var request = await _jobs.GetRequestAsync(jobId, cancellationToken);
        var sourcePdf = job.Artifacts
            .FirstOrDefault(item => item.Kind.Equals("source-pdf", StringComparison.OrdinalIgnoreCase))?.Path;
        if (string.IsNullOrWhiteSpace(sourcePdf) || !File.Exists(sourcePdf))
        {
            sourcePdf = Path.Combine(GetWorkDirectory(jobId), "input.pdf");
        }

        if (!File.Exists(sourcePdf))
        {
            throw new InvalidOperationException("pdf2zh export needs the original PDF, which was not found for this job.");
        }

        var executable = PathResolver.Resolve(_contentRootPath, _options.Document.Pdf2zhExecutable);
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"pdf2zh was not found at '{executable}'. Install it into the pdf2zh venv (pip install pdf2zh-next).");
        }

        var outputDirectory = Path.Combine(GetWorkDirectory(jobId), "pdf2zh-out");
        if (Directory.Exists(outputDirectory))
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch (IOException) { }
        }

        Directory.CreateDirectory(outputDirectory);

        var arguments = new List<string>
        {
            "--openai",
            "--openai-base-url", apiBaseUrl.TrimEnd('/') + "/internal/v1",
            "--openai-api-key", "verbeam",
            "--openai-model", $"verbeam:{jobId}:{profileId}",
            "--lang-in", MapPdf2zhLang(request?.Source, "en"),
            "--lang-out", MapPdf2zhLang(request?.Target, "zh"),
            "--output", outputDirectory
        };
        // Air-gapped/offline: restore the pre-built asset bundle (DocLayout-YOLO ONNX + fonts +
        // cmaps) instead of letting pdf2zh download it. Only passed when the bundle is actually
        // present, so a machine without it gracefully falls back to pdf2zh's own cache.
        if (!string.IsNullOrWhiteSpace(_options.Document.Pdf2zhOfflineAssets))
        {
            var offlineAssets = PathResolver.Resolve(_contentRootPath, _options.Document.Pdf2zhOfflineAssets);
            if ((Directory.Exists(offlineAssets) && Directory.EnumerateFiles(offlineAssets, "offline_assets_*.zip").Any())
                || File.Exists(offlineAssets))
            {
                arguments.Add("--restore-offline-assets");
                arguments.Add(offlineAssets);
            }
        }

        arguments.Add(sourcePdf);

        await RunProcessAsync(executable, arguments, _options.Document.Pdf2zhTimeoutSeconds, cancellationToken);

        var produced = Directory.EnumerateFiles(outputDirectory, "*.pdf").ToList();
        var mono = produced.FirstOrDefault(p => p.Contains("mono", StringComparison.OrdinalIgnoreCase))
            ?? produced.FirstOrDefault(p => !p.Contains("dual", StringComparison.OrdinalIgnoreCase));
        var dual = produced.FirstOrDefault(p => p.Contains("dual", StringComparison.OrdinalIgnoreCase));

        var artifacts = job.Artifacts.ToList();
        if (mono is not null)
        {
            UpsertArtifact(artifacts, "translated-pdf2zh", mono, "application/pdf");
        }

        if (dual is not null)
        {
            UpsertArtifact(artifacts, "translated-pdf2zh-dual", dual, "application/pdf");
        }

        await _jobs.UpdateJobAsync(job with { Artifacts = artifacts, ArtifactCount = artifacts.Count }, cancellationToken);
        return artifacts.Where(item => item.Kind.StartsWith("translated-pdf2zh", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string MapPdf2zhLang(string? language, string fallback)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value == "auto")
        {
            return fallback;
        }

        if (value.StartsWith("zh")) return "zh";
        if (value.StartsWith("ja")) return "ja";
        if (value.StartsWith("ko")) return "ko";
        if (value.StartsWith("en")) return "en";
        return value.Split('-')[0];
    }

    private async Task RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(" ", arguments.Select(Quote)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 3600)));

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"'{Path.GetFileName(fileName)}' timed out after {timeoutSeconds}s.");
        }

        var stderr = await stderrTask;
        await stdoutTask;
        if (process.ExitCode != 0)
        {
            var detail = stderr.ReplaceLineEndings(" ").Trim();
            throw new InvalidOperationException(
                $"'{Path.GetFileName(fileName)}' failed with exit code {process.ExitCode}: {(detail.Length > 400 ? detail[..400] : detail)}");
        }
    }

    public async Task<bool> CancelAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (!_activeJobs.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        await _jobs.AddEventAsync(jobId, "cancel_requested", new { jobId }, cancellationToken);
        cts.Cancel();
        return true;
    }

    private async Task TryResumeAsync(
        DocumentJobStatus job,
        CancellationToken cancellationToken)
    {
        var request = await _jobs.GetRequestAsync(job.Id, cancellationToken);
        if (request is null || string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
        {
            var failed = job with
            {
                Status = "failed",
                ErrorCode = "document_job_resume_unavailable",
                ErrorMessage = "Document job could not resume because its saved request or input file is missing.",
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _jobs.UpdateJobAsync(failed, cancellationToken);
            await _jobs.AddEventAsync(failed.Id, "error", new { jobId = failed.Id, failed.ErrorCode, failed.ErrorMessage }, cancellationToken);
            return;
        }

        var cts = new CancellationTokenSource();
        if (!_activeJobs.TryAdd(job.Id, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => RunJobAsync(job, request, cts.Token), CancellationToken.None);
        await _jobs.AddEventAsync(job.Id, "job_resumed", new { jobId = job.Id }, cancellationToken);
    }

    private async Task RunJobAsync(
        DocumentJobStatus initialJob,
        DocumentJobRequest request,
        CancellationToken cancellationToken)
    {
        var job = initialJob;
        try
        {
            job = await UpdateJobAsync(job with
            {
                Status = "running",
                Stage = DocumentJobStages.Preparing,
                StartedAt = job.StartedAt ?? DateTimeOffset.UtcNow,
                Progress = StageProgress(DocumentJobStages.Preparing, null, 0),
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            await EmitAsync(job, "job_started", new { job }, cancellationToken);

            var artifacts = job.Artifacts.ToList();
            var warnings = job.Warnings.ToList();
            if (!string.IsNullOrWhiteSpace(request.PageRange) &&
                !string.Equals(request.SourceKind, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new DocumentJobWarning(
                    "document_page_range_ignored",
                    $"Page range '{request.PageRange}' only applies to PDF documents and was ignored.",
                    request.SourceKind ?? string.Empty));
            }

            var context = await BuildDocumentContextAsync(request, cancellationToken);

            job = request.SourceKind switch
            {
                "docx" or "pptx" or "xlsx" => await ProcessOoxmlAsync(job, request, context, artifacts, warnings, cancellationToken),
                "html" => await ProcessHtmlAsync(job, request, context, artifacts, warnings, cancellationToken),
                "markdown" or "text" => await ProcessTextAsync(job, request, context, artifacts, warnings, cancellationToken),
                "image" => await ProcessImageAsync(job, request, context, artifacts, warnings, cancellationToken),
                "pdf" => await ProcessPdfAsync(job, request, context, artifacts, warnings, cancellationToken),
                _ => await ProcessTextAsync(
                    job,
                    request with { SourceKind = "text" },
                    context,
                    artifacts,
                    warnings,
                    cancellationToken)
            };

            var completed = job with
            {
                Status = "succeeded",
                Stage = DocumentJobStages.Done,
                Progress = 1,
                ArtifactCount = job.Artifacts.Count,
                WarningCount = job.Warnings.Count,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await EmitAsync(completed, "job_done", new { job = completed }, cancellationToken);
            job = await UpdateJobAsync(completed, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            job = job with
            {
                Status = "canceled",
                ErrorCode = "canceled",
                ErrorMessage = "Document job was canceled.",
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _jobs.UpdateJobAsync(job, CancellationToken.None);
            await _jobs.AddEventAsync(job.Id, "job_canceled", new { job }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            job = job with
            {
                Status = "failed",
                ErrorCode = "document_job_failed",
                ErrorMessage = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _jobs.UpdateJobAsync(job, CancellationToken.None);
            await _jobs.AddEventAsync(job.Id, "error", new { jobId = job.Id, job.ErrorCode, job.ErrorMessage }, CancellationToken.None);
        }
        finally
        {
            if (_activeJobs.TryRemove(job.Id, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    private async Task<DocumentJobStatus> ProcessTextAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string documentContext,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var isMarkdown = request.SourceKind == "markdown";
        var outputExtension = isMarkdown ? ".md" : ".txt";
        var outputPath = GetArtifactPath(job.Id, "translated" + outputExtension);
        var content = await File.ReadAllTextAsync(RequireInputPath(request), cancellationToken);
        var segments = isMarkdown
            ? MarkdownSegmenter.Segment(content, _options.Document.MergeMarkdownParagraphs)
            : SplitPlainTextLines(content);

        return await TranslateSegmentsAsync(
            job,
            request,
            documentContext,
            segments,
            separator: "\n",
            namePrefix: isMarkdown ? "md" : "line",
            renderTranslated: static text => text,
            outputPath,
            ContentTypeForExtension(outputExtension),
            artifacts,
            warnings,
            cancellationToken);
    }

    private static IReadOnlyList<DocumentSegment> SplitPlainTextLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized
            .Split('\n')
            .Select(line => new DocumentSegment(line, !string.IsNullOrWhiteSpace(line)))
            .ToArray();
    }

    private async Task<DocumentJobStatus> ProcessHtmlAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string documentContext,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var outputPath = GetArtifactPath(job.Id, "translated.html");
        var html = await File.ReadAllTextAsync(RequireInputPath(request), cancellationToken);
        var segments = HtmlTextSegmenter.Segment(html);

        return await TranslateSegmentsAsync(
            job,
            request,
            documentContext,
            segments,
            separator: string.Empty,
            namePrefix: "html",
            renderTranslated: EscapeHtml,
            outputPath,
            "text/html; charset=utf-8",
            artifacts,
            warnings,
            cancellationToken);
    }

    private async Task<DocumentJobStatus> ProcessOoxmlAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string documentContext,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var inputPath = RequireInputPath(request);
        var extension = Path.GetExtension(request.OriginalFileName ?? inputPath);
        var outputPath = GetArtifactPath(job.Id, "translated" + extension);
        var translateEntries = CountTranslatableZipEntries(inputPath, request.SourceKind ?? string.Empty);
        job = await UpdateStageAsync(job, DocumentJobStages.Translating, translateEntries, 0, artifacts, warnings, cancellationToken);

        var completedEntries = 0;
        var tokenUsages = new List<TokenUsage>();
        using (var source = ZipFile.OpenRead(inputPath))
        using (var destination = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputEntry = destination.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                await using var output = outputEntry.Open();

                if (!ShouldTranslateOoxmlEntry(request.SourceKind ?? string.Empty, entry.FullName))
                {
                    await using var input = entry.Open();
                    await input.CopyToAsync(output, cancellationToken);
                    continue;
                }

                try
                {
                    await using var input = entry.Open();
                    var document = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, cancellationToken);
                    var groups = OoxmlParagraphGrouping.GroupTextNodes(document, request.SourceKind);
                    if (groups.Count > 0)
                    {
                        var sources = groups.Select(OoxmlParagraphGrouping.JoinGroupText).ToArray();
                        var units = new DocumentTranslationUnit[groups.Count];
                        for (var index = 0; index < groups.Count; index++)
                        {
                            var previous = index > 0 ? sources[index - 1] : null;
                            var next = index + 1 < sources.Length ? sources[index + 1] : null;
                            units[index] = new DocumentTranslationUnit(
                                $"document:{job.Id}:{entry.FullName}:{index}",
                                sources[index],
                                BuildUnitContext(documentContext, previous, sources[index], next));
                        }

                        var outcomes = await TranslateUnitsAsync(units, request, null, cancellationToken);
                        tokenUsages.AddRange(outcomes
                            .Select(outcome => outcome.TokenUsage)
                            .OfType<TokenUsage>());
                        for (var index = 0; index < groups.Count; index++)
                        {
                            OoxmlParagraphGrouping.ApplyTranslation(groups[index], outcomes[index].Text);
                        }
                    }

                    await document.SaveAsync(output, SaveOptions.DisableFormatting, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add(new DocumentJobWarning("document_ooxml_entry_failed", ex.Message, entry.FullName));
                    await using var input = entry.Open();
                    await input.CopyToAsync(output, cancellationToken);
                }

                completedEntries++;
                job = await UpdateStageAsync(job, DocumentJobStages.Translating, translateEntries, completedEntries, artifacts, warnings, cancellationToken);
            }
        }

        artifacts.Add(CreateArtifact("translated", outputPath, ContentTypeForExtension(extension)));
        await EmitTokenUsageAsync(job, MergeTokenUsage(tokenUsages, "document:ooxml"), cumulative: true, cancellationToken);
        return await UpdateStageAsync(job, DocumentJobStages.Packaging, translateEntries, translateEntries, artifacts, warnings, cancellationToken);
    }

    private async Task<DocumentJobStatus> ProcessImageAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string documentContext,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var inputPath = RequireInputPath(request);
        job = await UpdateStageAsync(job, DocumentJobStages.Translating, 1, 0, artifacts, warnings, cancellationToken);
        var page = await RecognizeImageFileAsync(job.Id, inputPath, request, pageIndex: 0, cancellationToken);
        var segments = new List<OcrSegmentTranslation>();
        var translatedPage = await TranslatePageAsync(page, request, documentContext, segments, cancellationToken);
        var translatedPages = new List<OcrPageResult> { translatedPage };
        await WriteOcrArtifactsAsync(job.Id, translatedPages, artifacts, cancellationToken);
        await EmitTokenUsageAsync(
            job,
            MergeTokenUsage(segments.Select(segment => segment.TokenUsage), "document:ocr"),
            cumulative: true,
            cancellationToken);
        await EmitPageTranslatedAsync(job, translatedPage, pageCount: 1, cancellationToken);
        job = await UpdateStageAsync(job, DocumentJobStages.Translating, 1, 1, artifacts, warnings, cancellationToken);
        return await PackageOcrDocxAsync(job, translatedPages, artifacts, warnings, cancellationToken);
    }

    private async Task<DocumentJobStatus> ProcessPdfAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string documentContext,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var inputPath = RequireInputPath(request);
        // Expose the original PDF as a downloadable artifact: the overlay editor renders it
        // as the PDF.js backdrop, and the overlay exporter masks/draws onto it.
        UpsertArtifact(artifacts, "source-pdf", inputPath, "application/pdf");
        job = await UpdateStageAsync(job, DocumentJobStages.Analyzing, null, 0, artifacts, warnings, cancellationToken);
        var textDocument = await TryExtractPdfTextAsync(inputPath, warnings, cancellationToken);
        var pageCount = textDocument?.PageCount ?? await GetPdfPageCountAsync(inputPath, cancellationToken);
        var selectedPages = PageRangeParser.Parse(request.PageRange, pageCount);
        if (!string.IsNullOrWhiteSpace(request.PageRange) && selectedPages.Count == 0)
        {
            warnings.Add(new DocumentJobWarning(
                "document_page_range_out_of_range",
                $"Page range '{request.PageRange}' selected no page of {pageCount}; nothing to translate."));
        }

        var totalPages = selectedPages.Count;
        job = await UpdateStageAsync(job, DocumentJobStages.Translating, totalPages, 0, artifacts, warnings, cancellationToken);

        var translatedPages = new List<OcrPageResult>();
        var segments = new List<OcrSegmentTranslation>();

        var completedPages = 0;
        foreach (var pageIndex in selectedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translatedCheckpointPath = Path.Combine(GetCheckpointsDirectory(job.Id), $"page-{pageIndex + 1}.translated.json");
            var translatedPage = File.Exists(translatedCheckpointPath)
                ? await ReadJsonAsync<OcrPageResult>(translatedCheckpointPath, cancellationToken)
                : null;

            if (translatedPage is null)
            {
                var page = await GetPdfPageBlocksAsync(job, request, inputPath, textDocument, pageIndex, artifacts, warnings, cancellationToken);
                if (page is null)
                {
                    job = await UpdateStageAsync(job, DocumentJobStages.Translating, totalPages, ++completedPages, artifacts, warnings, cancellationToken);
                    continue;
                }

                translatedPage = await TranslatePageAsync(page, request, documentContext, segments, cancellationToken);
                await WriteJsonAsync(translatedCheckpointPath, translatedPage, cancellationToken);
            }

            translatedPages.Add(translatedPage);
            await WriteOcrArtifactsAsync(job.Id, translatedPages, artifacts, cancellationToken);
            await EmitTokenUsageAsync(
                job,
                MergeTokenUsage(segments.Select(segment => segment.TokenUsage), "document:ocr"),
                cumulative: true,
                cancellationToken);
            await EmitPageTranslatedAsync(job, translatedPage, totalPages, cancellationToken);
            job = await UpdateStageAsync(job, DocumentJobStages.Translating, totalPages, ++completedPages, artifacts, warnings, cancellationToken);
        }

        return await PackageOcrDocxAsync(job, translatedPages, artifacts, warnings, cancellationToken);
    }

    private async Task<OcrPageResult?> GetPdfPageBlocksAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string inputPath,
        PdfTextDocument? textDocument,
        int pageIndex,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        // The rendered page is now a persistent artifact: it is the original-page backdrop
        // the PDF overlay editor draws on, and the bitmap the overlay exporter masks. Every
        // page is rendered (incl. text-layer pages, which previously skipped rasterization).
        var backdropPath = GetArtifactPath(job.Id, $"page-{pageIndex + 1}.jpg");
        var backdropKind = $"page-image-{pageIndex}";

        var checkpointPath = Path.Combine(GetCheckpointsDirectory(job.Id), $"page-{pageIndex + 1}.ocr.json");
        if (File.Exists(checkpointPath))
        {
            var cached = await ReadJsonAsync<OcrPageResult>(checkpointPath, cancellationToken);
            if (cached is not null)
            {
                if (File.Exists(backdropPath))
                {
                    UpsertArtifact(artifacts, backdropKind, backdropPath, "image/jpeg");
                }

                return cached;
            }
        }

        var textPage = textDocument?.Pages.FirstOrDefault(item => item.PageIndex == pageIndex);
        var page = textPage is null ? null : BuildPageFromPdfText(textPage);
        PdfPageRenderInfo renderInfo;
        try
        {
            renderInfo = await RenderPdfPageAsync(inputPath, pageIndex, backdropPath, cancellationToken);
            page ??= await RecognizeImageFileAsync(job.Id, backdropPath, request, pageIndex, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add(new DocumentJobWarning("document_pdf_page_failed", ex.Message, $"page:{pageIndex + 1}"));
            return null;
        }

        page = StampPageRenderInfo(page, renderInfo);
        if (File.Exists(backdropPath))
        {
            UpsertArtifact(artifacts, backdropKind, backdropPath, "image/jpeg");
        }

        await WriteJsonAsync(checkpointPath, page, cancellationToken);
        return page;
    }

    private static OcrPageResult StampPageRenderInfo(OcrPageResult page, PdfPageRenderInfo info)
        => page with
        {
            RenderDpi = info.Dpi > 0 ? info.Dpi : page.RenderDpi,
            ImageWidth = info.ImageWidth > 0 ? info.ImageWidth : page.ImageWidth,
            ImageHeight = info.ImageHeight > 0 ? info.ImageHeight : page.ImageHeight,
            PageWidthPoints = info.PageWidth > 0 ? info.PageWidth : page.PageWidthPoints,
            PageHeightPoints = info.PageHeight > 0 ? info.PageHeight : page.PageHeightPoints
        };

    private async Task<OcrPageResult> TranslatePageAsync(
        OcrPageResult page,
        DocumentJobRequest request,
        string documentContext,
        List<OcrSegmentTranslation> segments,
        CancellationToken cancellationToken)
    {
        var translatedBlocks = new List<OcrBlock>();
        foreach (var block in page.Blocks.OrderBy(block => block.ReadingOrder))
        {
            translatedBlocks.Add(await TranslateOcrBlockAsync(block, request, documentContext, segments, cancellationToken));
        }

        return page with { Blocks = translatedBlocks };
    }

    private async Task WriteOcrArtifactsAsync(
        string jobId,
        IReadOnlyList<OcrPageResult> pages,
        List<DocumentJobArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        var document = new OcrDocumentResult { Pages = pages };
        var jsonPath = GetArtifactPath(jobId, "ocr-ir.json");
        await WriteJsonAsync(jsonPath, document, cancellationToken);
        UpsertArtifact(artifacts, "ocr-ir-json", jsonPath, "application/json; charset=utf-8");

        var htmlPath = GetArtifactPath(jobId, "layout.html");
        await File.WriteAllTextAsync(htmlPath, RenderOcrHtml(document), new UTF8Encoding(false), cancellationToken);
        UpsertArtifact(artifacts, "layout-html", htmlPath, "text/html; charset=utf-8");
    }

    private async Task<DocumentJobStatus> PackageOcrDocxAsync(
        DocumentJobStatus job,
        IReadOnlyList<OcrPageResult> translatedPages,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var pageCount = translatedPages.Count;
        job = await UpdateStageAsync(job, DocumentJobStages.Packaging, job.TotalUnits, job.CompletedUnits, artifacts, warnings, cancellationToken);
        var docxPath = GetArtifactPath(job.Id, "translated.docx");
        await WritePlainDocxAsync(docxPath, translatedPages.SelectMany(page => page.Blocks).Select(RenderBlockText), cancellationToken);
        UpsertArtifact(artifacts, "translated", docxPath, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        return await UpdateStageAsync(job, DocumentJobStages.Packaging, job.TotalUnits ?? pageCount, job.TotalUnits ?? pageCount, artifacts, warnings, cancellationToken);
    }

    private async Task EmitPageTranslatedAsync(
        DocumentJobStatus job,
        OcrPageResult page,
        int pageCount,
        CancellationToken cancellationToken)
        => await EmitAsync(job, "page_translated", new
        {
            jobId = job.Id,
            pageIndex = page.PageIndex,
            pageCount,
            engine = page.Blocks.FirstOrDefault()?.Engine ?? string.Empty,
            text = Truncate(RenderPageText(page), 2000)
        }, cancellationToken);

    private async Task<OcrBlock> TranslateOcrBlockAsync(
        OcrBlock block,
        DocumentJobRequest request,
        string documentContext,
        List<OcrSegmentTranslation> segments,
        CancellationToken cancellationToken)
    {
        var children = new List<OcrBlock>();
        foreach (var child in block.Children.OrderBy(child => child.ReadingOrder))
        {
            children.Add(await TranslateOcrBlockAsync(child, request, documentContext, segments, cancellationToken));
        }

        if (!ShouldTranslateBlock(block) || string.IsNullOrWhiteSpace(block.Text))
        {
            segments.Add(new OcrSegmentTranslation(block.Id, block.Type, block.Text, block.Text, Translated: false, "none", 0, CacheHit: false, "0", string.Empty));
            return block with { Children = children };
        }

        if (OcrLabelTranslationFallback.SupportsTarget(request.Target) &&
            OcrLabelTranslationFallback.TryTranslate(block.Text) is { } fallbackText)
        {
            segments.Add(new OcrSegmentTranslation(block.Id, block.Type, block.Text, fallbackText, Translated: true, "ocr:fallback", 0, CacheHit: false, "0", string.Empty));
            return block with
            {
                SourceText = string.IsNullOrWhiteSpace(block.SourceText) ? block.Text : block.SourceText,
                Text = fallbackText,
                Children = children
            };
        }

        var outcome = await TranslateTextAsync(
            $"document-ocr:{block.Type}:{block.Id}",
            block.Text,
            request,
            BuildUnitContext(documentContext, null, block.Text, null),
            cancellationToken);
        segments.Add(new OcrSegmentTranslation(
            block.Id,
            block.Type,
            block.Text,
            outcome.Text,
            outcome.IsSuccess,
            outcome.Engine,
            outcome.LatencyMs,
            outcome.CacheHit,
            outcome.ErrorCode,
            outcome.ErrorMessage,
            outcome.TokenUsage));

        return block with
        {
            SourceText = string.IsNullOrWhiteSpace(block.SourceText) ? block.Text : block.SourceText,
            Text = outcome.Text,
            Children = children
        };
    }

    private async Task<OcrPageResult> RecognizeImageFileAsync(
        string jobId,
        string imagePath,
        DocumentJobRequest request,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var mimeType = ContentTypeForExtension(Path.GetExtension(imagePath));
        var response = await _ocrService.RecognizeAsync(
            new OcrRequest
            {
                ImageBase64 = Convert.ToBase64String(bytes),
                ImageMimeType = mimeType,
                Provider = request.OcrProvider,
                ContentType = "document",
                Preference = request.OcrPreference,
                Language = request.OcrLanguage,
                Profile = request.Profile,
                SessionId = request.SessionId ?? jobId,
                PreprocessingPreset = request.OcrPreprocessingPreset
            },
            cancellationToken);

        var page = response.Document?.Pages.FirstOrDefault() ??
            new OcrPageResult
            {
                PageIndex = pageIndex,
                Blocks = response.Blocks
                    .Select((block, index) => new OcrBlock
                    {
                        Id = $"p{pageIndex + 1}-b{index + 1}",
                        Type = OcrBlockTypes.Text,
                        Text = block.Text,
                        SourceText = block.Text,
                        Confidence = block.Confidence,
                        BoundingBox = block.BoundingBox,
                        ReadingOrder = index,
                        Engine = response.Engine,
                        ShouldTranslate = true,
                        DetectedLanguage = block.DetectedLanguage,
                        Script = block.Script
                    })
                    .ToArray()
            };

        return page with
        {
            PageIndex = pageIndex,
            Blocks = page.Blocks
                .Select((block, index) => block with
                {
                    Id = string.IsNullOrWhiteSpace(block.Id) ? $"p{pageIndex + 1}-b{index + 1}" : $"p{pageIndex + 1}-{block.Id}",
                    ReadingOrder = index
                })
                .ToArray()
        };
    }

    private readonly record struct DocumentTranslationUnit(string Name, string Text, string Context);

    /// <summary>
    /// Translates the <see cref="DocumentSegment.Translate"/> segments of a text-like
    /// document with bounded concurrency, then writes the document back by joining the
    /// original (verbatim) and translated segments with <paramref name="separator"/>.
    /// </summary>
    private async Task<DocumentJobStatus> TranslateSegmentsAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        string documentContext,
        IReadOnlyList<DocumentSegment> segments,
        string separator,
        string namePrefix,
        Func<string, string> renderTranslated,
        string outputPath,
        string artifactContentType,
        List<DocumentJobArtifact> artifacts,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var translatableIndices = new List<int>();
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Translate)
            {
                translatableIndices.Add(i);
            }
        }

        var units = new DocumentTranslationUnit[translatableIndices.Count];
        for (var k = 0; k < translatableIndices.Count; k++)
        {
            var current = segments[translatableIndices[k]].Text;
            var previous = k > 0 ? segments[translatableIndices[k - 1]].Text : null;
            var next = k + 1 < translatableIndices.Count ? segments[translatableIndices[k + 1]].Text : null;
            units[k] = new DocumentTranslationUnit(
                $"document:{job.Id}:{namePrefix}:{translatableIndices[k]}",
                current,
                BuildUnitContext(documentContext, previous, current, next));
        }

        var total = units.Length;
        job = await UpdateStageAsync(job, DocumentJobStages.Translating, total, 0, artifacts, warnings, cancellationToken);

        var emitStep = Math.Max(10, total / 50);
        var lastEmit = 0;
        var running = job;
        var outcomes = await TranslateUnitsAsync(units, request, async processed =>
        {
            if (processed - lastEmit < emitStep && processed != total)
            {
                return;
            }

            lastEmit = processed;
            running = await UpdateStageAsync(running, DocumentJobStages.Translating, total, processed, artifacts, warnings, cancellationToken);
        }, cancellationToken);
        job = running;

        var builder = new StringBuilder();
        var cursor = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            if (segments[i].Translate)
            {
                builder.Append(renderTranslated(outcomes[cursor].Text));
                cursor++;
            }
            else
            {
                builder.Append(segments[i].Text);
            }
        }

        await File.WriteAllTextAsync(outputPath, builder.ToString(), new UTF8Encoding(false), cancellationToken);
        UpsertArtifact(artifacts, "translated", outputPath, artifactContentType);
        await EmitTokenUsageAsync(
            job,
            MergeTokenUsage(outcomes.Select(outcome => outcome.TokenUsage), $"document:{namePrefix}"),
            cumulative: true,
            cancellationToken);
        return await UpdateStageAsync(job, DocumentJobStages.Packaging, total, total, artifacts, warnings, cancellationToken);
    }

    /// <summary>
    /// Translates units in order, running up to <see cref="DocumentOptions.TranslationConcurrency"/>
    /// requests at once and preserving result order. <paramref name="onProgressAsync"/> runs once per
    /// completed chunk (single-threaded) with the cumulative completed count.
    /// </summary>
    private async Task<IReadOnlyList<TranslationOutcome>> TranslateUnitsAsync(
        IReadOnlyList<DocumentTranslationUnit> units,
        DocumentJobRequest request,
        Func<int, Task>? onProgressAsync,
        CancellationToken cancellationToken)
    {
        var results = new TranslationOutcome[units.Count];
        if (units.Count == 0)
        {
            return results;
        }

        var degree = Math.Clamp(_options.Document.TranslationConcurrency, 1, 32);
        var processed = 0;
        for (var start = 0; start < units.Count; start += degree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(degree, units.Count - start);
            if (count == 1)
            {
                var unit = units[start];
                results[start] = await TranslateTextAsync(unit.Name, unit.Text, request, unit.Context, cancellationToken);
            }
            else
            {
                var chunk = new Task<TranslationOutcome>[count];
                for (var j = 0; j < count; j++)
                {
                    var unit = units[start + j];
                    chunk[j] = TranslateTextAsync(unit.Name, unit.Text, request, unit.Context, cancellationToken);
                }

                var chunkOutcomes = await Task.WhenAll(chunk);
                chunkOutcomes.CopyTo(results, start);
            }

            processed += count;
            if (onProgressAsync is not null)
            {
                await onProgressAsync(processed);
            }
        }

        return results;
    }

    private async Task<TranslationOutcome> TranslateTextAsync(
        string name,
        string text,
        DocumentJobRequest request,
        string context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _translationService.TranslateAsync(
                new MortTranslateRequest
                {
                    Name = name,
                    Text = text,
                    Source = request.Source,
                    Target = request.Target,
                    Mode = request.Mode,
                    Surface = "document",
                    Glossary = request.Glossary,
                    Provider = request.TranslationProvider,
                    Model = request.Model,
                    Profile = request.Profile,
                    SessionId = request.SessionId,
                    Context = context,
                    AllowSharedMemory = request.AllowSharedMemory,
                    PrincipalId = request.PrincipalId
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A single block/part failing (e.g. the model returns an empty
            // translation) must not fail the whole job; degrade to a failed
            // segment that keeps the source text and let the job continue.
            return TranslationOutcome.Failure(text, ex.Message);
        }
    }

    private async Task<DocumentJobStatus> UpdateStageAsync(
        DocumentJobStatus job,
        string stage,
        int? totalUnits,
        int completedUnits,
        IReadOnlyList<DocumentJobArtifact> artifacts,
        IReadOnlyList<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        var updated = job with
        {
            Stage = stage,
            TotalUnits = totalUnits ?? job.TotalUnits,
            CompletedUnits = completedUnits,
            Progress = StageProgress(stage, totalUnits ?? job.TotalUnits, completedUnits),
            ArtifactCount = artifacts.Count,
            WarningCount = warnings.Count,
            Artifacts = artifacts.ToArray(),
            Warnings = warnings.ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _jobs.UpdateJobAsync(updated, cancellationToken);
        await EmitAsync(updated, "stage", new
        {
            jobId = updated.Id,
            stage,
            totalUnits = updated.TotalUnits,
            completedUnits = updated.CompletedUnits,
            progress = updated.Progress,
            artifactCount = updated.ArtifactCount,
            warningCount = updated.WarningCount
        }, cancellationToken);
        return updated;
    }

    private async Task<DocumentJobStatus> UpdateJobAsync(
        DocumentJobStatus job,
        CancellationToken cancellationToken)
    {
        await _jobs.UpdateJobAsync(job, cancellationToken);
        return job;
    }

    private async Task EmitTokenUsageAsync(
        DocumentJobStatus job,
        TokenUsage? tokenUsage,
        bool cumulative,
        CancellationToken cancellationToken)
    {
        if (tokenUsage is null)
        {
            return;
        }

        await EmitAsync(job, "token_usage", new
        {
            jobId = job.Id,
            tokenUsage,
            cumulative
        }, cancellationToken);
    }

    private async Task EmitAsync(
        DocumentJobStatus job,
        string type,
        object payload,
        CancellationToken cancellationToken)
    {
        await _jobs.AddEventAsync(job.Id, type, payload, cancellationToken);
    }

    private async Task<string> BuildDocumentContextAsync(
        DocumentJobRequest request,
        CancellationToken cancellationToken)
    {
        var inputPath = RequireInputPath(request);
        var lines = new List<string>();
        if (request.SourceKind is "text" or "markdown" or "html")
        {
            using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (!reader.EndOfStream && lines.Count < 40)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line.Trim());
                }
            }
        }

        return string.Join(
            Environment.NewLine,
            new[]
            {
                $"Document: {request.OriginalFileName}",
                $"Kind: {request.SourceKind}",
                $"Source: {request.Source ?? "auto"}",
                $"Target: {request.Target ?? _options.DefaultTarget}",
                lines.Count == 0 ? string.Empty : "Outline sample:",
                string.Join(Environment.NewLine, lines)
            }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string BuildUnitContext(string documentContext, string? previous, string current, string? next)
    {
        var items = new List<string> { documentContext };
        if (!string.IsNullOrWhiteSpace(previous))
        {
            items.Add("Previous unit:\n" + previous);
        }

        items.Add("Current unit:\n" + current);
        if (!string.IsNullOrWhiteSpace(next))
        {
            items.Add("Next unit:\n" + next);
        }

        return string.Join("\n\n", items);
    }

    private async Task<int> GetPdfPageCountAsync(string inputPath, CancellationToken cancellationToken)
    {
        var result = await RunPdfHelperAsync(["--count", inputPath], cancellationToken);
        using var document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("pageCount", out var count) && count.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException("PDF helper did not return pageCount.");
    }

    private const int PdfTextLayerMinChars = 30;

    private sealed record PdfTextBlock(string Text, IReadOnlyList<double> Bbox);

    private sealed record PdfTextPage(int PageIndex, int CharCount, int? Width, int? Height, IReadOnlyList<PdfTextBlock> Blocks);

    private sealed record PdfTextDocument(int PageCount, IReadOnlyList<PdfTextPage> Pages);

    private async Task<PdfTextDocument?> TryExtractPdfTextAsync(
        string inputPath,
        List<DocumentJobWarning> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunPdfHelperAsync(["--text", inputPath], cancellationToken);
            return JsonSerializer.Deserialize<PdfTextDocument>(result, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add(new DocumentJobWarning("document_pdf_text_extract_failed", ex.Message));
            return null;
        }
    }

    private static OcrPageResult? BuildPageFromPdfText(PdfTextPage page)
    {
        if (page.CharCount < PdfTextLayerMinChars)
        {
            return null;
        }

        var blocks = page.Blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .Select((block, index) => new OcrBlock
            {
                Id = $"p{page.PageIndex + 1}-b{index + 1}",
                Type = OcrBlockTypes.Text,
                Text = block.Text,
                SourceText = block.Text,
                Confidence = 1.0,
                BoundingBox = ToBoundingBox(block.Bbox),
                ReadingOrder = index,
                Engine = "pdf-text",
                ShouldTranslate = true
            })
            .ToArray();
        return blocks.Length == 0
            ? null
            : new OcrPageResult
            {
                PageIndex = page.PageIndex,
                Width = page.Width is > 0 ? page.Width : null,
                Height = page.Height is > 0 ? page.Height : null,
                Blocks = blocks
            };
    }

    private static OcrBoundingBox? ToBoundingBox(IReadOnlyList<double> bbox)
        => bbox is { Count: 4 }
            ? new OcrBoundingBox(
                (int)Math.Round(bbox[0]),
                (int)Math.Round(bbox[1]),
                Math.Max(0, (int)Math.Round(bbox[2] - bbox[0])),
                Math.Max(0, (int)Math.Round(bbox[3] - bbox[1])))
            : null;

    private async Task<PdfPageRenderInfo> RenderPdfPageAsync(
        string inputPath,
        int pageIndex,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        var stdout = await RunPdfHelperAsync(["--page", pageIndex.ToString(), "--output", outputPath, inputPath], cancellationToken);
        return ParsePdfPageRenderInfo(stdout);
    }

    /// <summary>Rendered backdrop pixel size + logical page size (points) reported by the PDF helper's --page mode.</summary>
    private readonly record struct PdfPageRenderInfo(int Dpi, int ImageWidth, int ImageHeight, double PageWidth, double PageHeight);

    private static PdfPageRenderInfo ParsePdfPageRenderInfo(string stdout)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(stdout);
            var root = document.RootElement;
            int ReadInt(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
            double ReadDouble(string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0;
            return new PdfPageRenderInfo(
                ReadInt("dpi"),
                ReadInt("imageWidth"),
                ReadInt("imageHeight"),
                ReadDouble("pageWidth"),
                ReadDouble("pageHeight"));
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    private async Task<string> RunPdfHelperAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var scriptPath = PathResolver.Resolve(_contentRootPath, "../../scripts/pdf_page_images.py");
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"PDF helper script was not found: {scriptPath}");
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ResolvePythonFileName(),
            Arguments = string.Join(" ", new[] { scriptPath }.Concat(arguments).Select(Quote)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start PDF helper '{startInfo.FileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PDF helper failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    private string ResolvePythonFileName()
    {
        if (!string.IsNullOrWhiteSpace(_options.Ocr.LocalSet.VenvPythonPath))
        {
            var venvPython = PathResolver.Resolve(_contentRootPath, _options.Ocr.LocalSet.VenvPythonPath);
            if (File.Exists(venvPython))
            {
                return venvPython;
            }
        }

        return string.IsNullOrWhiteSpace(_options.Ocr.LocalSet.PythonFileName)
            ? "python"
            : _options.Ocr.LocalSet.PythonFileName;
    }

    private static int CountTranslatableZipEntries(string inputPath, string sourceKind)
    {
        using var source = ZipFile.OpenRead(inputPath);
        return source.Entries.Count(entry => ShouldTranslateOoxmlEntry(sourceKind, entry.FullName));
    }

    private static bool ShouldTranslateOoxmlEntry(string sourceKind, string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        return sourceKind switch
        {
            "docx" => normalized.StartsWith("word/", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Contains("/_rels/", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith("settings.xml", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith("styles.xml", StringComparison.OrdinalIgnoreCase),
            "pptx" => (normalized.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase)) &&
                normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase),
            "xlsx" => (normalized.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)) &&
                normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private async Task WritePlainDocxAsync(
        string outputPath,
        IEnumerable<string> paragraphs,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        WriteZipEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        WriteZipEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);

        var builder = new StringBuilder();
        builder.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
            """);
        foreach (var paragraph in paragraphs.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("<w:p><w:r><w:t xml:space=\"preserve\">")
                .Append(EscapeXml(paragraph))
                .Append("</w:t></w:r></w:p>");
        }

        builder.Append("<w:sectPr/></w:body></w:document>");
        WriteZipEntry(archive, "word/document.xml", builder.ToString());
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string RenderOcrHtml(OcrDocumentResult document)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Verbeam OCR document</title>");
        builder.Append("<style>body{font-family:system-ui,sans-serif;line-height:1.55;margin:24px}.page{border:1px solid #ddd;margin:0 0 18px;padding:16px}.block{white-space:pre-wrap;margin:0 0 8px}.table{border-collapse:collapse}.table td{border:1px solid #ccc;padding:4px 6px}</style>");
        builder.Append("</head><body>");
        foreach (var page in document.Pages.OrderBy(page => page.PageIndex))
        {
            builder.Append("<section class=\"page\" data-page=\"").Append(page.PageIndex + 1).Append("\">");
            foreach (var block in page.Blocks.OrderBy(block => block.ReadingOrder))
            {
                builder.Append("<p class=\"block\" data-block=\"").Append(EscapeHtml(block.Id)).Append("\">")
                    .Append(EscapeHtml(RenderBlockText(block)))
                    .Append("</p>");
            }

            builder.Append("</section>");
        }

        builder.Append("</body></html>");
        return builder.ToString();
    }

    private static string RenderBlockText(OcrBlock block)
    {
        if (block.Table is not null)
        {
            return string.Join(
                Environment.NewLine,
                block.Table.Cells
                    .GroupBy(cell => cell.RowIndex)
                    .OrderBy(group => group.Key)
                    .Select(group => string.Join("\t", group.OrderBy(cell => cell.ColumnIndex).Select(cell => cell.Text))));
        }

        return string.IsNullOrWhiteSpace(block.Text)
            ? string.Join(Environment.NewLine, block.Children.OrderBy(child => child.ReadingOrder).Select(RenderBlockText))
            : block.Text;
    }

    private static string RenderPageText(OcrPageResult page)
        => string.Join(
            Environment.NewLine,
            page.Blocks
                .OrderBy(block => block.ReadingOrder)
                .Select(RenderBlockText)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static bool ShouldTranslateBlock(OcrBlock block)
        => block.ShouldTranslate &&
           !string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(block.Type, OcrBlockTypes.Code, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(block.Type, OcrBlockTypes.Figure, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> CopyWithHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    private async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private string GetWorkDirectory(string jobId)
        => Path.Combine(_workRoot, jobId);

    private string GetArtifactsDirectory(string jobId)
        => Path.Combine(GetWorkDirectory(jobId), "artifacts");

    private string GetCheckpointsDirectory(string jobId)
        => Path.Combine(GetWorkDirectory(jobId), "checkpoints");

    private string GetArtifactPath(string jobId, string fileName)
    {
        var directory = GetArtifactsDirectory(jobId);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, SafeFileName(fileName));
    }

    private static DocumentJobArtifact CreateArtifact(string kind, string path, string contentType)
        => new(
            Guid.NewGuid().ToString("N"),
            kind,
            Path.GetFileName(path),
            contentType,
            Path.GetFullPath(path),
            new FileInfo(path).Length,
            DateTimeOffset.UtcNow);

    private static void UpsertArtifact(List<DocumentJobArtifact> artifacts, string kind, string path, string contentType)
    {
        var index = artifacts.FindIndex(item => item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            artifacts[index] = artifacts[index] with { SizeBytes = new FileInfo(path).Length };
        }
        else
        {
            artifacts.Add(CreateArtifact(kind, path, contentType));
        }
    }

    private static string RequireInputPath(DocumentJobRequest request)
        => !string.IsNullOrWhiteSpace(request.InputPath) && File.Exists(request.InputPath)
            ? request.InputPath
            : throw new FileNotFoundException("Document job input file is missing.", request.InputPath);

    private static TokenUsage? MergeTokenUsage(IEnumerable<TokenUsage?> usages, string source)
    {
        var values = usages
            .Where(usage => usage is not null)
            .Select(usage => usage!)
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        var input = values.Sum(usage => usage.InputTokens);
        var output = values.Sum(usage => usage.OutputTokens);
        var total = values.Sum(usage => usage.TotalTokens);
        if (total <= 0)
        {
            total = input + output;
        }

        return new TokenUsage(input, output, total, source, values.Any(usage => usage.IsEstimated));
    }

    private static double StageProgress(string stage, int? totalUnits, int completedUnits)
    {
        if (stage == DocumentJobStages.Done)
        {
            return 1;
        }

        var baseValue = stage switch
        {
            DocumentJobStages.Preparing => 0.03,
            DocumentJobStages.Analyzing => 0.06,
            DocumentJobStages.Ocr => 0.12,
            DocumentJobStages.Translating => 0.10,
            DocumentJobStages.Packaging => 0.92,
            _ => 0
        };
        if (totalUnits is not > 0)
        {
            return baseValue;
        }

        var span = stage switch
        {
            DocumentJobStages.Ocr => 0.30,
            DocumentJobStages.Translating => 0.80,
            DocumentJobStages.Packaging => 0.06,
            _ => 0.08
        };
        return Math.Clamp(baseValue + (Math.Clamp(completedUnits, 0, totalUnits.Value) / (double)totalUnits.Value * span), 0, 0.98);
    }

    private static bool IsTerminal(string status)
        => status is "succeeded" or "failed" or "canceled";

    private static string ResolveSourceKind(string? requested, string fileName, string? contentType)
    {
        var normalized = requested?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized) && normalized != "auto")
        {
            return normalized;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".docx" or ".pptx" or ".xlsx")
        {
            return extension[1..];
        }

        if (extension == ".pdf")
        {
            return "pdf";
        }

        if (extension is ".htm" or ".html")
        {
            return "html";
        }

        if (extension is ".md" or ".markdown")
        {
            return "markdown";
        }

        if (extension is ".txt" or ".csv" or ".tsv")
        {
            return "text";
        }

        var mime = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "image";
        }

        return "text";
    }

    private static string GuessContentType(string fileName)
        => ContentTypeForExtension(Path.GetExtension(fileName));

    private static string ContentTypeForExtension(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pdf" => "application/pdf",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".md" or ".markdown" => "text/markdown; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "document" : sanitized;
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string EscapeXml(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static string EscapeHtml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
