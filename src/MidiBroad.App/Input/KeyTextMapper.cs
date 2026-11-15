using System.Runtime.InteropServices;

namespace MidiBroad.Input;

/// <summary>
/// Converts a typed printable character into the physical scan code Windows
/// reports for the corresponding key on a US keyboard. Symbols that require
/// Shift intentionally reuse their base key's scan code; MBD maps by physical
/// key, not by the shifted character.
/// </summary>
public static class KeyTextMapper
{
    private const uint MapVkToVsc = 0;

    public static bool IsAllowedCharacter(char c)
    {
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
        {
            return true;
        }

        // Printable ASCII punctuation, excluding space (space is a performance key).
        return c is >= '!' and <= '~';
    }

    public static bool TryGetScanCode(string? text, out int scanCode, out string label)
    {
        scanCode = 0;
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.Length != 1 || !IsAllowedCharacter(value[0]))
        {
            return false;
        }

        var character = value[0];
        var layout = LoadKeyboardLayout("00000409", 0);
        if (layout == IntPtr.Zero)
        {
            return false;
        }

        var vkScan = VkKeyScanEx(character, layout);
        if (vkScan == -1)
        {
            return false;
        }

        var virtualKey = (byte)(vkScan & 0xFF);
        var mapped = MapVirtualKeyEx(virtualKey, MapVkToVsc, layout);
        if (mapped is 0 or > 0xFF)
        {
            return false;
        }

        scanCode = (int)mapped;
        label = char.IsLetter(character)
            ? char.ToUpperInvariant(character).ToString()
            : character.ToString();
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string layoutId, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanEx(char character, IntPtr keyboardLayout);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);
}
