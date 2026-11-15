namespace MidiBroad.Models;

public static class PresetLibrary
{
    public const string PianoId = "piano";
    public const string ChromaticId = "chromatic";
    public const string DrumsId = "drums";

    public static IReadOnlyList<MappingPreset> All { get; } = new[]
    {
        CreatePiano(),
        CreateChromatic(),
        CreateDrums()
    };

    public static MappingPreset Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static MappingPreset GetEffective(
        string? id,
        IReadOnlyDictionary<string, Dictionary<int, CustomKeyBinding>> customMappings)
    {
        var preset = Get(id);
        if (!customMappings.TryGetValue(preset.Id, out var overrides))
        {
            return preset;
        }

        return preset.WithOverrides(overrides);
    }

    private static MappingPreset CreatePiano()
    {
        var keys = new List<KeyBinding>
        {
            new(0x2C, 0, "Z"),
            new(0x1F, 1, "S"),
            new(0x2D, 2, "X"),
            new(0x20, 3, "D"),
            new(0x2E, 4, "C"),
            new(0x2F, 5, "V"),
            new(0x22, 6, "G"),
            new(0x30, 7, "B"),
            new(0x23, 8, "H"),
            new(0x31, 9, "N"),
            new(0x24, 10, "J"),
            new(0x32, 11, "M"),
            new(0x10, 12, "Q"),
            new(0x03, 13, "2"),
            new(0x11, 14, "W"),
            new(0x04, 15, "3"),
            new(0x12, 16, "E"),
            new(0x13, 17, "R"),
            new(0x06, 18, "5"),
            new(0x14, 19, "T"),
            new(0x07, 20, "6"),
            new(0x15, 21, "Y"),
            new(0x08, 22, "7"),
            new(0x16, 23, "U"),
            new(0x17, 24, "I")
        };

        return new MappingPreset
        {
            Id = PianoId,
            Name = "Piano",
            Description = "Two rows, 25 keys, C to C",
            Keys = keys
        };
    }

    private static MappingPreset CreateChromatic()
    {
        var keys = new List<KeyBinding>
        {
            new(0x1E, 0, "A"),
            new(0x11, 1, "W"),
            new(0x1F, 2, "S"),
            new(0x12, 3, "E"),
            new(0x20, 4, "D"),
            new(0x21, 5, "F"),
            new(0x14, 6, "T"),
            new(0x22, 7, "G"),
            new(0x15, 8, "Y"),
            new(0x23, 9, "H"),
            new(0x16, 10, "U"),
            new(0x24, 11, "J"),
            new(0x25, 12, "K"),
            new(0x18, 13, "O"),
            new(0x26, 14, "L"),
            new(0x19, 15, "P"),
            new(0x27, 16, ";"),
            new(0x28, 17, "'")
        };

        return new MappingPreset
        {
            Id = ChromaticId,
            Name = "Chromatic",
            Description = "One continuous chromatic run",
            Keys = keys
        };
    }

    private static MappingPreset CreateDrums()
    {
        var keys = new List<KeyBinding>
        {
            new(0x2C, 36, "Z", true),
            new(0x2D, 38, "X", true),
            new(0x2E, 42, "C", true),
            new(0x2F, 46, "V", true),
            new(0x30, 45, "B", true),
            new(0x31, 47, "N", true),
            new(0x32, 50, "M", true),
            new(0x10, 49, "Q", true),
            new(0x11, 51, "W", true),
            new(0x12, 39, "E", true),
            new(0x13, 37, "R", true),
            new(0x14, 56, "T", true),
            new(0x15, 41, "Y", true),
            new(0x16, 43, "U", true)
        };

        return new MappingPreset
        {
            Id = DrumsId,
            Name = "GM Drums",
            Description = "General MIDI percussion pads",
            Keys = keys,
            IsDrum = true
        };
    }
}
