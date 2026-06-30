using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Verbeam.Core.Providers;

internal sealed class LocalPythonOcrWorker : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pythonFileName;
    private readonly string _workerScriptPath;
    private readonly int _timeoutSeconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _stderrLines = new();

    private Process? _process;
    private bool _disposed;

    public LocalPythonOcrWorker(
        string pythonFileName,
        string workerScriptPath,
        int timeoutSeconds)
    {
        _pythonFileName = pythonFileName;
        _workerScriptPath = workerScriptPath;
        _timeoutSeconds = Math.Max(1, timeoutSeconds);
    }

    public async Task<string> RecognizeAsync(
        string engine,
        string imagePath,
        string language,
        string preprocessingPreset,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var process = EnsureProcess();
                    return await SendRequestAsync(
                        process,
                        engine,
                        imagePath,
                        language,
                        preprocessingPreset,
                        cancellationToken);
                }
                catch when (attempt == 0 && !cancellationToken.IsCancellationRequested)
                {
                    StopProcess();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        throw new InvalidOperationException("Persistent OCR worker failed.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _gate.Dispose();
    }

    private Process EnsureProcess()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        if (!File.Exists(_workerScriptPath))
        {
            throw new InvalidOperationException($"Local OCR worker script was not found: {_workerScriptPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonFileName,
            Arguments = Quote(_workerScriptPath),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true
        };
        // Python inherits the console code page (e.g. cp950) for its own stdio unless told otherwise.
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start OCR worker '{_pythonFileName}'.");
        _ = Task.Run(() => DrainStderrAsync(_process));
        return _process;
    }

    private async Task<string> SendRequestAsync(
        Process process,
        string engine,
        string imagePath,
        string language,
        string preprocessingPreset,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var requestId = Guid.NewGuid().ToString("N");
        var requestJson = JsonSerializer.Serialize(
            new
            {
                id = requestId,
                engine,
                image = imagePath,
                language,
                preprocess = preprocessingPreset
            },
            JsonOptions);

        await process.StandardInput.WriteLineAsync(requestJson).WaitAsync(timeout.Token);
        await process.StandardInput.FlushAsync(timeout.Token);

        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException($"OCR worker exited without a response. {RecentStderr()}".Trim());
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var responseId = root.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OCR worker returned a mismatched response id.");
        }

        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString() ?? "OCR worker failed."
                : "OCR worker failed.";
            throw new InvalidOperationException(error);
        }

        if (!root.TryGetProperty("result", out var resultElement))
        {
            throw new InvalidOperationException("OCR worker response did not include a result.");
        }

        return resultElement.GetRawText();
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                _stderrLines.Enqueue(line);
                while (_stderrLines.Count > 20 && _stderrLines.TryDequeue(out _))
                {
                }
            }
        }
        catch
        {
        }
    }

    private string RecentStderr()
        => string.Join(" ", _stderrLines.TakeLast(5));

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

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
        finally
        {
            process.Dispose();
        }
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
