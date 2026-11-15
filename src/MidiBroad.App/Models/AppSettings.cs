using System.Text.Json;
using System.Text.Json.Serialization;
using MidiBroad.Core;

namespace MidiBroad.Models;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum VelocityMode
{
    Fixed,
    Random
}

public enum PitchBendDirection
{
    Up,
    Down
}

public sealed class CustomKeyBinding
{
    public int ScanCode { get; set; }
    public string Label { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public string? SelectedDeviceId { get; set; }
    public string? SelectedDeviceName { get; set; }
    public string VirtualDeviceName { get; set; } = "Midi Broad";
    public string PresetId { get; set; } = "piano";
    public int MidiChannel { get; set; } = 1;
    public int BaseOctave { get; set; } = 3;
    public int Transpose { get; set; } = 0;
    public VelocityMode VelocityMode { get; set; } = VelocityMode.Random;
    public int FixedVelocity { get; set; } = 100;
    public int MinVelocity { get; set; } = 65;
    public int MaxVelocity { get; set; } = 110;
    public int PitchBendKeyScanCode { get; set; } = 0x39; // Space
    public int PitchBendRange { get; set; } = 2;
    public PitchBendDirection PitchBendDirection { get; set; } = PitchBendDirection.Up;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string AccentColor { get; set; } = "#8B5CF6";
    public string Language { get; set; } = "en";
    public string? BackgroundImagePath { get; set; }
    public bool AutoStart { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool ShowKeyLabels { get; set; } = true;
    public Dictionary<string, Dictionary<int, CustomKeyBinding>> CustomKeyMappings { get; set; } = new();
    public double WindowWidth { get; set; } = 1220;
    public double WindowHeight { get; set; } = 860;

    [JsonIgnore]
    public int ClampedMidiChannel => Math.Clamp(MidiChannel, 1, 16);

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.CustomKeyMappings = CloneMappings(CustomKeyMappings);
        return clone;
    }

    public void CopyFrom(AppSettings other)
    {
        SelectedDeviceId = other.SelectedDeviceId;
        SelectedDeviceName = other.SelectedDeviceName;
        VirtualDeviceName = other.VirtualDeviceName;
        PresetId = other.PresetId;
        MidiChannel = other.MidiChannel;
        BaseOctave = other.BaseOctave;
        Transpose = other.Transpose;
        VelocityMode = other.VelocityMode;
        FixedVelocity = other.FixedVelocity;
        MinVelocity = other.MinVelocity;
        MaxVelocity = other.MaxVelocity;
        PitchBendKeyScanCode = other.PitchBendKeyScanCode;
        PitchBendRange = other.PitchBendRange;
        PitchBendDirection = other.PitchBendDirection;
        Theme = other.Theme;
        AccentColor = other.AccentColor;
        Language = other.Language;
        BackgroundImagePath = other.BackgroundImagePath;
        AutoStart = other.AutoStart;
        AutoReconnect = other.AutoReconnect;
        ShowKeyLabels = other.ShowKeyLabels;
        CustomKeyMappings = CloneMappings(other.CustomKeyMappings);
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        Normalize();
    }

    private static Dictionary<string, Dictionary<int, CustomKeyBinding>> CloneMappings(
        IReadOnlyDictionary<string, Dictionary<int, CustomKeyBinding>> source)
    {
        return source.ToDictionary(
            preset => preset.Key,
            preset => preset.Value.ToDictionary(
                pair => pair.Key,
                pair => new CustomKeyBinding
                {
                    ScanCode = pair.Value.ScanCode,
                    Label = pair.Value.Label
                }),
            StringComparer.OrdinalIgnoreCase);
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(VirtualDeviceName))
        {
            VirtualDeviceName = "Midi Broad";
        }

        VirtualDeviceName = VirtualDeviceName.Trim();
        MidiChannel = Math.Clamp(MidiChannel, 1, 16);
        BaseOctave = Math.Clamp(BaseOctave, 0, 8);
        Transpose = Math.Clamp(Transpose, -36, 36);
        FixedVelocity = Math.Clamp(FixedVelocity, 1, 127);
        MinVelocity = Math.Clamp(MinVelocity, 1, 127);
        MaxVelocity = Math.Clamp(MaxVelocity, 1, 127);
        if (MinVelocity > MaxVelocity)
        {
            (MinVelocity, MaxVelocity) = (MaxVelocity, MinVelocity);
        }

        PitchBendRange = Math.Clamp(PitchBendRange, 1, 24);
        if (!AccentColor.StartsWith('#') || AccentColor.Length != 7)
        {
            AccentColor = "#8B5CF6";
        }

        Language = LanguageService.Normalize(Language);

        WindowWidth = Math.Clamp(WindowWidth, 1000, 4000);
        WindowHeight = Math.Clamp(WindowHeight, 680, 3000);

        CustomKeyMappings ??= new Dictionary<string, Dictionary<int, CustomKeyBinding>>();
        foreach (var preset in CustomKeyMappings.ToArray())
        {
            if (preset.Value is null)
            {
                CustomKeyMappings.Remove(preset.Key);
                continue;
            }

            foreach (var pair in preset.Value.ToArray())
            {
                if (pair.Value is null ||
                    pair.Value.ScanCode is < 1 or > 0xFF ||
                    string.IsNullOrWhiteSpace(pair.Value.Label))
                {
                    preset.Value.Remove(pair.Key);
                }
                else
                {
                    pair.Value.Label = pair.Value.Label.Trim();
                }
            }
        }
    }
}

public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
