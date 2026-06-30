using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;

namespace Verbeam.Tests;

/// <summary>
/// Windows-runnable coverage for the macOS Apple Vision provider's C# half: registration gate,
/// descriptor identity, and the helper JSON contract + engine re-tag (driven through the real
/// ExternalCommandOcrProvider with a cross-platform stub command). The Swift helper + Vision runtime
/// itself is validated separately on a macOS CI runner (.github/workflows/vision-ocr.yml).
/// </summary>
public sealed class AppleVisionOcrProviderTests
{
    [Fact]
    public void TryProbeAvailability_OffMacOS_ReturnsFalse()
    {
        if (OperatingSystem.IsMacOS())
        {
            return; // on macOS the probe legitimately depends on whether the helper binary exists
        }

        var available = AppleVisionOcrProvider.TryProbeAvailability(
            AppContext.BaseDirectory, configuredPath: null, out var path, out var note);

        Assert.False(available);
        Assert.Empty(path);
        Assert.Contains("macOS", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_UsesAppleVisionDescriptor()
    {
        var provider = AppleVisionOcrProvider.Create(
            "/usr/local/bin/verbeam-vision-ocr", AppContext.BaseDirectory, "ja");

        Assert.Equal(AppleVisionOcrProvider.ProviderName, provider.Descriptor.Name);
        Assert.True(provider.Descriptor.IsLanguageAgnostic);
        Assert.True(provider.Descriptor.IsLocal);
    }

    [Fact]
    public async Task RecognizeAsync_ParsesHelperContract_AndReTagsEngine()
    {
        // The stub omits "engine" so the inner ExternalCommandOcrProvider would report
        // "external:powershell"; asserting the result engine is "apple-vision" proves the re-tag.
        // It also locks the JSON contract the Swift helper must emit:
        // text / blocks[].text / confidence:double / boundingBox:{x,y,width,height}:int.
        const string json =
            "{\"text\":\"HELLO OCR\\nOpen Settings\"," +
            "\"blocks\":[" +
            "{\"text\":\"HELLO OCR\",\"confidence\":0.98,\"boundingBox\":{\"x\":12,\"y\":8,\"width\":200,\"height\":40}}," +
            "{\"text\":\"Open Settings\",\"confidence\":0.91,\"boundingBox\":{\"x\":14,\"y\":60,\"width\":260,\"height\":36}}]}";

        var stubDir = Path.Combine(Path.GetTempPath(), "vb-vision-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stubDir);
        var stubPath = Path.Combine(stubDir, "stub.ps1");
        await File.WriteAllTextAsync(stubPath, "Write-Output '" + json + "'", new UTF8Encoding(false));

        try
        {
            var options = new ExternalOcrOptions
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{stubPath}\" {{image}} {{language}}",
                TimeoutSeconds = 30,
            };
            var descriptor = new OcrProviderDescriptor(
                AppleVisionOcrProvider.ProviderName, "Apple Vision (macOS)", "local-native", "ja",
                RequiresExternalProcess: true, IsLocal: true);
            var provider = new AppleVisionOcrProvider(descriptor, new ExternalCommandOcrProvider(options, stubDir));

            var request = new OcrProviderRequest(
                Encoding.ASCII.GetBytes("stub-image"), "image/png", "ja", NormalizeWhitespace: true);

            var result = await provider.RecognizeAsync(request, CancellationToken.None);

            Assert.Equal(AppleVisionOcrProvider.ProviderName, result.Engine);
            Assert.Contains("HELLO OCR", result.Text);
            Assert.Contains("Open Settings", result.Text);
            Assert.Equal(2, result.Blocks.Count);
            Assert.Equal("HELLO OCR", result.Blocks[0].Text);
            Assert.Equal(0.98, result.Blocks[0].Confidence, 3);

            var box = result.Blocks[0].BoundingBox;
            Assert.NotNull(box);
            Assert.Equal(12, box!.X);
            Assert.Equal(8, box.Y);
            Assert.Equal(200, box.Width);
            Assert.Equal(40, box.Height);
        }
        finally
        {
            try { Directory.Delete(stubDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
