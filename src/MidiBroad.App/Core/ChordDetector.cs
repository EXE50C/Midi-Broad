using System.IO;
using System.Text.Json;
using MidiBroad.Models;

namespace MidiBroad.Core;

public sealed record ChordMatch(
    string Name,
    string RootName,
    string Suffix,
    int RootNote,
    int RootPitchClass,
    string Degree,
    int DegreeSemitone,
    IReadOnlyList<int> Notes);

/// <summary>
/// Recognizes the chord shapes documented by the user. The lowest pressed note
/// is treated as the root, and intervals are compared as pitch classes so a 9th
/// can be voiced in any octave.
/// </summary>
public static class ChordDetector
{
    private sealed record Shape(string Suffix, int[] Intervals);

    private static readonly Shape[] DefaultShapes =
    {
        // Triads
        new(string.Empty, new[] { 0, 4, 7 }),
        new("m", new[] { 0, 3, 7 }),
        new("dim", new[] { 0, 3, 6 }),
        new("aug", new[] { 0, 4, 8 }),
        new("sus2", new[] { 0, 2, 7 }),
        new("sus4", new[] { 0, 5, 7 }),

        // Seventh chords
        new("maj7", new[] { 0, 4, 7, 11 }),
        new("7", new[] { 0, 4, 7, 10 }),
        new("m7", new[] { 0, 3, 7, 10 }),
        new("m7b5", new[] { 0, 3, 6, 10 }),
        new("dim7", new[] { 0, 3, 6, 9 }),
        new("mMaj7", new[] { 0, 3, 7, 11 }),
        new("augMaj7", new[] { 0, 4, 8, 11 }),

        // Sixths, ninths and suspended extensions
        new("6", new[] { 0, 4, 7, 9 }),
        new("m6", new[] { 0, 3, 7, 9 }),
        new("add9", new[] { 0, 4, 7, 14 }),
        new("9", new[] { 0, 4, 7, 10, 14 }),
        new("maj9", new[] { 0, 4, 7, 11, 14 }),
        new("m9", new[] { 0, 3, 7, 10, 14 }),
        new("7sus4", new[] { 0, 5, 7, 10 })
    };

    private static readonly Shape[] Shapes = LoadShapes();

    private static Shape[] LoadShapes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "chords.json");
        try
        {
            if (!File.Exists(path))
            {
                return DefaultShapes;
            }

            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!document.RootElement.TryGetProperty("shapes", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                Log.Warn($"Chord table '{path}' has no 'shapes' array. Using built-in table.");
                return DefaultShapes;
            }

            var loaded = new List<Shape>();
            foreach (var element in array.EnumerateArray())
            {
                if (!element.TryGetProperty("suffix", out var suffixElement) ||
                    suffixElement.ValueKind != JsonValueKind.String ||
                    !element.TryGetProperty("intervals", out var intervalsElement) ||
                    intervalsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var intervals = new List<int>();
                foreach (var interval in intervalsElement.EnumerateArray())
                {
                    if (interval.ValueKind == JsonValueKind.Number && interval.TryGetInt32(out var value))
                    {
                        intervals.Add(value);
                    }
                }

                var pitchClasses = intervals.Select(i => ((i % 12) + 12) % 12).Distinct().Count();
                if (intervals.Count >= 2 && pitchClasses >= 2)
                {
                    loaded.Add(new Shape(suffixElement.GetString() ?? string.Empty, intervals.ToArray()));
                }
            }

            if (loaded.Count > 0)
            {
                Log.Info($"Loaded {loaded.Count} chord shapes from '{path}'.");
                return loaded.ToArray();
            }

            Log.Warn($"Chord table '{path}' has no valid shapes. Using built-in table.");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read chord table '{path}'. Using built-in table.", ex);
        }

        return DefaultShapes;
    }

    private static readonly string[] DegreeNames =
    {
        "I", "♭II", "II", "♭III", "III", "IV", "♯IV", "V", "♭VI", "VI", "♭VII", "VII"
    };

    public static ChordMatch? Detect(IReadOnlyCollection<int> notes, int keyPitchClass)
    {
        var ordered = notes
            .Where(n => n is >= 0 and <= 127)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        if (ordered.Length < 2)
        {
            return null;
        }

        var root = ordered[0];
        var rootPc = ((root % 12) + 12) % 12;
        // Compare intervals relative to the lowest note, not absolute pitch
        // classes. Without this, only chords rooted on C matched.
        var relativePitchClasses = ordered
            .Select(n => (((n % 12) - rootPc) % 12 + 12) % 12)
            .Distinct()
            .ToHashSet();

        if (relativePitchClasses.Count < 2)
        {
            return null;
        }

        foreach (var shape in Shapes)
        {
            var shapePitchClasses = shape.Intervals.Select(i => (((i % 12) + 12) % 12)).Distinct().ToHashSet();
            if (shapePitchClasses.Count == relativePitchClasses.Count &&
                shapePitchClasses.SetEquals(relativePitchClasses))
            {
                var keyPc = ((keyPitchClass % 12) + 12) % 12;
                var degreeSemitone = ((rootPc - keyPc) % 12 + 12) % 12;
                var rootName = NoteNames.GetPitchClass(rootPc);
                return new ChordMatch(
                    rootName + shape.Suffix,
                    rootName,
                    shape.Suffix,
                    root,
                    rootPc,
                    DegreeNames[degreeSemitone],
                    degreeSemitone,
                    ordered);
            }
        }

        return null;
    }
}
