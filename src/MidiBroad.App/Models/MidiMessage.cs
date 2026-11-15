namespace MidiBroad.Models;

public readonly record struct MidiMessage(byte Status, byte Data1, byte Data2)
{
    public int Kind => Status & 0xF0;
    public int Channel => (Status & 0x0F) + 1;

    public static MidiMessage NoteOn(int channel, int note, int velocity) =>
        new((byte)(0x90 | (Math.Clamp(channel, 1, 16) - 1)),
            (byte)Math.Clamp(note, 0, 127),
            (byte)Math.Clamp(velocity, 1, 127));

    public static MidiMessage NoteOff(int channel, int note) =>
        new((byte)(0x80 | (Math.Clamp(channel, 1, 16) - 1)),
            (byte)Math.Clamp(note, 0, 127),
            0);

    public static MidiMessage ControlChange(int channel, int controller, int value) =>
        new((byte)(0xB0 | (Math.Clamp(channel, 1, 16) - 1)),
            (byte)Math.Clamp(controller, 0, 127),
            (byte)Math.Clamp(value, 0, 127));

    public static MidiMessage PitchBend(int channel, int value14)
    {
        var value = Math.Clamp(value14, 0, 16383);
        return new((byte)(0xE0 | (Math.Clamp(channel, 1, 16) - 1)),
            (byte)(value & 0x7F),
            (byte)((value >> 7) & 0x7F));
    }

    public static MidiMessage AllNotesOff(int channel) =>
        ControlChange(channel, 123, 0);

    public override string ToString()
    {
        return Kind switch
        {
            0x90 when Data2 > 0 => $"Note On  {Data1}  vel {Data2}",
            0x80 or 0x90 => $"Note Off {Data1}",
            0xB0 => $"CC {Data1} = {Data2}",
            0xE0 => $"Pitch Bend {(Data2 << 7) | Data1}",
            _ => $"{Status:X2} {Data1:X2} {Data2:X2}"
        };
    }
}
