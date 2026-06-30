using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public sealed class OneOcrProvider : IOcrProvider
{
    private const string OneOcrDllName = "oneocr.dll";
    private const string OneOcrModelName = "oneocr.onemodel";
    private const string OnnxRuntimeDllName = "onnxruntime.dll";
    private const string ModelKey = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
    private const long MaxRecognitionLineCount = 1000;

    private static readonly object DiscoveryLock = new();
    private static readonly object NativeLoaderLock = new();

    private static bool s_discoveryCached;
    private static OneOcrRuntime? s_cachedRuntime;
    private static string s_cachedNote = string.Empty;
    private static string? s_nativeDirectory;
    private static bool s_resolverRegistered;
    private static bool s_defaultDllDirectoriesSet;
    private static IntPtr s_dllDirectoryCookie;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly OneOcrRuntime _runtime;
    private bool _initialized;
    private long _pipeline;
    private long _processOptions;

    public OneOcrProvider(OcrProviderDescriptor descriptor, OneOcrRuntime runtime)
    {
        Descriptor = descriptor;
        _runtime = runtime;
    }

    public OcrProviderDescriptor Descriptor { get; }

    public static bool TryProbeAvailability(out string note)
    {
        var available = TryResolveRuntime(out _, out note);
        return available;
    }

    public static bool TryResolveRuntime(out OneOcrRuntime? runtime, out string note)
    {
        lock (DiscoveryLock)
        {
            if (s_discoveryCached)
            {
                runtime = s_cachedRuntime;
                note = s_cachedNote;
                return runtime is not null;
            }

            runtime = ResolveRuntimeCore(out note);
            s_cachedRuntime = runtime;
            s_cachedNote = note;
            s_discoveryCached = true;
            return runtime is not null;
        }
    }

    public async Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var sourceStream = new MemoryStream(request.ImageBytes, writable: false);
        using var source = new Bitmap(sourceStream);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var image = new NativeImage
            {
                Type = 1,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Unknown = 0,
                Stride = Math.Abs(bitmapData.Stride),
                Data = bitmapData.Scan0
            };

            await _runLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lines = RunOcr(image);
                var blocks = lines
                    .Select(line => new OcrTextBlock(line.Text, 1.0, BoundingBoxFrom(line.Box)))
                    .ToArray();
                var text = string.Join(Environment.NewLine, blocks
                    .Select(block => block.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

                return new OcrProviderResult(text, blocks, "oneocr:snipping-tool");
            }
            finally
            {
                _runLock.Release();
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var loadRuntime = PrepareRuntimeForLoad(_runtime);
            EnsureNativeLoader(loadRuntime);

            var createResult = NativeMethods.CreateOcrPipeline(
                loadRuntime.ModelPath,
                ModelKey,
                context: 0,
                out var pipeline);
            if (createResult != 0)
            {
                var utf16Result = NativeMethods.CreateOcrPipelineUtf16(
                    loadRuntime.ModelPath,
                    ModelKey,
                    context: 0,
                    out pipeline);
                if (utf16Result != 0)
                {
                    throw new InvalidOperationException(
                        $"OneOCR pipeline initialization failed. UTF-8 result={createResult}; UTF-16 result={utf16Result}; model={loadRuntime.ModelPath}");
                }
            }

            var optionsResult = NativeMethods.CreateOcrProcessOptions(out var processOptions);
            if (optionsResult != 0)
            {
                throw new InvalidOperationException($"OneOCR process options initialization failed: {optionsResult}");
            }

            var maxLinesResult = NativeMethods.OcrProcessOptionsSetMaxRecognitionLineCount(
                processOptions,
                MaxRecognitionLineCount);
            if (maxLinesResult != 0)
            {
                throw new InvalidOperationException($"OneOCR line limit configuration failed: {maxLinesResult}");
            }

            _pipeline = pipeline;
            _processOptions = processOptions;
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private IReadOnlyList<OneOcrLine> RunOcr(NativeImage image)
    {
        var runResult = NativeMethods.RunOcrPipeline(_pipeline, ref image, _processOptions, out var instance);
        if (runResult != 0)
        {
            if (runResult == 3)
            {
                return Array.Empty<OneOcrLine>();
            }

            throw new InvalidOperationException($"OneOCR RunOcrPipeline failed: {runResult}");
        }

        var countResult = NativeMethods.GetOcrLineCount(instance, out var lineCount);
        if (countResult != 0)
        {
            throw new InvalidOperationException($"OneOCR GetOcrLineCount failed: {countResult}");
        }

        var lines = new List<OneOcrLine>((int)Math.Min(lineCount, 4096));
        for (var i = 0L; i < lineCount; i++)
        {
            var lineResult = NativeMethods.GetOcrLine(instance, i, out var line);
            if (lineResult != 0 || line == 0)
            {
                continue;
            }

            var contentResult = NativeMethods.GetOcrLineContent(line, out var contentPtr);
            if (contentResult != 0 || contentPtr == IntPtr.Zero)
            {
                continue;
            }

            var boxResult = NativeMethods.GetOcrLineBoundingBox(line, out var boxPtr);
            if (boxResult != 0 || boxPtr == IntPtr.Zero)
            {
                continue;
            }

            var text = Marshal.PtrToStringUTF8(contentPtr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new OneOcrLine(text, Marshal.PtrToStructure<NativeBoundingBox>(boxPtr)));
        }

        return lines;
    }

    private static void EnsureNativeLoader(OneOcrRuntime runtime)
    {
        lock (NativeLoaderLock)
        {
            s_nativeDirectory = runtime.Directory;
            if (!s_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(OneOcrProvider).Assembly, ResolveNativeLibrary);
                s_resolverRegistered = true;
            }

            if (!s_defaultDllDirectoriesSet)
            {
                s_defaultDllDirectoriesSet = SetDefaultDllDirectories(NativeLoadLibrarySearchDefaultDirs);
            }

            if (s_dllDirectoryCookie == IntPtr.Zero)
            {
                s_dllDirectoryCookie = AddDllDirectory(runtime.Directory);
                if (s_dllDirectoryCookie == IntPtr.Zero && !SetDllDirectory(runtime.Directory))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"Failed to add OneOCR native directory '{runtime.Directory}' to the DLL search path. Win32 error={error}.");
                }
            }
        }
    }

    private static OneOcrRuntime PrepareRuntimeForLoad(OneOcrRuntime runtime)
    {
        if (!IsPackagedAppDirectory(runtime.Directory))
        {
            return runtime;
        }

        var stagedDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Verbeam",
            "oneocr",
            StableDirectoryId(runtime.Directory));
        Directory.CreateDirectory(stagedDirectory);

        foreach (var fileName in new[] { OneOcrDllName, OneOcrModelName, OnnxRuntimeDllName })
        {
            CopyIfChanged(
                Path.Combine(runtime.Directory, fileName),
                Path.Combine(stagedDirectory, fileName));
        }

        return runtime with
        {
            Directory = stagedDirectory,
            ModelPath = Path.Combine(stagedDirectory, OneOcrModelName),
            Note = runtime.Note + $" Runtime files are staged from the installed package to {stagedDirectory} at startup; they are not distributed with Verbeam."
        };
    }

    private static bool IsPackagedAppDirectory(string directory)
        => directory.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
           directory.Contains(@"/WindowsApps/", StringComparison.OrdinalIgnoreCase);

    private static string StableDirectoryId(string directory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(directory));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static void CopyIfChanged(string sourcePath, string destinationPath)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("OneOCR runtime file was not found.", sourcePath);
        }

        var destination = new FileInfo(destinationPath);
        if (destination.Exists &&
            destination.Length == source.Length &&
            destination.LastWriteTimeUtc >= source.LastWriteTimeUtc)
        {
            return;
        }

        File.Copy(source.FullName, destination.FullName, overwrite: true);
        File.SetLastWriteTimeUtc(destination.FullName, source.LastWriteTimeUtc);
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(OneOcrDllName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(s_nativeDirectory))
        {
            return IntPtr.Zero;
        }

        var libraryPath = Path.Combine(s_nativeDirectory, OneOcrDllName);
        return File.Exists(libraryPath) ? NativeLibrary.Load(libraryPath) : IntPtr.Zero;
    }

    private static OneOcrRuntime? ResolveRuntimeCore(out string note)
    {
        if (!OperatingSystem.IsWindows())
        {
            note = "OneOCR is only available on Windows.";
            return null;
        }

        foreach (var candidate in EnumerateCandidateDirectories())
        {
            if (!HasRequiredFiles(candidate.Directory, out var missing))
            {
                continue;
            }

            var modelPath = Path.GetFullPath(Path.Combine(candidate.Directory, OneOcrModelName));
            note = $"Found Microsoft OneOCR runtime from {candidate.Source}: {candidate.Directory}.";
            return new OneOcrRuntime(
                Path.GetFullPath(candidate.Directory),
                modelPath,
                candidate.Source,
                note);
        }

        note = "Microsoft OneOCR runtime was not found. Install or update Windows Snipping Tool; Verbeam does not bundle oneocr.dll, oneocr.onemodel, or onnxruntime.dll.";
        return null;
    }

    private static IEnumerable<OneOcrCandidateDirectory> EnumerateCandidateDirectories()
    {
        foreach (var variable in new[] { "VERBEAM_ONEOCR_DIR", "VB_ONEOCR_DIR" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return new OneOcrCandidateDirectory(value.Trim(), variable);
            }
        }

        foreach (var packageName in new[] { "Microsoft.ScreenSketch", "Microsoft.Windows.Photos" })
        {
            var installLocation = GetAppxInstallLocation(packageName);
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                continue;
            }

            yield return new OneOcrCandidateDirectory(
                Path.Combine(installLocation, "SnippingTool"),
                packageName + "/SnippingTool");
            yield return new OneOcrCandidateDirectory(installLocation, packageName);
        }
    }

    private static bool HasRequiredFiles(string directory, out IReadOnlyList<string> missing)
    {
        var values = new List<string>();
        foreach (var fileName in new[] { OneOcrDllName, OneOcrModelName, OnnxRuntimeDllName })
        {
            if (!File.Exists(Path.Combine(directory, fileName)))
            {
                values.Add(fileName);
            }
        }

        missing = values;
        return values.Count == 0;
    }

    private static string? GetAppxInstallLocation(string packageName)
    {
        try
        {
            var command = $"(Get-AppxPackage -Name '{packageName}' | Select-Object -First 1 -ExpandProperty InstallLocation)";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + QuotePowerShell(command),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(milliseconds: 5000))
            {
                TryKill(process);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"OneOCR AppX discovery failed for {packageName}: {ex.Message}");
            return null;
        }
    }

    private static string QuotePowerShell(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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

    private static OcrBoundingBox? BoundingBoxFrom(NativeBoundingBox box)
    {
        var minX = Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4));
        var minY = Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4));
        var maxX = Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4));
        var maxY = Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4));
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new OcrBoundingBox(
            Math.Max(0, (int)Math.Floor(minX)),
            Math.Max(0, (int)Math.Floor(minY)),
            Math.Max(1, (int)Math.Ceiling(width)),
            Math.Max(1, (int)Math.Ceiling(height)));
    }

    private const uint NativeLoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory([MarshalAs(UnmanagedType.LPWStr)] string newDirectory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage
    {
        public int Type;
        public int Width;
        public int Height;
        public int Unknown;
        public long Stride;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBoundingBox
    {
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public float X3;
        public float Y3;
        public float X4;
        public float Y4;
    }

    private static class NativeMethods
    {
        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long GetOcrLineCount(long instance, out long count);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long GetOcrLine(long instance, long index, out long line);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long GetOcrLineContent(long line, out IntPtr content);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long GetOcrLineBoundingBox(long line, out IntPtr boundingBoxPtr);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long OcrProcessOptionsSetMaxRecognitionLineCount(long options, long count);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long RunOcrPipeline(
            long pipeline,
            ref NativeImage image,
            long options,
            out long instance);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long CreateOcrProcessOptions(out long options);

        [DllImport(OneOcrDllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern long CreateOcrPipeline(
            string modelPath,
            string key,
            long context,
            out long pipeline);

        [DllImport(OneOcrDllName, EntryPoint = "CreateOcrPipeline", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern long CreateOcrPipelineUtf16(
            string modelPath,
            string key,
            long context,
            out long pipeline);
    }

    private sealed record OneOcrCandidateDirectory(string Directory, string Source);
    private sealed record OneOcrLine(string Text, NativeBoundingBox Box);
}

public sealed record OneOcrRuntime(
    string Directory,
    string ModelPath,
    string Source,
    string Note);
