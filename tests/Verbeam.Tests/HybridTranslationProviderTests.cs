using Verbeam.Core.Models;
using Verbeam.Core.Providers;
using Xunit;

namespace Verbeam.Tests;

public class HybridTranslationProviderTests
{
    private static ProviderTranslationRequest Request(string text = "hello") => new(
        text,
        "en",
        "zh-TW",
        "game_dialogue",
        "model",
        new PromptPreset { Id = "p", Name = "p", SystemPrompt = "s", UserTemplate = "{TEXT}" },
        new Dictionary<string, string>(),
        string.Empty);

    private sealed class FakeProvider : ITranslationProvider
    {
        private readonly TimeSpan _delay;
        private readonly string? _result;
        private readonly Exception? _error;
        public int Calls;

        public FakeProvider(string name, TimeSpan delay, string? result, Exception? error = null)
        {
            Descriptor = new ProviderDescriptor(name, name, "test", "m", RequiresNetwork: false, IsLocal: true);
            _delay = delay;
            _result = result;
            _error = error;
        }

        public ProviderDescriptor Descriptor { get; }

        public async Task<ProviderTranslationResult> TranslateAsync(
            ProviderTranslationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(_delay, cancellationToken);
            if (_error is not null)
            {
                throw _error;
            }

            return new ProviderTranslationResult(_result!, $"{Descriptor.Name}:engine");
        }
    }

    [Fact]
    public async Task FastLocal_ReturnsLocal_WithoutCallingCloud()
    {
        var local = new FakeProvider("local", TimeSpan.FromMilliseconds(10), "L");
        var cloud = new FakeProvider("cloud", TimeSpan.Zero, "C");
        var hybrid = new HybridTranslationProvider(() => local, () => cloud, deadlineMs: 300);

        var result = await hybrid.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("L", result.Text);
        Assert.Equal(0, cloud.Calls); // private fast path — cloud never touched
    }

    [Fact]
    public async Task SlowLocal_FallsBackToCloud()
    {
        var local = new FakeProvider("local", TimeSpan.FromSeconds(5), "L");
        var cloud = new FakeProvider("cloud", TimeSpan.FromMilliseconds(10), "C");
        var hybrid = new HybridTranslationProvider(() => local, () => cloud, deadlineMs: 50);

        var result = await hybrid.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("C", result.Text); // spike -> cloud rescue
        Assert.Equal(1, cloud.Calls);
    }

    [Fact]
    public async Task LocalFailsFast_FallsBackToCloud()
    {
        var local = new FakeProvider("local", TimeSpan.FromMilliseconds(5), null, new InvalidOperationException("boom"));
        var cloud = new FakeProvider("cloud", TimeSpan.FromMilliseconds(10), "C");
        var hybrid = new HybridTranslationProvider(() => local, () => cloud, deadlineMs: 500);

        var result = await hybrid.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("C", result.Text);
    }

    [Fact]
    public async Task CloudFails_LocalBackstopWins()
    {
        // Local runs past the deadline but eventually succeeds; cloud fails (e.g. no API key).
        var local = new FakeProvider("local", TimeSpan.FromMilliseconds(150), "L");
        var cloud = new FakeProvider("cloud", TimeSpan.FromMilliseconds(10), null, new InvalidOperationException("no key"));
        var hybrid = new HybridTranslationProvider(() => local, () => cloud, deadlineMs: 30);

        var result = await hybrid.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("L", result.Text); // cloud failed -> local backstop
    }

    [Fact]
    public async Task BothFail_Throws()
    {
        var local = new FakeProvider("local", TimeSpan.FromMilliseconds(80), null, new InvalidOperationException("local boom"));
        var cloud = new FakeProvider("cloud", TimeSpan.FromMilliseconds(10), null, new InvalidOperationException("cloud boom"));
        var hybrid = new HybridTranslationProvider(() => local, () => cloud, deadlineMs: 30);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hybrid.TranslateAsync(Request(), CancellationToken.None));
    }
}
