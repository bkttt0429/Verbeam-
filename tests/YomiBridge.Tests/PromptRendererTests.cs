using YomiBridge.Core.Models;
using YomiBridge.Core.Services;

namespace YomiBridge.Tests;

public sealed class PromptRendererTests
{
    [Fact]
    public void Render_ExpandsLanguageCodesForModelPrompts()
    {
        var preset = new PromptPreset
        {
            Id = "test",
            Name = "Test",
            SystemPrompt = "system",
            UserTemplate = "Translate {TEXT} from {SOURCE} to {TARGET}."
        };

        var request = new ProviderTranslationRequest(
            "hello",
            "ja",
            "zh-TW",
            "game_dialogue",
            "model",
            preset,
            new Dictionary<string, string>(),
            string.Empty);

        var prompt = PromptRenderer.Render(request);

        Assert.Contains("Japanese", prompt.User);
        Assert.Contains("Traditional Chinese (Taiwan)", prompt.User);
        Assert.Contains("Traditional Chinese characters", prompt.User);
    }

    [Fact]
    public void Render_ReplacesExplicitContextToken()
    {
        var preset = new PromptPreset
        {
            Id = "test",
            Name = "Test",
            SystemPrompt = "system",
            UserTemplate = "Context:\n{CONTEXT}\n\nText:\n{TEXT}"
        };

        var request = new ProviderTranslationRequest(
            "line to translate",
            "en",
            "zh-TW",
            "web_article",
            "model",
            preset,
            new Dictionary<string, string>(),
            "Earlier chapter says Mina calls the device Star Key.");

        var prompt = PromptRenderer.Render(request);

        Assert.Contains("Mina calls the device Star Key", prompt.User);
        Assert.Contains("line to translate", prompt.User);
        Assert.DoesNotContain("{CONTEXT}", prompt.User);
    }

    [Fact]
    public void Render_PrependsContextWhenPresetHasNoContextToken()
    {
        var preset = new PromptPreset
        {
            Id = "test",
            Name = "Test",
            SystemPrompt = "system",
            UserTemplate = "Translate {TEXT}."
        };

        var request = new ProviderTranslationRequest(
            "hello",
            "en",
            "zh-TW",
            "web_article",
            "model",
            preset,
            new Dictionary<string, string>(),
            "Use a formal tone.");

        var prompt = PromptRenderer.Render(request);

        Assert.StartsWith("Background context", prompt.User);
        Assert.Contains("Use a formal tone.", prompt.User);
        Assert.Contains("Translate hello.", prompt.User);
    }
}
