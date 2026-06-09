using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LocalTranslateHub.Core.Models;
using LocalTranslateHub.Core.Options;
using LocalTranslateHub.Core.Providers;
using LocalTranslateHub.Core.Storage;

namespace LocalTranslateHub.Core.Services;

public sealed class OcrService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly LocalTranslateHubOptions _options;
    private readonly OcrProviderRegistry _providers;
    private readonly IOcrMemoryStore _memoryStore;

    public OcrService(
        LocalTranslateHubOptions options,
        OcrProviderRegistry providers,
        IOcrMemoryStore memoryStore)
    {
        _options = options;
        _providers = providers;
        _memoryStore = memoryStore;
    }

    public async Task<OcrResponse> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        var providerName = Pick(request.Provider, _options.Ocr.DefaultProvider);
        var provider = _providers.GetRequired(providerName);
        var language = Pick(request.Language, _options.Ocr.DefaultLanguage);
        var profileId = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
        var normalizeWhitespace = request.NormalizeWhitespace ?? _options.Ocr.NormalizeWhitespace;
        var decoded = DecodeImage(request.ImageBase64, request.ImageMimeType, _options.Ocr.MaxImageBytes);

        var providerRequest = new OcrProviderRequest(
            decoded.ImageBytes,
            decoded.ImageMimeType,
            language,
            normalizeWhitespace);

        var stopwatch = Stopwatch.StartNew();
        var result = await provider.RecognizeAsync(providerRequest, cancellationToken);
        stopwatch.Stop();

        var rawBlocks = result.Blocks
            .Select(block => block with
            {
                Text = normalizeWhitespace ? NormalizeWhitespace(block.Text) : block.Text
            })
            .ToArray();

        var rawText = string.IsNullOrWhiteSpace(result.Text)
            ? string.Join(Environment.NewLine, rawBlocks.Select(block => block.Text).Where(value => !string.IsNullOrWhiteSpace(value)))
            : result.Text;

        if (normalizeWhitespace)
        {
            rawText = NormalizeWhitespace(rawText);
        }

        var corrections = await _memoryStore.ListCorrectionsAsync(
            profileId,
            language,
            limit: 200,
            activeOnly: true,
            cancellationToken);
        var correctionResult = ApplyCorrections(rawText, rawBlocks, corrections);
        if (correctionResult.AppliedCorrections.Count > 0)
        {
            await _memoryStore.RecordCorrectionUseAsync(
                correctionResult.AppliedCorrections.Select(item => item.CorrectionId).ToArray(),
                cancellationToken);
        }

        var eventId = Guid.NewGuid().ToString("N");
        await _memoryStore.AddEventAsync(
            new OcrEvent(
                eventId,
                profileId,
                sessionId,
                ComputeSha256(decoded.ImageBytes),
                decoded.ImageMimeType,
                language,
                provider.Descriptor.Name,
                result.Engine,
                rawText,
                correctionResult.Text,
                correctionResult.Blocks,
                correctionResult.AppliedCorrections,
                stopwatch.ElapsedMilliseconds,
                DateTimeOffset.UtcNow),
            cancellationToken);

        return new OcrResponse(
            eventId,
            correctionResult.Text,
            rawText,
            correctionResult.Blocks,
            correctionResult.AppliedCorrections,
            provider.Descriptor.Name,
            result.Engine,
            language,
            decoded.ImageMimeType,
            stopwatch.ElapsedMilliseconds);
    }

    private static DecodedOcrImage DecodeImage(string? imageBase64, string? imageMimeType, int maxImageBytes)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            throw new ArgumentException("imageBase64 is required.");
        }

        var payload = imageBase64.Trim();
        var mimeType = Pick(imageMimeType, "application/octet-stream");

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = payload.IndexOf(',');
            if (commaIndex < 0)
            {
                throw new ArgumentException("imageBase64 data URI is missing a comma separator.");
            }

            var metadata = payload[5..commaIndex];
            var semicolonIndex = metadata.IndexOf(';');
            if (semicolonIndex > 0)
            {
                mimeType = metadata[..semicolonIndex];
            }
            else if (!string.IsNullOrWhiteSpace(metadata))
            {
                mimeType = metadata;
            }

            payload = payload[(commaIndex + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("imageBase64 must be valid base64.", ex);
        }

        if (bytes.Length == 0)
        {
            throw new ArgumentException("imageBase64 decoded to an empty payload.");
        }

        if (bytes.Length > maxImageBytes)
        {
            throw new ArgumentException($"image payload is too large. Max size is {maxImageBytes} bytes.");
        }

        return new DecodedOcrImage(bytes, mimeType);
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeWhitespace(string text)
        => WhitespaceRegex.Replace(text.Trim(), " ");

    private static CorrectionResult ApplyCorrections(
        string text,
        IReadOnlyList<OcrTextBlock> blocks,
        IReadOnlyList<OcrCorrection> corrections)
    {
        var correctedText = text;
        var correctedBlocks = blocks.ToArray();
        var applied = new List<AppliedOcrCorrection>();

        foreach (var correction in corrections)
        {
            if (string.IsNullOrEmpty(correction.WrongText) ||
                string.Equals(correction.WrongText, correction.CorrectedText, StringComparison.Ordinal))
            {
                continue;
            }

            var nextText = correctedText.Replace(correction.WrongText, correction.CorrectedText, StringComparison.Ordinal);
            var blockChanged = false;
            for (var index = 0; index < correctedBlocks.Length; index++)
            {
                var block = correctedBlocks[index];
                var nextBlockText = block.Text.Replace(correction.WrongText, correction.CorrectedText, StringComparison.Ordinal);
                if (!string.Equals(nextBlockText, block.Text, StringComparison.Ordinal))
                {
                    correctedBlocks[index] = block with { Text = nextBlockText };
                    blockChanged = true;
                }
            }

            if (!string.Equals(nextText, correctedText, StringComparison.Ordinal) || blockChanged)
            {
                correctedText = nextText;
                applied.Add(new AppliedOcrCorrection(
                    correction.Id,
                    correction.WrongText,
                    correction.CorrectedText));
            }
        }

        return new CorrectionResult(correctedText, correctedBlocks, applied);
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record DecodedOcrImage(byte[] ImageBytes, string ImageMimeType);

    private sealed record CorrectionResult(
        string Text,
        IReadOnlyList<OcrTextBlock> Blocks,
        IReadOnlyList<AppliedOcrCorrection> AppliedCorrections);
}
