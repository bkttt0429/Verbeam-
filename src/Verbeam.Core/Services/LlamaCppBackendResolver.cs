using System.Globalization;
using System.Text.RegularExpressions;

namespace Verbeam.Core.Services;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Apple
}

/// <summary>One GPU as detected by an OS probe. <see cref="VramGb"/> is 0 when unknown.</summary>
public sealed record DetectedGpu(GpuVendor Vendor, string Name, double VramGb)
{
    public bool IsDedicated => Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Apple;
}

/// <summary>Host platform + GPUs, used to choose a llama.cpp backend flavor.</summary>
public sealed record HostHardware(string Platform, string Architecture, IReadOnlyList<DetectedGpu> Gpus);

/// <summary>One device row from `llama-server --list-devices`.</summary>
public sealed record ListedDevice(string Backend, int Index, string Name, double TotalMib, double FreeMib)
{
    public GpuVendor Vendor => LlamaCppBackendResolver.ClassifyVendor(Name);
}

/// <summary>
/// Pure (no I/O) helpers that turn raw OS/llama-server text into a llama.cpp
/// backend decision: which binary flavor to prefer, which device-selection env
/// var to set, and which device index to pick. The actual shelling-out lives in
/// <see cref="HardwareProbe"/>; everything here is unit-tested with captured
/// sample outputs so the cross-platform mapping is verifiable on any machine.
/// </summary>
public static class LlamaCppBackendResolver
{
    public static GpuVendor ClassifyVendor(string? name)
    {
        var value = (name ?? string.Empty).ToLowerInvariant();
        if (value.Length == 0)
        {
            return GpuVendor.Unknown;
        }

        if (value.Contains("nvidia") || value.Contains("geforce") || value.Contains("rtx") ||
            value.Contains("gtx") || value.Contains("quadro") || value.Contains("tesla"))
        {
            return GpuVendor.Nvidia;
        }

        if (value.Contains("radeon") || value.Contains("instinct") ||
            (value.Contains("amd") && !value.Contains("amd64")))
        {
            return GpuVendor.Amd;
        }

        if (value.Contains("apple") || value.Contains("m1") || value.Contains("m2") ||
            value.Contains("m3") || value.Contains("m4"))
        {
            return GpuVendor.Apple;
        }

        if (value.Contains("intel") || value.Contains("iris") || value.Contains("arc") ||
            value.Contains("uhd") || value.Contains("hd graphics"))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }

    // ---- OS probe parsers --------------------------------------------------

