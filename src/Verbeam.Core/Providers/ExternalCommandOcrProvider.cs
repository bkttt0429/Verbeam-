using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Providers;

public sealed class ExternalCommandOcrProvider : IOcrProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExternalOcrOptions _options;
    private readonly string _workingDirectory;

    public ExternalCommandOcrProvider(ExternalOcrOptions options, string workingDirectory)
    {
        _options = options;
        _workingDirectory = workingDirectory;
    }

    public OcrProviderDescriptor Descriptor { get; } = new(
        "external",
        "External OCR Command",
        "process",
        "ja",
        RequiresExternalProcess: true,
        IsLocal: true);

    public async Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.FileName))
        {
            throw new InvalidOperationException("External OCR provider is not configured. Set Verbeam:Ocr:External:FileName.");
        }

        var extension = ExtensionFromMimeType(request.ImageMimeType);
        var imagePath = Path.Combine(Path.GetTempPath(), $"verbeam-ocr-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(imagePath, request.ImageBytes, cancellationToken);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.FileName,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            // Pass each templated argument as a discrete ArgumentList entry (.NET applies correct
            // Win32 escaping) so request-derived values can't inject extra arguments. See
            // ExternalCommandTemplate.
            foreach (var argument in ExternalCommandTemplate.BuildArguments(
                _options.Arguments,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["{image}"] = imagePath,
                    ["{language}"] = request.Language,
                    ["{preprocess}"] = request.PreprocessingPreset
                }))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start OCR command '{_options.FileName}'.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException($"External OCR command timed out after {_options.TimeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"External OCR command failed with exit code {process.ExitCode}: {stderr.Trim()}");
            }

            return ParseOutput(stdout, Path.GetFileName(_options.FileName));
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    private static OcrProviderResult ParseOutput(string stdout, string engineName)
    {
        var trimmed = stdout.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new OcrProviderResult(string.Empty, Array.Empty<OcrTextBlock>(), $"external:{engineName}");
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    ? ParseBlocksOnly(document.RootElement, engineName)
                    : ParseObject(document.RootElement, engineName);
            }
            catch (JsonException)
            {
                // Fall through to plain text output.
            }
        }

        IReadOnlyList<OcrTextBlock> blocks =
        [
            new OcrTextBlock(trimmed, 1.0, null)
        ];

        return new OcrProviderResult(trimmed, blocks, $"external:{engineName}");
    }

    private static OcrProviderResult ParseObject(JsonElement root, string engineName)
    {
        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;

        var blocks = root.TryGetProperty("blocks", out var blocksElement) && blocksElement.ValueKind == JsonValueKind.Array
            ? ParseBlocks(blocksElement)
            : Array.Empty<OcrTextBlock>();
        var document = root.TryGetProperty("document", out var documentElement) && documentElement.ValueKind == JsonValueKind.Object
            ? ParseDocument(documentElement)
            : null;
        var engine = ResolveEngine(root, document, engineName);

        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.Join(Environment.NewLine, blocks.Select(block => block.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            blocks = [new OcrTextBlock(text, 1.0, null)];
        }

        return new OcrProviderResult(text, blocks, engine, document);
    }

    private static string ResolveEngine(JsonElement root, OcrDocumentResult? document, string engineName)
    {
        if (root.TryGetProperty("engine", out var engineElement))
        {
            var engine = engineElement.GetString();
            if (!string.IsNullOrWhiteSpace(engine))
            {
                return engine;
            }
        }

        var documentEngine = document?.Pages
            .SelectMany(page => page.Blocks)
            .SelectMany(FlattenBlocks)
            .Select(block => block.Engine)
            .FirstOrDefault(engine => !string.IsNullOrWhiteSpace(engine));
        return string.IsNullOrWhiteSpace(documentEngine)
            ? $"external:{engineName}"
            : documentEngine;
    }

    private static IEnumerable<OcrBlock> FlattenBlocks(OcrBlock block)
    {
        yield return block;
        foreach (var child in block.Children.SelectMany(FlattenBlocks))
        {
            yield return child;
        }
    }

    private static OcrProviderResult ParseBlocksOnly(JsonElement root, string engineName)
    {
        var blocks = ParseBlocks(root);
        var text = string.Join(Environment.NewLine, blocks.Select(block => block.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
        return new OcrProviderResult(text, blocks, $"external:{engineName}");
    }

    private static OcrDocumentResult? ParseDocument(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<OcrDocumentResult>(element.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<OcrTextBlock> ParseBlocks(JsonElement root)
    {
        var blocks = new List<OcrTextBlock>();
        foreach (var item in root.EnumerateArray())
        {
            var text = item.TryGetProperty("text", out var textElement)
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
            var confidence = item.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var value)
                ? value
                : 1.0;
            var box = item.TryGetProperty("boundingBox", out var boxElement)
                ? ParseBoundingBox(boxElement)
                : null;

            blocks.Add(new OcrTextBlock(text, confidence, box));
        }

        return blocks;
    }

    private static OcrBoundingBox? ParseBoundingBox(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var x = GetInt(element, "x");
        var y = GetInt(element, "y");
        var width = GetInt(element, "width");
        var height = GetInt(element, "height");
        return new OcrBoundingBox(x, y, width, height);
    }

    private static int GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string ExtensionFromMimeType(string mimeType)
        => mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".img"
        };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
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
    }
}
