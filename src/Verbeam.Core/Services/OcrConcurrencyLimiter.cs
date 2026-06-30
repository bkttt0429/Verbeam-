using System.Collections.Concurrent;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class OcrConcurrencyLimiter
{
    private const int MockMaxConcurrency = 16;
    private const int MaximumAllowedConcurrency = 64;

    private readonly VerbeamOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _limiters = new(StringComparer.OrdinalIgnoreCase);

    public OcrConcurrencyLimiter(VerbeamOptions options)
    {
        _options = options;
    }

    public async Task<IDisposable> WaitAsync(string providerName, CancellationToken cancellationToken = default)
    {
        var normalizedProviderName = NormalizeProviderName(providerName);
        var limiter = _limiters.GetOrAdd(normalizedProviderName, CreateLimiter);
        await limiter.WaitAsync(cancellationToken);
        return new Lease(limiter);
    }

    public int GetMaxConcurrency(string providerName)
        => ResolveMaxConcurrency(NormalizeProviderName(providerName));

    private SemaphoreSlim CreateLimiter(string providerName)
    {
        var maxConcurrency = ResolveMaxConcurrency(providerName);
        return new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    private int ResolveMaxConcurrency(string providerName)
    {
        var concurrency = _options.Ocr.Concurrency;
        var overrideValue = GetOverride(providerName, concurrency.Overrides);
        if (overrideValue is not null)
        {
            return Clamp(overrideValue.Value);
        }

        return providerName switch
        {
            "mock" => MockMaxConcurrency,
            "external" or "windows" or "oneocr" or "snipping-tool-ocr" => Clamp(concurrency.RealtimeMaxConcurrency),
            "tesseract" or "easyocr" or "paddleocr" or "rapidocr-net" or "rapidocr-ppocrv5" => Clamp(concurrency.TextMaxConcurrency),
            "pix2text" or "pp-structure-v3" => Clamp(concurrency.StructureMaxConcurrency),
            "paddleocr-vl" or "dots-ocr" or "deepseek-ocr-vlm" => Clamp(concurrency.VlmMaxConcurrency),
            _ => Clamp(concurrency.DefaultMaxConcurrency)
        };
    }

    private static int? GetOverride(string providerName, IReadOnlyDictionary<string, int> overrides)
    {
        if (overrides.TryGetValue(providerName, out var value))
        {
            return value;
        }

        foreach (var pair in overrides)
        {
            if (string.Equals(pair.Key, providerName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static int Clamp(int value)
        => Math.Clamp(value, 1, MaximumAllowedConcurrency);

    private static string NormalizeProviderName(string providerName)
        => string.IsNullOrWhiteSpace(providerName)
            ? "default"
            : providerName.Trim().ToLowerInvariant();

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _limiter;
        private int _disposed;

        public Lease(SemaphoreSlim limiter)
        {
            _limiter = limiter;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _limiter.Release();
            }
        }
    }
}
