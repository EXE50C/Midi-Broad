using MidiBroad.Models;

namespace MidiBroad.Core;

public sealed class MappingEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _activeNotes = new();
    private readonly Dictionary<int, int> _activeVelocities = new();
    private bool _pitchBendKeyDown;
    private int _octaveOffset;
    private AppSettings _settings;
    private MappingPreset _preset;

    public MappingEngine(AppSettings settings, MappingPreset preset)
    {
        _settings = settings;
        _preset = preset;
    }

    public event EventHandler<MidiMessage>? MessageReady;
    public event EventHandler? ActiveNotesChanged;

    public AppSettings Settings
    {
        get => _settings;
        set
        {
            lock (_gate)
            {
                _settings = value;
            }
        }
    }

    public MappingPreset Preset
    {
        get => _preset;
        set
        {
            lock (_gate)
            {
                _preset = value;
            }
        }
    }

    /// <summary>
    /// Temporary octave change used by the Shift performance key. It is not
    /// persisted; releasing Shift returns to the configured base octave.
    /// </summary>
    public int OctaveOffset
    {
        get
        {
            lock (_gate)
            {
                return _octaveOffset;
            }
        }
        set
        {
            lock (_gate)
            {
                _octaveOffset = Math.Clamp(value, -8, 8);
            }
        }
    }

    public IReadOnlyCollection<int> ActiveNotes
    {
        get
        {
            lock (_gate)
            {
                return _activeNotes.Values.Distinct().OrderBy(n => n).ToArray();
            }
        }
    }

    public IReadOnlyDictionary<int, int> ActiveVelocities
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<int, int>(_activeVelocities);
            }
        }
    }

    /// <summary>Processes one physical key transition. Returns true when the key was consumed.</summary>
    public bool Handle(KeyTransition transition)
    {
        MidiMessage? message = null;
        var activeChanged = false;

        lock (_gate)
        {
            if (transition.ScanCode == _settings.PitchBendKeyScanCode)
            {
                if (transition.IsDown && !_pitchBendKeyDown)
                {
                    _pitchBendKeyDown = true;
                    var value = _settings.PitchBendDirection == PitchBendDirection.Up ? 16383 : 0;
                    message = MidiMessage.PitchBend(_settings.ClampedMidiChannel, value);
                }
                else if (!transition.IsDown && _pitchBendKeyDown)
                {
                    _pitchBendKeyDown = false;
                    message = MidiMessage.PitchBend(_settings.ClampedMidiChannel, 8192);
                }

                if (message is not null)
                {
                    Emit(message.Value);
                }

                return message is not null;
            }

            var binding = _preset.Find(transition.ScanCode);
            if (binding is null)
            {
                return false;
            }

            if (transition.IsDown)
            {
                if (_activeNotes.ContainsKey(transition.ScanCode))
                {
                    return true; // ignore keyboard auto-repeat
                }

                var note = ResolveNote(binding);
                if (note is < 0 or > 127)
                {
                    return true;
                }

                var velocity = PickVelocity();
                _activeNotes[transition.ScanCode] = note;
                _activeVelocities[note] = velocity;
                message = MidiMessage.NoteOn(_settings.ClampedMidiChannel, note, velocity);
                activeChanged = true;
            }
            else
            {
                if (!_activeNotes.Remove(transition.ScanCode, out var note))
                {
                    return true;
                }

                _activeVelocities.Remove(note);
                message = MidiMessage.NoteOff(_settings.ClampedMidiChannel, note);
                activeChanged = true;
            }
        }

        if (activeChanged)
        {
            ActiveNotesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (message is not null)
        {
            Emit(message.Value);
        }

        return true;
    }

    public void Panic()
    {
        MidiMessage[] messages;
        lock (_gate)
        {
            messages = _activeNotes.Values
                .Distinct()
                .Select(note => MidiMessage.NoteOff(_settings.ClampedMidiChannel, note))
                .Concat(new[]
                {
                    MidiMessage.AllNotesOff(_settings.ClampedMidiChannel),
                    MidiMessage.PitchBend(_settings.ClampedMidiChannel, 8192)
                })
                .ToArray();

            _activeNotes.Clear();
            _activeVelocities.Clear();
            _pitchBendKeyDown = false;
        }

        foreach (var message in messages)
        {
            Emit(message);
        }

        ActiveNotesChanged?.Invoke(this, EventArgs.Empty);
    }

    private int ResolveNote(KeyBinding binding)
    {
        if (binding.IsAbsolute)
        {
            return binding.Note;
        }

        var root = (_settings.BaseOctave + _octaveOffset + 1) * 12;
        return root + binding.Note + _settings.Transpose;
    }

    private int PickVelocity()
    {
        if (_settings.VelocityMode == VelocityMode.Fixed)
        {
            return Math.Clamp(_settings.FixedVelocity, 1, 127);
        }

        var min = Math.Clamp(Math.Min(_settings.MinVelocity, _settings.MaxVelocity), 1, 127);
        var max = Math.Clamp(Math.Max(_settings.MinVelocity, _settings.MaxVelocity), 1, 127);
        return Random.Shared.Next(min, max + 1);
    }

    private void Emit(MidiMessage message) => MessageReady?.Invoke(this, message);
}