    /// <summary>Parses `nvidia-smi --query-gpu=name,memory.total,index --format=csv`.</summary>
    public static IReadOnlyList<DetectedGpu> ParseNvidiaSmi(string? csv)
    {
        var gpus = new List<DetectedGpu>();
        foreach (var line in SplitLines(csv))
        {
            // Skip the header row ("name, memory.total [MiB], index").
            if (line.StartsWith("name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            gpus.Add(new DetectedGpu(GpuVendor.Nvidia, parts[0], MibTextToGb(parts[1])));
        }

        return gpus;
    }

    /// <summary>Parses `lspci` lines mentioning VGA/3D/Display controllers (vendor only).</summary>
    public static IReadOnlyList<DetectedGpu> ParseLspci(string? text)
    {
        var gpus = new List<DetectedGpu>();
        foreach (var line in SplitLines(text))
        {
            if (!line.Contains("VGA compatible controller", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("3D controller", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Display controller", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = line;
            var colon = line.IndexOf(':');
            if (colon >= 0 && colon + 1 < line.Length)
            {
                // Drop the "00:02.0 VGA compatible controller:" prefix.
                var afterClass = line.IndexOf(':', colon + 1);
                name = (afterClass >= 0 ? line[(afterClass + 1)..] : line[(colon + 1)..]).Trim();
            }

            gpus.Add(new DetectedGpu(ClassifyVendor(name), name, 0));
        }

        return gpus;
    }

    /// <summary>Parses macOS `system_profiler SPDisplaysDataType` (Chipset Model / VRAM lines).</summary>
    public static IReadOnlyList<DetectedGpu> ParseSystemProfiler(string? text)
    {
        var gpus = new List<DetectedGpu>();
        string? name = null;
        double vram = 0;
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("Chipset Model:", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null)
                {
                    gpus.Add(new DetectedGpu(ClassifyVendor(name), name, vram));
                }

                name = line["Chipset Model:".Length..].Trim();
                vram = 0;
            }
            else if (line.StartsWith("VRAM", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    vram = SizeTextToGb(line[(colon + 1)..]);
                }
            }
        }

        if (name is not null)
        {
            gpus.Add(new DetectedGpu(ClassifyVendor(name), name, vram));
        }

        return gpus;
    }

    /// <summary>Parses Windows `wmic path win32_VideoController get name` (names only; AdapterRAM is unreliable so VRAM is left 0).</summary>
    public static IReadOnlyList<DetectedGpu> ParseWmicNames(string? text)
    {
        var gpus = new List<DetectedGpu>();
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            gpus.Add(new DetectedGpu(ClassifyVendor(line), line, 0));
        }

        return gpus;
    }

    // ---- Flavor decision ---------------------------------------------------

    /// <summary>
    /// Ordered backend-flavor preference for this host. The binary store picks the
    /// first flavor that actually has a catalog artifact for this platform/arch,
    /// falling back down the list (always ending at "cpu"). Decoupling preference
    /// from catalog contents keeps the fallback automatic and never silent.
    /// </summary>
    public static IReadOnlyList<string> ResolveFlavorPreferences(HostHardware host)
    {
        var platform = host.Platform.ToLowerInvariant();
        var arch = host.Architecture.ToLowerInvariant();

        if (platform == "macos")
        {
            // Metal is built into the stock macOS builds; flavor == platform build.
            return arch == "arm64" ? ["macos-arm64", "cpu"] : ["macos-x64", "cpu"];
        }

        var hasNvidia = host.Gpus.Any(gpu => gpu.Vendor == GpuVendor.Nvidia);
        var hasAmd = host.Gpus.Any(gpu => gpu.Vendor == GpuVendor.Amd);
        var hasAnyGpu = host.Gpus.Any(gpu => gpu.Vendor != GpuVendor.Unknown);

        var prefs = new List<string>();
        if (hasNvidia)
        {
            prefs.Add("cuda");
        }

        if (hasAmd)
        {
            prefs.Add(platform == "windows" ? "hip-radeon" : "rocm");
        }

        // Vulkan runs on every vendor (incl. NVIDIA/AMD as a fallback, and is the
        // best option for Intel/unknown discrete GPUs).
        if (hasAnyGpu)
        {
            prefs.Add("vulkan");
        }

        prefs.Add("cpu");
        return prefs;
    }

    public static string? DeviceEnvKeyForFlavor(string flavor)
        => flavor.ToLowerInvariant() switch
        {
            "cuda" => "CUDA_VISIBLE_DEVICES",
            "hip-radeon" or "rocm" => "HIP_VISIBLE_DEVICES",
            "vulkan" => "GGML_VK_VISIBLE_DEVICES",
            _ => null
        };

    /// <summary>
    /// The binary-flavor request after applying a compute-target override. "integrated"
    /// forces "vulkan" (the only backend that targets an Intel/AMD iGPU); "cpu" forces
    /// the CPU build; "auto" (or anything else) passes the configured flavor through
    /// unchanged, so the default discrete-GPU path is untouched.
    /// </summary>
    public static string EffectiveFlavor(string? computeTarget, string binaryFlavor)
        => (computeTarget ?? "auto").Trim().ToLowerInvariant() switch
        {
            "integrated" => "vulkan",
            "cpu" => "cpu",
            _ => binaryFlavor.Trim()
        };

    // ---- Device selection from `--list-devices` ----------------------------

    private static readonly Regex ListDeviceRegex = new(
        @"^\s*([A-Za-z]+)(\d+):\s*(.+?)\s*\((\d+)\s*MiB(?:,\s*(\d+)\s*MiB free)?\)",
        RegexOptions.Compiled);

    public static IReadOnlyList<ListedDevice> ParseListDevices(string? text)
    {
        var devices = new List<ListedDevice>();
        foreach (var line in SplitLines(text))
        {
            var match = ListDeviceRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var total = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            var free = match.Groups[5].Success
                ? double.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture)
                : total;
            devices.Add(new ListedDevice(match.Groups[1].Value, index, match.Groups[3].Value, total, free));
        }

        return devices;
    }

    /// <summary>
    /// Picks the device index to pin. Vendor priority dominates raw VRAM on
    /// purpose: an Intel iGPU reports shared system RAM (e.g. 20 GB) that dwarfs a
    /// 4 GB discrete NVIDIA card, so "largest VRAM" would wrongly pick the iGPU.
    /// Returns null when there is nothing better than CPU / no devices listed.
    /// </summary>
    public static int? PickDeviceIndex(IReadOnlyList<ListedDevice> devices)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        var best = devices
            .OrderBy(device => VendorRank(device.Vendor))
            .ThenByDescending(device => device.FreeMib)
            .First();

        // If every listed device is an integrated/unknown adapter, still pin the
        // ranked-best one (Vulkan/HIP enumerations can list only the iGPU).
        return best.Index;
    }

    private static int VendorRank(GpuVendor vendor)
        => vendor switch
        {
            GpuVendor.Nvidia => 0,
            GpuVendor.Amd => 1,
            GpuVendor.Apple => 2,
            GpuVendor.Intel => 3,
            _ => 4
        };

    /// <summary>
    /// Picks the INTEGRATED GPU index — the inverse of <see cref="PickDeviceIndex"/> —
    /// for "run the LLM on the iGPU so a game keeps the discrete card" mode. Vendor
    /// drives the choice, never VRAM (an Intel iGPU reports large shared system RAM, so
    /// size can't distinguish it): Intel first, then an AMD APU, then unknown adapters;
    /// a discrete NVIDIA/Apple device is chosen only if nothing integrated was listed.
    /// Returns null when no devices are listed (caller then leaves the device unpinned).
    /// </summary>
    public static int? PickIntegratedDeviceIndex(IReadOnlyList<ListedDevice> devices)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        var best = devices
            .OrderBy(device => IntegratedRank(device.Vendor))
            .ThenByDescending(device => device.FreeMib)
            .First();
        return best.Index;
    }

    private static int IntegratedRank(GpuVendor vendor)
        => vendor switch
        {
            GpuVendor.Intel => 0,
            GpuVendor.Amd => 1,
            GpuVendor.Unknown => 2,
            GpuVendor.Apple => 3,
            GpuVendor.Nvidia => 4,
            _ => 5
        };

    // ---- helpers -----------------------------------------------------------

    private static IEnumerable<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? []
            : text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static double MibTextToGb(string text)
    {
        var digits = new string(text.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var mib)
            ? Math.Round(mib / 1024.0, 2)
            : 0;
    }

    private static double SizeTextToGb(string text)
    {
        var lower = text.ToLowerInvariant();
        var digits = new string(lower.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (!double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        if (lower.Contains("mb") || lower.Contains("mib"))
        {
            return Math.Round(value / 1024.0, 2);
        }

        // Default to GB (system_profiler reports "VRAM (Total): 8 GB").
        return value;
    }
}
