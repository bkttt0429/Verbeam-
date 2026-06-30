using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrCoordinateMathTests
{
    [Fact]
    public void TextLayerPoints_AndOcrPixels_NormalizeToSameBox()
    {
        // Same physical region on a Letter page: text layer is in points@72 (612x792),
        // the re-OCR'd backdrop is the same page at 180 DPI (1530x1980 px = 2.5x).
        var page = new OcrPageResult
        {
            PageIndex = 0,
            PageWidthPoints = 612,
            PageHeightPoints = 792,
            ImageWidth = 1530,
            ImageHeight = 1980,
            RenderDpi = 180
        };

        var textBlock = new OcrBlock
        {
            Engine = "pdf-text",
            BoundingBox = new OcrBoundingBox(153, 198, 306, 99) // points
        };
        var ocrBlock = new OcrBlock
        {
            Engine = "rapidocr-net",
            BoundingBox = new OcrBoundingBox(382, 495, 765, 247) // 2.5x pixels (rounded)
        };

        var fromText = OcrCoordinateMath.NormalizeBox(page, textBlock, layout: null);
        var fromOcr = OcrCoordinateMath.NormalizeBox(page, ocrBlock, layout: null);

        Assert.Equal(0.25, fromText.Nx, 3);
        Assert.Equal(0.25, fromText.Ny, 3);
        Assert.Equal(0.50, fromText.Nw, 3);
        Assert.Equal(0.125, fromText.Nh, 3);

        // The pixel-space OCR block lands on the same 0..1 box (within rounding).
        Assert.Equal(fromText.Nx, fromOcr.Nx, 2);
        Assert.Equal(fromText.Ny, fromOcr.Ny, 2);
        Assert.Equal(fromText.Nw, fromOcr.Nw, 2);
        Assert.Equal(fromText.Nh, fromOcr.Nh, 2);
    }

    [Fact]
    public void LayoutOverride_WinsOverBbox()
    {
        var page = new OcrPageResult { PageWidthPoints = 612, PageHeightPoints = 792 };
        var block = new OcrBlock { Engine = "pdf-text", BoundingBox = new OcrBoundingBox(0, 0, 100, 100) };
        var layout = new OcrBlockLayout("default", "job:0", "b1", DateTimeOffset.UtcNow)
        {
            Nx = 0.4,
            Ny = 0.5,
            Nw = 0.2,
            Nh = 0.1
        };

        var box = OcrCoordinateMath.NormalizeBox(page, block, layout);

        Assert.Equal(0.4, box.Nx, 6);
        Assert.Equal(0.5, box.Ny, 6);
        Assert.Equal(0.2, box.Nw, 6);
        Assert.Equal(0.1, box.Nh, 6);
    }

    [Fact]
    public void OutOfRangeBbox_IsClampedTo01()
    {
        var page = new OcrPageResult { PageWidthPoints = 100, PageHeightPoints = 100 };
        var block = new OcrBlock { Engine = "pdf-text", BoundingBox = new OcrBoundingBox(-20, 50, 200, 200) };

        var box = OcrCoordinateMath.NormalizeBox(page, block, layout: null);

        Assert.Equal(0.0, box.Nx, 6);
        Assert.Equal(0.5, box.Ny, 6);
        Assert.Equal(1.0, box.Nw, 6);
        Assert.Equal(1.0, box.Nh, 6);
    }

    [Fact]
    public void NoBboxAndNoOverride_YieldsZeroBox()
    {
        var page = new OcrPageResult { PageWidthPoints = 100, PageHeightPoints = 100 };
        var block = new OcrBlock { Engine = "pdf-text", BoundingBox = null };

        var box = OcrCoordinateMath.NormalizeBox(page, block, layout: null);

        Assert.Equal(new NormalizedBox(0, 0, 0, 0), box);
    }
}
