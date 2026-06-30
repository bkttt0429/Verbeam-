using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class LlamaCppBackendResolverTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050 Ti Laptop GPU", GpuVendor.Nvidia)]
    [InlineData("NVIDIA GeForce GTX 1660", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuVendor.Amd)]
    [InlineData("Radeon Instinct MI300", GpuVendor.Amd)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendor.Intel)]
    [InlineData("Intel(R) Arc(TM) A770", GpuVendor.Intel)]
    [InlineData("Apple M3 Pro", GpuVendor.Apple)]
    [InlineData("Microsoft Basic Display Adapter", GpuVendor.Unknown)]
    [InlineData("", GpuVendor.Unknown)]
    public void ClassifyVendor_MapsKnownNames(string name, GpuVendor expected)
        => Assert.Equal(expected, LlamaCppBackendResolver.ClassifyVendor(name));

    [Fact]
    public void ParseNvidiaSmi_ReadsNameAndVram()
    {
        // Real output captured on this machine.
        const string csv = """
            name, memory.total [MiB], index
            NVIDIA GeForce RTX 3050 Ti Laptop GPU, 4096 MiB, 0
            """;

        var gpu = Assert.Single(LlamaCppBackendResolver.ParseNvidiaSmi(csv));
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", gpu.Name);
        Assert.Equal(4.0, gpu.VramGb);
        Assert.True(gpu.IsDedicated);
    }

    [Fact]
    public void ParseWmicNames_SkipsHeaderAndClassifies()
    {
        const string text = """
            Name
            NVIDIA GeForce RTX 3050 Ti Laptop GPU
            Intel(R) Iris(R) Xe Graphics
            """;

        var gpus = LlamaCppBackendResolver.ParseWmicNames(text);
        Assert.Equal(2, gpus.Count);
        Assert.Equal(GpuVendor.Nvidia, gpus[0].Vendor);
        Assert.Equal(GpuVendor.Intel, gpus[1].Vendor);
        Assert.All(gpus, gpu => Assert.Equal(0, gpu.VramGb)); // AdapterRAM unreliable -> 0
    }

    [Fact]
    public void ParseLspci_PicksDisplayControllers()
    {
        const string text = """
            00:02.0 VGA compatible controller: Intel Corporation Iris Xe Graphics
            01:00.0 3D controller: NVIDIA Corporation GA107M [GeForce RTX 3050 Ti Mobile]
            00:1f.0 ISA bridge: Intel Corporation Tiger Lake LPC Controller
            """;

        var gpus = LlamaCppBackendResolver.ParseLspci(text);
        Assert.Equal(2, gpus.Count);
        Assert.Equal(GpuVendor.Intel, gpus[0].Vendor);
        Assert.Equal(GpuVendor.Nvidia, gpus[1].Vendor);
    }

    [Fact]
    public void ParseSystemProfiler_ReadsAppleGpu()
    {
        const string text = """
            Graphics/Displays:
                Apple M3 Pro:
                  Chipset Model: Apple M3 Pro
                  Type: GPU
                  Bus: Built-In
                  Total Number of Cores: 18
            """;

        var gpu = Assert.Single(LlamaCppBackendResolver.ParseSystemProfiler(text));
        Assert.Equal(GpuVendor.Apple, gpu.Vendor);
        Assert.Equal("Apple M3 Pro", gpu.Name);
    }

    [Theory]
    // Windows + NVIDIA -> CUDA first, then Vulkan, then CPU.
    [InlineData("windows", "x64", GpuVendor.Nvidia, "cuda,vulkan,cpu")]
    // Windows + AMD -> HIP.
    [InlineData("windows", "x64", GpuVendor.Amd, "hip-radeon,vulkan,cpu")]
    // Windows + Intel only -> Vulkan.
    [InlineData("windows", "x64", GpuVendor.Intel, "vulkan,cpu")]
    // Linux + NVIDIA -> CUDA preferred (catalog may lack it -> store falls to vulkan).
    [InlineData("linux", "x64", GpuVendor.Nvidia, "cuda,vulkan,cpu")]
    // Linux + AMD -> ROCm.
    [InlineData("linux", "x64", GpuVendor.Amd, "rocm,vulkan,cpu")]
    public void ResolveFlavorPreferences_PerVendorPlatform(string platform, string arch, GpuVendor vendor, string expected)
    {
        var host = new HostHardware(platform, arch, [new DetectedGpu(vendor, "gpu", 8)]);
        Assert.Equal(expected.Split(','), LlamaCppBackendResolver.ResolveFlavorPreferences(host));
    }

    [Fact]
    public void ResolveFlavorPreferences_AppleSilicon_PrefersMetalBuild()
    {
        var host = new HostHardware("macos", "arm64", [new DetectedGpu(GpuVendor.Apple, "Apple M3", 0)]);
        Assert.Equal(new[] { "macos-arm64", "cpu" }, LlamaCppBackendResolver.ResolveFlavorPreferences(host));
    }

    [Fact]
    public void ResolveFlavorPreferences_NoGpu_CpuOnly()
    {
        var host = new HostHardware("windows", "x64", []);
        Assert.Equal(new[] { "cpu" }, LlamaCppBackendResolver.ResolveFlavorPreferences(host));
    }

    [Fact]
    public void ResolveFlavorPreferences_HybridLaptop_NvidiaWins()
    {
        // Intel iGPU + NVIDIA discrete: CUDA must come first regardless of order.
        var host = new HostHardware("windows", "x64",
        [
            new DetectedGpu(GpuVendor.Intel, "Iris Xe", 0),
            new DetectedGpu(GpuVendor.Nvidia, "RTX 3050 Ti", 4)
        ]);
        Assert.Equal(new[] { "cuda", "vulkan", "cpu" }, LlamaCppBackendResolver.ResolveFlavorPreferences(host));
    }

    [Theory]
    [InlineData("cuda", "CUDA_VISIBLE_DEVICES")]
    [InlineData("hip-radeon", "HIP_VISIBLE_DEVICES")]
    [InlineData("rocm", "HIP_VISIBLE_DEVICES")]
    [InlineData("vulkan", "GGML_VK_VISIBLE_DEVICES")]
    [InlineData("cpu", null)]
    [InlineData("macos-arm64", null)]
    public void DeviceEnvKeyForFlavor(string flavor, string? expected)
        => Assert.Equal(expected, LlamaCppBackendResolver.DeviceEnvKeyForFlavor(flavor));

    [Fact]
    public void ParseListDevices_ReadsBackendIndexNameVram()
    {
        // Real output captured on this machine (vulkan binary).
        const string text = """
            Available devices:
              Vulkan0: Intel(R) Iris(R) Xe Graphics (20342 MiB, 19574 MiB free)
              Vulkan1: NVIDIA GeForce RTX 3050 Ti Laptop GPU (3962 MiB, 3367 MiB free)
            """;

        var devices = LlamaCppBackendResolver.ParseListDevices(text);
        Assert.Equal(2, devices.Count);
        Assert.Equal("Vulkan", devices[0].Backend);
        Assert.Equal(0, devices[0].Index);
        Assert.Equal(GpuVendor.Intel, devices[0].Vendor);
        Assert.Equal(1, devices[1].Index);
        Assert.Equal(GpuVendor.Nvidia, devices[1].Vendor);
        Assert.Equal(3367, devices[1].FreeMib);
    }

    [Fact]
    public void PickDeviceIndex_PrefersDiscreteNvidiaOverLargerIgpu()
    {
        // The Intel iGPU reports 20 GB shared RAM > the NVIDIA's 4 GB, but the
        // NVIDIA must still win on vendor priority. This is the real trap.
        const string text = """
            Available devices:
              Vulkan0: Intel(R) Iris(R) Xe Graphics (20342 MiB, 19574 MiB free)
              Vulkan1: NVIDIA GeForce RTX 3050 Ti Laptop GPU (3962 MiB, 3367 MiB free)
            """;

        var devices = LlamaCppBackendResolver.ParseListDevices(text);
        Assert.Equal(1, LlamaCppBackendResolver.PickDeviceIndex(devices));
    }

    [Fact]
    public void PickDeviceIndex_CudaEnumeratesNvidiaAtZero()
    {
        const string text = """
            Available devices:
              CUDA0: NVIDIA GeForce RTX 3050 Ti Laptop GPU (3962 MiB, 3367 MiB free)
            """;

        var devices = LlamaCppBackendResolver.ParseListDevices(text);
        Assert.Equal(0, LlamaCppBackendResolver.PickDeviceIndex(devices));
    }

    [Fact]
    public void PickDeviceIndex_NoDevices_ReturnsNull()
        => Assert.Null(LlamaCppBackendResolver.PickDeviceIndex(LlamaCppBackendResolver.ParseListDevices("Available devices:")));

    [Fact]
    public void PickIntegratedDeviceIndex_PrefersIntelIgpuOverDiscreteNvidia()
    {
        // The mirror image of the discrete pick: integrated mode runs the LLM on the
        // Intel iGPU so a game keeps the NVIDIA card, even though the iGPU reports more
        // (shared) RAM. Vendor decides, not VRAM.
        const string text = """
            Available devices:
              Vulkan0: Intel(R) Iris(R) Xe Graphics (20342 MiB, 19574 MiB free)
              Vulkan1: NVIDIA GeForce RTX 3050 Ti Laptop GPU (3962 MiB, 3367 MiB free)
            """;

        var devices = LlamaCppBackendResolver.ParseListDevices(text);
        Assert.Equal(0, LlamaCppBackendResolver.PickIntegratedDeviceIndex(devices));
    }

    [Fact]
    public void PickIntegratedDeviceIndex_FallsBackToOnlyDiscreteWhenNoIgpuListed()
    {
        // No integrated adapter present (CUDA enumeration lists only NVIDIA): integrated
        // mode can't do better than the discrete card, so it returns that index rather
        // than null — the caller still pins a working device.
        const string text = """
            Available devices:
              CUDA0: NVIDIA GeForce RTX 3050 Ti Laptop GPU (3962 MiB, 3367 MiB free)
            """;

        var devices = LlamaCppBackendResolver.ParseListDevices(text);
        Assert.Equal(0, LlamaCppBackendResolver.PickIntegratedDeviceIndex(devices));
    }

    [Fact]
    public void PickIntegratedDeviceIndex_NoDevices_ReturnsNull()
        => Assert.Null(LlamaCppBackendResolver.PickIntegratedDeviceIndex(LlamaCppBackendResolver.ParseListDevices("Available devices:")));

    [Theory]
    [InlineData("auto", "cuda", "cuda")]          // compute-target auto → flavor passes through unchanged
    [InlineData("auto", "auto", "auto")]          // …including the "auto" sentinel the binary store resolves
    [InlineData(null, "vulkan", "vulkan")]        // null target behaves like auto
    [InlineData("integrated", "cuda", "vulkan")]  // integrated forces Vulkan even over a CUDA config
    [InlineData("INTEGRATED", "auto", "vulkan")]  // case-insensitive
    [InlineData("cpu", "cuda", "cpu")]            // cpu forces the CPU build
    public void EffectiveFlavor_AppliesComputeTargetOverride(string? target, string flavor, string expected)
        => Assert.Equal(expected, LlamaCppBackendResolver.EffectiveFlavor(target, flavor));
}
