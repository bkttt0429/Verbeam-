using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrConcurrencyLimiterTests
{
    [Fact]
    public async Task WaitAsync_SerializesSameVlmProvider()
    {
        var limiter = CreateLimiter(options =>
        {
            options.Ocr.Concurrency.VlmMaxConcurrency = 1;
        });
        var maxActive = 0;
        var active = 0;
        var gate = new object();

        async Task RunAsync()
        {
            using var lease = await limiter.WaitAsync("paddleocr-vl", CancellationToken.None);
            var current = Interlocked.Increment(ref active);
            lock (gate)
            {
                maxActive = Math.Max(maxActive, current);
            }

            await Task.Delay(25);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(RunAsync(), RunAsync(), RunAsync());

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task WaitAsync_DoesNotBlockDifferentProviderLanes()
    {
        var limiter = CreateLimiter(options =>
        {
            options.Ocr.Concurrency.RealtimeMaxConcurrency = 1;
            options.Ocr.Concurrency.VlmMaxConcurrency = 1;
        });

        using var vlmLease = await limiter.WaitAsync("paddleocr-vl", CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        using var realtimeLease = await limiter.WaitAsync("external", timeout.Token);

        Assert.Equal(1, limiter.GetMaxConcurrency("paddleocr-vl"));
        Assert.Equal(1, limiter.GetMaxConcurrency("external"));
    }

    [Fact]
    public async Task WaitAsync_UsesProviderOverride()
    {
        var limiter = CreateLimiter(options =>
        {
            options.Ocr.Concurrency.VlmMaxConcurrency = 1;
            options.Ocr.Concurrency.Overrides["paddleocr-vl"] = 2;
        });
        var maxActive = 0;
        var active = 0;
        var gate = new object();

        async Task RunAsync()
        {
            using var lease = await limiter.WaitAsync("paddleocr-vl", CancellationToken.None);
            var current = Interlocked.Increment(ref active);
            lock (gate)
            {
                maxActive = Math.Max(maxActive, current);
            }

            await Task.Delay(25);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(RunAsync(), RunAsync(), RunAsync(), RunAsync());

        Assert.Equal(2, maxActive);
    }

    private static OcrConcurrencyLimiter CreateLimiter(Action<VerbeamOptions> configure)
    {
        var options = new VerbeamOptions();
        configure(options);
        return new OcrConcurrencyLimiter(options);
    }
}
