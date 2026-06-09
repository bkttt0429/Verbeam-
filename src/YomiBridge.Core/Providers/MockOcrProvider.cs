using System.Text;
using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

public sealed class MockOcrProvider : IOcrProvider
{
    public OcrProviderDescriptor Descriptor { get; } = new(
        "mock",
        "Mock OCR Provider",
        "test",
        "ja",
        RequiresExternalProcess: false,
        IsLocal: true);

    public Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = TryReadTextPayload(request.ImageBytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"[mock-ocr: no real OCR configured; received {request.ImageBytes.Length} bytes]";
        }

        IReadOnlyList<OcrTextBlock> blocks =
        [
            new OcrTextBlock(text, 1.0, null)
        ];

        return Task.FromResult(new OcrProviderResult(text, blocks, "mock"));
    }

    private static string TryReadTextPayload(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.All(IsReadableTextChar) ? text : string.Empty;
    }

    private static bool IsReadableTextChar(char value)
        => !char.IsControl(value) || char.IsWhiteSpace(value);
}
