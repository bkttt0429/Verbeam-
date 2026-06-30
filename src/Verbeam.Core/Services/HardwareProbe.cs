using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Verbeam.Core.Services;

/// <summary>
/// Detects the host platform and GPUs by shelling out to vendor tools, then
/// caches the result for the process lifetime (hardware does not change at
/// runtime). All parsing is delegated to the pure <see cref="LlamaCppBackendResolver"/>
/// so the OS-specific I/O here stays thin. Probe failures degrade to an
/// empty-GPU result (→ CPU backend), never throw.
/// </summary>
public sealed class HardwareProbe
{
    private readonly object _lock = new();
    private HostHardware? _cached;

    public HostHardware Detect()
    {
        lock (_lock)
        {
            return _cached ??= DetectCore();
        }
    }

    private static HostHardware DetectCore()
    {
        var platform = CurrentPlatform();
        var arch = CurrentArchitecture();
        var gpus = platform switch
        {
            "windows" => DetectWindowsGpus(),
            "linux" => DetectLinuxGpus(),
            "macos" => DetectMacGpus(arch),
            _ => []
        };

        return new HostHardware(platform, arch, gpus);
    }

    private static IReadOnlyList<DetectedGpu> DetectWindowsGpus()
    {
        var nvidia = LlamaCppBackendResolver.ParseNvidiaSmi(
            RunOrNull("nvidia-smi", "--query-gpu=name,memory.total,index --format=csv"));
        if (nvidia.Count > 0)
        {
            // nvidia-smi gives reliable VRAM; still add non-NVIDIA adapters (for
            // flavor decisions on hybrid laptops) from WMIC, deduped by vendor.
            var others = LlamaCppBackendResolver
                .ParseWmicNames(RunOrNull("wmic", "path win32_VideoController get name"))
                .Where(gpu => gpu.Vendor != GpuVendor.Nvidia);
            return [.. nvidia, .. others];
        }

        return LlamaCppBackendResolver.ParseWmicNames(
            RunOrNull("wmic", "path win32_VideoController get name"));
    }

    private static IReadOnlyList<DetectedGpu> DetectLinuxGpus()
    {
        var nvidia = LlamaCppBackendResolver.ParseNvidiaSmi(
            RunOrNull("nvidia-smi", "--query-gpu=name,memory.total,index --format=csv"));
        var lspci = LlamaCppBackendResolver.ParseLspci(RunOrNull("lspci", string.Empty));
        if (nvidia.Count > 0)
        {
            var others = lspci.Where(gpu => gpu.Vendor != GpuVendor.Nvidia);
            return [.. nvidia, .. others];
        }

        return lspci;
    }

    private static IReadOnlyList<DetectedGpu> DetectMacGpus(string arch)
    {
        var gpus = LlamaCppBackendResolver.ParseSystemProfiler(
            RunOrNull("system_profiler", "SPDisplaysDataType"));
        if (gpus.Count > 0)
        {
            return gpus;
        }

        // Apple Silicon always has an integrated Apple GPU even if the profiler
        // call failed; synthesize one so the resolver still picks the Metal build.
        return arch == "arm64" ? [new DetectedGpu(GpuVendor.Apple, "Apple GPU", 0)] : [];
    }

    public static string CurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macos";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "unknown";
    }

    public static string CurrentArchitecture()
        => RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant()
        };

    private static string? RunOrNull(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            // Tool not installed / not on PATH — caller treats null as "no data".
            return null;
        }
    }
}
