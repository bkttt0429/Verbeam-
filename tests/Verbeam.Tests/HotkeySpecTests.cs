using Verbeam.Api.Tray;

namespace Verbeam.Tests;

public sealed class HotkeySpecTests
{
    // Virtual-key codes (avoid a System.Windows.Forms.Keys reference the test project doesn't carry):
    // Keys.R == 0x52 (82), Keys.F1 == 0x70 (112).
    private const uint VkR = 82;
    private const uint VkF1 = 112;

    [Fact]
    public void ParsesModifiersAndKey()
    {
        Assert.True(HotkeySpec.TryParse("Alt+Shift+R", out var mods, out var vk));
        Assert.Equal(HotkeySpec.ModAlt | HotkeySpec.ModShift, mods);
        Assert.Equal(VkR, vk);
    }

    [Fact]
    public void ParsesCtrlAndFunctionKeyCaseInsensitive()
    {
        Assert.True(HotkeySpec.TryParse("ctrl+f1", out var mods, out var vk));
        Assert.Equal(HotkeySpec.ModControl, mods);
        Assert.Equal(VkF1, vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alt+Shift")]   // modifiers only, no key
    [InlineData("Alt+Bogus")]   // unknown key name
    [InlineData("R+L")]         // two non-modifier keys
    public void RejectsInvalidSpecs(string spec)
    {
        Assert.False(HotkeySpec.TryParse(spec, out _, out _));
    }
}
