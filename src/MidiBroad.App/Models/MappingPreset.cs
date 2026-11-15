namespace MidiBroad.Models;

public sealed record KeyBinding(int ScanCode, int Note, string Label, bool IsAbsolute = false);

public sealed class MappingPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<KeyBinding> Keys { get; init; }
    public bool IsDrum { get; init; }

    private Dictionary<int, KeyBinding>? _byScanCode;

    public KeyBinding? Find(int scanCode)
    {
        _byScanCode ??= Keys.ToDictionary(k => k.ScanCode);
        return _byScanCode.TryGetValue(scanCode, out var binding) ? binding : null;
    }

    public string LowNoteName => Keys.Count == 0 ? "—" : NoteNames.Get(Keys.Min(k => k.Note));
    public string HighNoteName => Keys.Count == 0 ? "—" : NoteNames.Get(Keys.Max(k => k.Note));

    public MappingPreset WithOverrides(IReadOnlyDictionary<int, CustomKeyBinding>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return this;
        }

        var keys = Keys
            .Select(binding =>
            {
                if (overrides.TryGetValue(binding.Note, out var custom))
                {
                    return binding with
                    {
                        ScanCode = custom.ScanCode,
                        Label = custom.Label
                    };
                }

                return binding;
            })
            .ToArray();

        return new MappingPreset
        {
            Id = Id,
            Name = Name,
            Description = Description,
            IsDrum = IsDrum,
            Keys = keys
        };
    }

    public override string ToString() => Name;
}

public static class NoteNames
{
    private static readonly string[] Names =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
    };

    public static string Get(int note)
    {
        if (note is < 0 or > 127)
        {
            return "—";
        }

        return $"{Names[note % 12]}{note / 12 - 1}";
    }

    public static string GetPitchClass(int note) => Names[((note % 12) + 12) % 12];

    public static bool IsBlack(int note) => note % 12 is 1 or 3 or 6 or 8 or 10;
}
