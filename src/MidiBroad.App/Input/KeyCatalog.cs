namespace MidiBroad.Input;

public sealed record KeyOption(int ScanCode, string Name);

public static class KeyCatalog
{
    public static IReadOnlyList<KeyOption> PitchBendKeys { get; } = new[]
    {
        new KeyOption(0x39, "Space"),
        new KeyOption(0x2A, "Left Shift"),
        new KeyOption(0x0F, "Tab"),
        new KeyOption(0x3A, "Caps Lock"),
        new KeyOption(0x2B, "Backslash"),
        new KeyOption(0x1D, "Left Ctrl"),
        new KeyOption(0x38, "Left Alt")
    };

    public static IReadOnlyList<int> PitchBendRanges { get; } = new[] { 1, 2, 3, 4, 5, 7, 12 };

    public static string GetPitchBendKeyName(int scanCode) =>
        PitchBendKeys.FirstOrDefault(k => k.ScanCode == scanCode)?.Name ?? $"Scan {scanCode}";
}
