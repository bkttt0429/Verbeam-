using Verbeam.Core.Providers;

namespace Verbeam.Tests;

public sealed class ExternalCommandTemplateTests
{
    // The default Windows OCR provider command line (see VerbeamOptions / appsettings Ocr:External).
    private const string WindowsOcrTemplate =
        "-NoProfile -ExecutionPolicy Bypass -File \"..\\..\\scripts\\windows_ocr_json.ps1\" -Image {image} -Language {language}";

    [Fact]
    public void BuildArguments_TokenizesWindowsTemplateAndSubstitutes()
    {
        var args = ExternalCommandTemplate.BuildArguments(
            WindowsOcrTemplate,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["{image}"] = @"C:\Temp\verbeam ocr.png",
                ["{language}"] = "zh-TW"
            });

        // Quotes around the script path are stripped to one token; the temp path (with a space) and
        // language land in their own slots — ProcessStartInfo.ArgumentList re-quotes each correctly.
        Assert.Equal(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                @"..\..\scripts\windows_ocr_json.ps1",
                "-Image",
                @"C:\Temp\verbeam ocr.png",
                "-Language",
                "zh-TW"
            },
            args);
    }

    [Theory]
    [InlineData("zh-TW\" -Command Calc")] // tries to break out of the (old) quoted arg
    [InlineData("zh\\")]                   // trailing backslash — defeats naive "..\" quoting
    [InlineData("a b c")]                  // embedded spaces
    [InlineData("\"; & calc.exe")]         // quotes + shell metacharacters
    public void BuildArguments_KeepsRequestValueAsSingleArgument(string language)
    {
        var args = ExternalCommandTemplate.BuildArguments(
            WindowsOcrTemplate,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["{image}"] = @"C:\Temp\img.png",
                ["{language}"] = language
            });

        // Whatever the value contains (quotes, backslashes, spaces, metacharacters), it occupies
        // exactly one argument slot and never adds tokens — no argument injection.
        Assert.Equal(9, args.Count);
        Assert.Equal(language, args[^1]);
    }

    [Fact]
    public void BuildArguments_NoSubstitutionsReturnsRawTokens()
    {
        var args = ExternalCommandTemplate.BuildArguments(
            "{audio} {language}",
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(new[] { "{audio}", "{language}" }, args);
    }
}
