using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YomiBridge.Core.Models;
using YomiBridge.Core.Options;
using YomiBridge.Core.Services;

namespace YomiBridge.Core.Providers;

public sealed class LocalPythonOcrProvider : IOcrProvider
{
    private readonly string _engineName;
    private readonly LocalOcrSetOptions _options;
    private readonly string _contentRootPath;

    public LocalPythonOcrProvider(
        OcrProviderDescriptor descriptor,
        string engineName,
        LocalOcrSetOptions options,
        string contentRootPath)
    {
        Descriptor = descriptor;
        _engineName = engineName;
        _options = options;
        _contentRootPath = contentRootPath;
    }

    public OcrProviderDescriptor Descriptor { get; }

    public async Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolveScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"Local OCR wrapper script was not found: {scriptPath}");
        }

        var extension = ExtensionFromMimeType(request.ImageMimeType);
        var imagePath = Path.Combine(Path.GetTempPath(), $"yomibridge-ocr-{_engineName}-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(imagePath, request.ImageBytes, cancellationToken);

        try
        {
            var arguments = BuildArguments(
                scriptPath,
                "--engine",
                _engineName,
                "--image",
                imagePath,
                "--language",
                request.Language);
            var result = await RunAsync(
                ResolvePythonFileName(),
                arguments,
                Math.Max(1, _options.TimeoutSeconds),
                cancellationToken);

            return ParseOutput(result.Stdout, $"local:{_engineName}");
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    public async Task<LocalOcrEngineStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var scriptPath = ResolveScriptPath();
        if (!File.Exists(scriptPath))
        {
            return new LocalOcrEngineStatus(
                _engineName,
                IsAvailable: false,
                Missing: ["local_ocr_json.py"],
                $"Local OCR wrapper script was not found: {scriptPath}");
        }

        try
        {
            var arguments = BuildArguments(scriptPath, "--engine", _engineName, "--check");
            var result = await RunAsync(
                ResolvePythonFileName(),
                arguments,
                Math.Max(1, _options.CheckTimeoutSeconds),
                cancellationToken);

            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var available = root.TryGetProperty("available", out var availableElement) &&
                availableElement.ValueKind == JsonValueKind.True;
            var missing = root.TryGetProperty("missing", out var missingElement) && missingElement.ValueKind == JsonValueKind.Array
                ? missingElement.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray()
                : [];
            var note = root.TryGetProperty("note", out var noteElement)
                ? noteElement.GetString() ?? string.Empty
                : string.Empty;

            return new LocalOcrEngineStatus(_engineName, available, missing, note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LocalOcrEngineStatus(
                _engineName,
                IsAvailable: false,
                Missing: [],
                ex.Message);
        }
    }

    private string ResolvePythonFileName()
    {
        if (!string.IsNullOrWhiteSpace(_options.VenvPythonPath))
        {
            var venvPython = PathResolver.Resolve(_contentRootPath, _options.VenvPythonPath);
            if (File.Exists(venvPython))
            {
                return venvPython;
            }
        }

        return string.IsNullOrWhiteSpace(_options.PythonFileName)
            ? "python"
            : _options.PythonFileName;
    }

    private string ResolveScriptPath()
        => PathResolver.Resolve(_contentRootPath, _options.ScriptPath);

    private static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start OCR command '{fileName}'.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Local OCR command timed out after {timeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Local OCR command failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        return new CommandResult(stdout, stderr);
    }

    private static string BuildArguments(params string[] values)
        => string.Join(" ", values.Select(Quote));

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static OcrProviderResult ParseOutput(string stdout, string fallbackEngine)
    {
        var trimmed = stdout.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new OcrProviderResult(string.Empty, Array.Empty<OcrTextBlock>(), fallbackEngine);
        }

        using var document = JsonDocument.Parse(trimmed);
        var root = document.RootElement;
        var engine = root.TryGetProperty("engine", out var engineElement)
            ? engineElement.GetString() ?? fallbackEngine
            : fallbackEngine;
        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
        var blocks = root.TryGetProperty("blocks", out var blocksElement) && blocksElement.ValueKind == JsonValueKind.Array
            ? ParseBlocks(blocksElement)
            : Array.Empty<OcrTextBlock>();

        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.Join(Environment.NewLine, blocks.Select(block => block.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            blocks = [new OcrTextBlock(text, 1.0, null)];
        }

        return new OcrProviderResult(text, blocks, engine);
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

        return new OcrBoundingBox(
            GetInt(element, "x"),
            GetInt(element, "y"),
            GetInt(element, "width"),
            GetInt(element, "height"));
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

    private sealed record CommandResult(string Stdout, string Stderr);
}

public sealed record LocalOcrEngineStatus(
    string Engine,
    bool IsAvailable,
    IReadOnlyList<string> Missing,
    string Note);
