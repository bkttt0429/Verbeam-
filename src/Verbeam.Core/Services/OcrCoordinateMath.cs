using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>A block box in normalized 0..1 page space (origin top-left).</summary>
public readonly record struct NormalizedBox(double Nx, double Ny, double Nw, double Nh);

/// <summary>
/// Shared coordinate math for the PDF overlay editor and the layout-preserving exporters:
/// turns a block's bbox into normalized 0..1 page space so it overlays a PDF.js backdrop
/// (or re-renders into a PDF) at any DPI/scale. A user layout override (already normalized)
/// always wins; otherwise the IR bbox is divided by the reference space that matches the
/// block's engine — PDF points@72 for the text layer, rendered pixels for OCR blocks.
/// </summary>
public static class OcrCoordinateMath
{
    public static NormalizedBox NormalizeBox(OcrPageResult page, OcrBlock block, OcrBlockLayout? layout)
    {
        if (layout is { Nx: { } nx, Ny: { } ny, Nw: { } nw, Nh: { } nh })
        {
            return new NormalizedBox(Clamp01(nx), Clamp01(ny), Clamp01(nw), Clamp01(nh));
        }

        var bbox = block.BoundingBox;
        if (bbox is null || !TryResolveReference(page, block, out var referenceWidth, out var referenceHeight))
        {
            return new NormalizedBox(0, 0, 0, 0);
        }

        return new NormalizedBox(
            Clamp01(bbox.X / referenceWidth),
            Clamp01(bbox.Y / referenceHeight),
            Clamp01(bbox.Width / referenceWidth),
            Clamp01(bbox.Height / referenceHeight));
    }

    private static bool TryResolveReference(
        OcrPageResult page,
        OcrBlock block,
        out double referenceWidth,
        out double referenceHeight)
    {
        var isTextLayer = block.Engine.Equals("pdf-text", StringComparison.OrdinalIgnoreCase);
        if (isTextLayer && page.PageWidthPoints is > 0 && page.PageHeightPoints is > 0)
        {
            referenceWidth = page.PageWidthPoints.Value;
            referenceHeight = page.PageHeightPoints.Value;
            return true;
        }

        if (page.ImageWidth is > 0 && page.ImageHeight is > 0)
        {
            referenceWidth = page.ImageWidth.Value;
            referenceHeight = page.ImageHeight.Value;
            return true;
        }

        if (page.Width is > 0 && page.Height is > 0)
        {
            referenceWidth = page.Width.Value;
            referenceHeight = page.Height.Value;
            return true;
        }

        referenceWidth = 0;
        referenceHeight = 0;
        return false;
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
