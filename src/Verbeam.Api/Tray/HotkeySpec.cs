using System.Windows.Forms;

namespace Verbeam.Api.Tray;

/// <summary>
/// Parses a "Mod+Mod+Key" hotkey spec (e.g. "Alt+Shift+R") into RegisterHotKey modifier flags and a
/// virtual-key code. Modifiers: Alt / Shift / Ctrl (Control) / Win (Windows); the key is a
/// <see cref="Keys"/> name. Pure and public so the parsing rules are unit-testable.
/// </summary>
public static class HotkeySpec
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static bool TryParse(string? spec, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        foreach (var token in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "ctrl":
                case "control": modifiers |= ModControl; break;
                case "win":
                case "windows": modifiers |= ModWin; break;
                default:
                    // Exactly one non-modifier key is allowed; reject a second key or an unknown token.
                    if (virtualKey != 0 || !Enum.TryParse<Keys>(token, ignoreCase: true, out var key) || key == Keys.None)
                    {
                        return false;
                    }

                    virtualKey = (uint)key;
                    break;
            }
        }

        return virtualKey != 0;
    }
}
