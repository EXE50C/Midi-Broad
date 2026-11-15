using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MidiBroad.Controls;
using MidiBroad.Core;
using MidiBroad.Input;
using MidiBroad.Midi;
using MidiBroad.Models;

namespace MidiBroad;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<KeyboardDeviceInfo> _devices = new();
    private readonly ObservableCollection<MappingPreset> _presets = new();
    private readonly RawInputService _rawInput = new();
    private readonly KeyboardCaptureService _capture = new();
    private readonly DispatcherTimer _themeTimer;
    private readonly HashSet<int> _previewNotes = new();
    private readonly Dictionary<int, TextBox> _keyEditors = new();
    private readonly HashSet<int> _heldOctaveShifts = new();
    private readonly bool[] _pedals = new bool[4];

    private WmsMidiOutput _midi = new();
    private AppSettings _settings;
    private MappingEngine _engine;
    private KeyboardDeviceInfo? _selectedDevice;
    private bool _listening;
    private bool _suppressUi = true;
    private bool _allowClose;
    private bool _autoReconnectPending;
    private string? _autoReconnectDeviceId;
    private readonly bool _uiOnly;
    private bool _editingKeys;
    private bool _buildingKeyEditors;
    private Dictionary<string, Dictionary<int, CustomKeyBinding>>? _customMapSnapshot;
    private int _notesPlayed;
    private int _octaveOffset;

    public MainWindow()
    {
        InitializeComponent();

        _uiOnly = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--ui-only", StringComparison.OrdinalIgnoreCase));
        _settings = SettingsStore.Load();
        _engine = new MappingEngine(
            _settings,
            PresetLibrary.GetEffective(_settings.PresetId, _settings.CustomKeyMappings));

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        foreach (var preset in PresetLibrary.All)
        {
            _presets.Add(preset);
        }

        DeviceCombo.ItemsSource = _devices;
        PresetCombo.ItemsSource = _presets;
        VelocityModeCombo.ItemsSource = new[] { VelocityMode.Random, VelocityMode.Fixed };

        _suppressUi = true;
        PresetCombo.SelectedItem = _presets.FirstOrDefault(p => p.Id == _settings.PresetId) ?? _presets[0];
        VelocityModeCombo.SelectedItem = _settings.VelocityMode;

        var baseVelocity = _settings.VelocityMode == VelocityMode.Random
            ? (int)Math.Round((_settings.MinVelocity + _settings.MaxVelocity) / 2.0)
            : _settings.FixedVelocity;
        var humanize = _settings.VelocityMode == VelocityMode.Random
            ? Math.Max(0, (int)Math.Round((_settings.MaxVelocity - _settings.MinVelocity) / 2.0))
            : 0;

        VelocityBaseSlider.Value = Math.Clamp(baseVelocity, 1, 127);
        HumanizeSlider.Value = Math.Clamp(humanize, 0, 40);
        _suppressUi = false;

        _themeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _themeTimer.Tick += ThemeTimer_Tick;

        _rawInput.DevicesChanged += RawInput_DevicesChanged;
        _rawInput.KeySeen += RawInput_KeySeen;
        _capture.KeyCaptured += Capture_KeyCaptured;
        _capture.ReleaseRequested += Capture_ReleaseRequested;
        _capture.Error += Capture_Error;
        _capture.OctaveShiftRequested += Capture_OctaveShiftRequested;
        _capture.OctaveShiftReleased += Capture_OctaveShiftReleased;
        _capture.PedalToggleRequested += Capture_PedalToggleRequested;
        _capture.PitchBendRequested += Capture_PitchBendRequested;
        _capture.PitchBendReleased += Capture_PitchBendReleased;
        _capture.VelocityAdjustRequested += Capture_VelocityAdjustRequested;
        _midi.StateChanged += Midi_StateChanged;
        _engine.MessageReady += Engine_MessageReady;
        _engine.ActiveNotesChanged += Engine_ActiveNotesChanged;
        LanguageService.LanguageChanged += LanguageService_LanguageChanged;

        _capture.IsMappedKey = scanCode => _engine.Preset.Find(scanCode) is not null;
        _capture.PitchBendKeyScanCode = _settings.PitchBendKeyScanCode;

        ThemeService.Apply(_settings.Theme, _settings.AccentColor);
        ApplyBackgroundImage();
        Piano.ShowLabels = _settings.ShowKeyLabels;

        UpdatePresetUi();
        UpdateOctaveUi();
        UpdateTransposeUi();
        UpdateVelocityUi();
        UpdateMidiUi();
        UpdateListeningUi();
        UpdatePedalUi();

        KeyEditOverlay.SizeChanged += (_, _) =>
        {
            if (_editingKeys && !_buildingKeyEditors)
            {
                BuildKeyEditors();
            }
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _themeTimer.Start();
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = WindowState.Minimized;
        }

        if (!_uiOnly)
        {
            await StartMidiAsync();
        }

        _rawInput.RefreshDevices();
        UpdateListenAvailability();

        if (_uiOnly &&
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--listen", StringComparison.OrdinalIgnoreCase)))
        {
            StartListening();
        }

        if (_uiOnly &&
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--edit-keys", StringComparison.OrdinalIgnoreCase)))
        {
            EnterKeyEditMode();
        }

        ApplyLanguageToWindow();

        if (IntegrityCheck.IsLowIntegrity())
        {
            Log.Warn("Running at low integrity; background key capture is restricted.");
            NowPlayingSubText.Text = LanguageService.T(
                "Low integrity mode: extract Midi Broad to a normal folder for background key capture.");
        }

        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSelfTestAsync();
        }
    }

    private void LanguageService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyLanguageToWindow));
            return;
        }

        ApplyLanguageToWindow();
    }

    private void ApplyLanguageToWindow()
    {
        LanguageService.Apply(this);
        UpdatePresetUi();
        UpdateListeningUi();
        UpdateMidiUi();
        UpdateNowPlayingDisplay();
        LanguageService.Apply(NowPlayingSubText);
    }

    private async Task RunSelfTestAsync()
    {
        try
        {
            Log.Info("Selftest: begin.");
            if (_midi.State != MidiOutputState.Ready)
            {
                Log.Error("Selftest: virtual MIDI is not ready.");
                return;
            }

            await Task.Delay(4000);

            var keys = new[] { 0x2C, 0x2E, 0x30 }; // Z, C, B -> C, E, G
            foreach (var key in keys)
            {
                _engine.Handle(new KeyTransition(key, 0x5A, true, false, DateTime.UtcNow, null));
                await Task.Delay(300);
                _engine.Handle(new KeyTransition(key, 0x5A, false, false, DateTime.UtcNow, null));
                await Task.Delay(350);
            }

            await Task.Delay(1500);
            Log.Info("Selftest: Note On/Off sequence sent.");
        }
        finally
        {
            RequestExit();
        }
    }

    private async Task StartMidiAsync()
    {
        Log.Info("Checking Windows MIDI service and virtual endpoint.");
        var started = await TryStartMidiAsync();
        if (started)
        {
            UpdateMidiUi();
            return;
        }

        Log.Warn("Initial virtual MIDI startup failed; attempting service recovery.");
        NowPlayingSubText.Text =
            "Windows MIDI service is not ready — requesting administrator permission to restart it…";

        var repair = await MidiServiceRepair.RestartServiceAsync();
        if (repair != MidiServiceRepairResult.Success)
        {
            NowPlayingSubText.Text = repair switch
            {
                MidiServiceRepairResult.Cancelled =>
                    "MIDI service restart was cancelled. The virtual port is offline.",
                MidiServiceRepairResult.TimedOut =>
                    "MIDI service restart timed out. The virtual port is offline.",
                _ => "Could not restart the Windows MIDI service. The virtual port is offline."
            };
            UpdateMidiUi();
            return;
        }

        // A start attempt that timed out may still own a blocked WinRT call.
        // Abandon it and create a fresh output object before retrying.
        ReplaceMidiOutput();
        UpdateMidiUi();
        await Task.Delay(2000);

        NowPlayingSubText.Text = "MIDI service restarted — retrying the virtual port…";
        var retried = await TryStartMidiAsync();
        UpdateMidiUi();

        if (retried)
        {
            NowPlayingSubText.Text = "Virtual MIDI port is ready.";
        }
        else
        {
            NowPlayingSubText.Text =
                "Virtual MIDI is still offline. Restart Windows, then open Midi Broad again.";
        }
    }

    private async Task<bool> TryStartMidiAsync()
    {
        try
        {
            return await _midi.StartAsync(_settings.VirtualDeviceName);
        }
        catch (Exception ex)
        {
            Log.Error("Could not start virtual MIDI endpoint.", ex);
            return false;
        }
    }

    private void ReplaceMidiOutput()
    {
        var previous = _midi;
        previous.StateChanged -= Midi_StateChanged;

        _midi = new WmsMidiOutput();
        _midi.StateChanged += Midi_StateChanged;

        _ = Task.Run(() =>
        {
            try
            {
                previous.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Could not dispose the abandoned virtual MIDI output.", ex);
            }
        });
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _rawInput.Attach(hwnd);
        ThemeService.ApplyWindowChrome(this);
    }

    // ============================== MIDI status ==============================

    private void Midi_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateMidiUi));
            return;
        }

        UpdateMidiUi();
    }

    private void UpdateMidiUi()
    {
        if (!IsInitialized)
        {
            return;
        }

        var state = _midi.State;
        var ready = state == MidiOutputState.Ready;

        VirtualNameText.Text = string.IsNullOrWhiteSpace(_midi.EndpointName)
            ? _settings.VirtualDeviceName
            : _midi.EndpointName;
        VirtualStatusText.Text = LanguageService.T(_midi.StatusText);

        var dotBrush = state switch
        {
            MidiOutputState.Ready => (Brush)FindResource("SuccessBrush"),
            MidiOutputState.Starting => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("DangerBrush")
        };
        VirtualStatusDot.Fill = dotBrush;

        VirtualStatusChipText.Text = LanguageService.T(state switch
        {
            MidiOutputState.Ready => "READY",
            MidiOutputState.Starting => "STARTING",
            MidiOutputState.NotStarted => "OFFLINE",
            _ => "OFFLINE"
        });

        ChannelChipText.Text = LanguageService.F("MIDI CH {0}", _settings.ClampedMidiChannel);

        if (!ready && _listening && !_uiOnly)
        {
            StopListening("Virtual MIDI endpoint is not ready.");
        }

        UpdateListenAvailability();
    }

    private void UpdateListenAvailability()
    {
        ListenButton.IsEnabled = _listening ||
                                 ((_midi.State == MidiOutputState.Ready || _uiOnly) && _selectedDevice is not null);
        PanicButton.IsEnabled = _listening;
    }

    // ============================== Devices ==============================

    private void RawInput_DevicesChanged(object? sender, IReadOnlyList<KeyboardDeviceInfo> devices)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => RawInput_DevicesChanged(sender, devices)));
            return;
        }

        var previousId = _selectedDevice?.DeviceId ?? _settings.SelectedDeviceId;
        var wasListening = _listening;
        var selectedMissing = wasListening &&
                              !string.IsNullOrWhiteSpace(previousId) &&
                              devices.All(d => !string.Equals(d.DeviceId, previousId, StringComparison.Ordinal));

        _suppressUi = true;
        _devices.Clear();
        foreach (var device in devices)
        {
            _devices.Add(device);
        }

        var match = !string.IsNullOrWhiteSpace(previousId)
            ? _devices.FirstOrDefault(d => d.DeviceId == previousId)
            : null;
        match ??= _devices.FirstOrDefault();

        DeviceCombo.SelectedItem = match;
        _suppressUi = false;
        _selectedDevice = match;

        UpdateDeviceDetail();
        UpdateListenAvailability();

        if (selectedMissing)
        {
            _autoReconnectPending = _settings.AutoReconnect;
            _autoReconnectDeviceId = previousId;
            StopListening("Selected keyboard was disconnected.");
        }
        else if (!_listening && _autoReconnectPending && _settings.AutoReconnect &&
                 _selectedDevice is not null && _midi.State == MidiOutputState.Ready &&
                 (string.IsNullOrWhiteSpace(_autoReconnectDeviceId) ||
                  string.Equals(_selectedDevice.DeviceId, _autoReconnectDeviceId, StringComparison.Ordinal)))
        {
            _autoReconnectPending = false;
            _autoReconnectDeviceId = null;
            StartListening();
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi)
        {
            return;
        }

        _selectedDevice = DeviceCombo.SelectedItem as KeyboardDeviceInfo;
        if (_selectedDevice is not null)
        {
            _settings.SelectedDeviceId = _selectedDevice.DeviceId;
            _settings.SelectedDeviceName = _selectedDevice.DisplayName;
        }

        UpdateDeviceDetail();
        UpdateListenAvailability();

        if (_listening)
        {
            _autoReconnectPending = false;
            _autoReconnectDeviceId = null;
            StopListening("Input device changed.");
        }
    }

    private void UpdateDeviceDetail()
    {
        if (_selectedDevice is null)
        {
            DeviceDetailText.Text = _devices.Count == 0
                ? LanguageService.T("No physical keyboard found")
                : LanguageService.T("Select a keyboard");
            return;
        }

        DeviceDetailText.Text = string.IsNullOrWhiteSpace(_selectedDevice.Detail)
            ? _selectedDevice.BusType
            : $"{_selectedDevice.Detail}  ·  {_selectedDevice.BusType}";
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        _rawInput.RefreshDevices();
    }

    private void RawInput_KeySeen(object? sender, KeyTransition e)
    {
        if (!string.IsNullOrWhiteSpace(e.DeviceId))
        {
            var device = _devices.FirstOrDefault(d => d.RawDeviceName == e.DeviceId);
            if (device is not null)
            {
                LastInputText.Text = device.DisplayName;
            }
        }
    }

    // ============================== Capture ==============================

    private void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            _autoReconnectPending = false;
            _autoReconnectDeviceId = null;
            StopListening("Stopped by you.");
            return;
        }

        StartListening();
    }

    private void StartListening()
    {
        if (_selectedDevice is null)
        {
            NowPlayingSubText.Text = LanguageService.T("Select a keyboard first.");
            return;
        }

        if (!_uiOnly && _midi.State != MidiOutputState.Ready)
        {
            NowPlayingSubText.Text = LanguageService.T("Virtual MIDI endpoint is not ready.");
            return;
        }

        ClearPreviewNotes();
        ReleasePedals();
        ResetOctaveShift();
        _engine.Panic();
        _engine.Preset = GetEffectivePreset();
        _capture.IsMappedKey = scanCode => _engine.Preset.Find(scanCode) is not null;
        _capture.PitchBendKeyScanCode = _settings.PitchBendKeyScanCode;

        if (!_capture.Start(_selectedDevice.DisplayName))
        {
            NowPlayingSubText.Text = LanguageService.T("Could not install the keyboard hook.");
            return;
        }

        _autoReconnectPending = false;
        _autoReconnectDeviceId = null;
        _listening = true;
        _notesPlayed = 0;
        NotesPlayedText.Text = "0";
        LastVelocityText.Text = "—";
        SetNowPlayingIdle();
        NowPlayingSubText.Text = LanguageService.F("Listening to {0}.", _selectedDevice.DisplayName);
        LastInputText.Text = _selectedDevice.DisplayName;

        _settings.SelectedDeviceId = _selectedDevice.DeviceId;
        _settings.SelectedDeviceName = _selectedDevice.DisplayName;
        SettingsStore.Save(_settings);
        UpdateListeningUi();
    }

    private void StopListening(string reason)
    {
        if (!_listening)
        {
            ReleasePedals();
            ResetOctaveShift();
            _engine.Panic();
            ClearPreviewNotes();
            return;
        }

        _listening = false;
        _capture.Stop();
        ReleasePedals();
        ResetOctaveShift();
        _engine.Panic();
        ClearPreviewNotes();
        Piano.SetActiveNotes(Array.Empty<int>());
        ActiveNotesText.Text = "0";
        SetNowPlayingIdle();
        NowPlayingSubText.Text = LanguageService.T(reason);
        UpdateListeningUi();
        SettingsStore.Save(_settings);
    }

    private void Capture_ReleaseRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_listening)
            {
                StopListening("Released by Ctrl + Alt + Shift + F12.");
            }
        }));
    }

    private void Capture_KeyCaptured(object? sender, KeyTransition e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Capture_KeyCaptured(sender, e)));
            return;
        }

        _engine.Handle(e);
        if (_selectedDevice is not null)
        {
            LastInputText.Text = _selectedDevice.DisplayName;
        }
    }

    private void Capture_Error(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Capture_Error(sender, message)));
            return;
        }

        NowPlayingSubText.Text = message;
        StopListening(message);
    }

    private void Capture_OctaveShiftRequested(object? sender, int delta)
    {
        Dispatcher.BeginInvoke(new Action(() => SetOctaveShift(delta, true)));
    }

    private void Capture_OctaveShiftReleased(object? sender, int delta)
    {
        Dispatcher.BeginInvoke(new Action(() => SetOctaveShift(delta, false)));
    }

    private void SetOctaveShift(int delta, bool held)
    {
        var changed = held ? _heldOctaveShifts.Add(delta) : _heldOctaveShifts.Remove(delta);
        if (!changed)
        {
            return;
        }

        var requested = _heldOctaveShifts.Sum();
        var next = Math.Clamp(_settings.BaseOctave + requested, 0, 8) - _settings.BaseOctave;
        if (next == _octaveOffset)
        {
            return;
        }

        _engine.Panic();
        ClearPreviewNotes();
        _octaveOffset = next;
        _engine.OctaveOffset = next;
        UpdateOctaveUi();
        NowPlayingSubText.Text = next == 0
            ? "Octave restored."
            : $"Octave {(next > 0 ? "+" : string.Empty)}{next} while Shift is held.";
    }

    private void ResetOctaveShift()
    {
        _heldOctaveShifts.Clear();
        if (_octaveOffset == 0)
        {
            return;
        }

        _octaveOffset = 0;
        _engine.OctaveOffset = 0;
        UpdateOctaveUi();
    }

    private static readonly (string Name, int Controller)[] PedalDefinitions =
    {
        ("Soft", 67),
        ("Sostenuto", 66),
        ("Sustain", 64),
        ("Legato", 68)
    };

    private void TogglePedal(int index)
    {
        if (index < 0 || index >= _pedals.Length)
        {
            return;
        }

        if (!_listening || _midi.State != MidiOutputState.Ready)
        {
            NowPlayingSubText.Text = LanguageService.T("Start listening before using the pedals.");
            return;
        }

        _pedals[index] = !_pedals[index];
        var (name, controller) = PedalDefinitions[index];
        _midi.Send(MidiMessage.ControlChange(
            _settings.ClampedMidiChannel,
            controller,
            _pedals[index] ? 127 : 0));

        UpdatePedalUi();
        NowPlayingSubText.Text = LanguageService.F(
            "{0} pedal {1}.",
            LanguageService.T(name),
            LanguageService.T(_pedals[index] ? "on" : "off"));
    }

    private void ReleasePedals()
    {
        var changed = false;
        for (var i = 0; i < _pedals.Length; i++)
        {
            if (!_pedals[i])
            {
                continue;
            }

            if (_midi.State == MidiOutputState.Ready)
            {
                _midi.Send(MidiMessage.ControlChange(
                    _settings.ClampedMidiChannel,
                    PedalDefinitions[i].Controller,
                    0));
            }

            _pedals[i] = false;
            changed = true;
        }

        if (changed)
        {
            UpdatePedalUi();
        }
    }

    private void UpdatePedalUi()
    {
        var pedals = new[] { Pedal0, Pedal1, Pedal2, Pedal3 };
        for (var i = 0; i < pedals.Length; i++)
        {
            pedals[i].Opacity = _pedals[i] ? 1.0 : 0.32;
        }
    }

    private void Pedal_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index))
        {
            TogglePedal(index);
        }

        e.Handled = true;
    }

    private void Capture_PedalToggleRequested(object? sender, int index)
    {
        Dispatcher.BeginInvoke(new Action(() => TogglePedal(index)));
    }

    private void Capture_VelocityAdjustRequested(object? sender, int delta)
    {
        Dispatcher.BeginInvoke(new Action(() => AdjustVelocity(delta)));
    }

    private void Capture_PitchBendRequested(object? sender, int direction)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_midi.State != MidiOutputState.Ready)
            {
                return;
            }

            _midi.Send(MidiMessage.PitchBend(
                _settings.ClampedMidiChannel,
                direction > 0 ? 16383 : 0));
            NowPlayingSubText.Text = direction > 0 ? "Pitch bend up." : "Pitch bend down.";
        }));
    }

    private void Capture_PitchBendReleased(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_midi.State == MidiOutputState.Ready)
            {
                _midi.Send(MidiMessage.PitchBend(_settings.ClampedMidiChannel, 8192));
            }

            NowPlayingSubText.Text = _listening ? "Pitch bend centred." : "Idle.";
        }));
    }

    private void AdjustVelocity(int direction)
    {
        var baseVelocity = _settings.VelocityMode == VelocityMode.Random
            ? (int)Math.Round((_settings.MinVelocity + _settings.MaxVelocity) / 2.0)
            : _settings.FixedVelocity;
        baseVelocity = Math.Clamp(baseVelocity + direction * 5, 1, 127);

        _settings.FixedVelocity = baseVelocity;
        if (_settings.VelocityMode == VelocityMode.Random)
        {
            var spread = Math.Max(0, (int)Math.Round((_settings.MaxVelocity - _settings.MinVelocity) / 2.0));
            _settings.MinVelocity = Math.Clamp(baseVelocity - spread, 1, 127);
            _settings.MaxVelocity = Math.Clamp(baseVelocity + spread, 1, 127);
        }
        else
        {
            _settings.MinVelocity = baseVelocity;
            _settings.MaxVelocity = baseVelocity;
        }

        UpdateVelocityUi();
        SettingsStore.Save(_settings);
    }

    private void UpdateListeningUi()
    {
        ListenButton.Content = _listening ? "Stop listening" : "Start listening";
        if (_listening)
        {
            ListenButton.SetResourceReference(Control.BackgroundProperty, "DangerBrush");
            ListenButton.Foreground = Brushes.White;
        }
        else
        {
            ListenButton.ClearValue(Control.BackgroundProperty);
            ListenButton.ClearValue(Control.ForegroundProperty);
        }

        ListeningPulse.Fill = _listening
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");
        ListeningStateText.Text = LanguageService.T(_listening ? "LISTENING" : "IDLE");
        ModeChipText.Text = LanguageService.T(_listening ? "SOFT CAPTURE" : "SOFT MODE");
        HeaderSubtitleText.Text = _listening
            ? LanguageService.F("Mapping {0} to {1}.",
                _selectedDevice?.DisplayName ?? "keyboard",
                _settings.VirtualDeviceName)
            : LanguageService.T("Your spare keyboard is now a MIDI instrument.");
        PianoHintText.Text = _listening
            ? LanguageService.T("Soft capture · mapped keys are intercepted")
            : LanguageService.T("Keys light up as you play");
        UpdateListenAvailability();
    }

    private void PanicButton_Click(object sender, RoutedEventArgs e)
    {
        ReleasePedals();
        _engine.Panic();
        ClearPreviewNotes();
        Piano.SetActiveNotes(Array.Empty<int>());
        ActiveNotesText.Text = "0";
        SetNowPlayingIdle();
        NowPlayingSubText.Text = LanguageService.T("All notes off.");
    }

    // ============================== Mapping / piano ==============================

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || PresetCombo.SelectedItem is not MappingPreset preset)
        {
            return;
        }

        if (_editingKeys)
        {
            ExitKeyEditMode();
        }

        _engine.Panic();
        ClearPreviewNotes();
        _settings.PresetId = preset.Id;
        UpdatePresetUi();
        SettingsStore.Save(_settings);
    }

    private MappingPreset GetEffectivePreset() =>
        PresetLibrary.GetEffective(_settings.PresetId, _settings.CustomKeyMappings);

    private void UpdatePresetUi()
    {
        var preset = GetEffectivePreset();
        _engine.Preset = preset;

        _suppressUi = true;
        PresetCombo.SelectedItem = _presets.FirstOrDefault(p => p.Id == preset.Id) ?? _presets[0];
        _suppressUi = false;

        PresetDescriptionText.Text = LanguageService.T(preset.Description);
        UpdatePianoMapping();
        Piano.InvalidateVisual();
    }

    private void UpdatePianoMapping()
    {
        var preset = GetEffectivePreset();
        var effectiveBase = EffectiveBaseOctave;
        var low = preset.IsDrum ? 36 : (effectiveBase + 1) * 12;
        low = Math.Clamp(low, 0, 103);
        var labels = new Dictionary<int, string>();
        var root = (effectiveBase + 1) * 12;

        foreach (var key in preset.Keys)
        {
            var note = key.IsAbsolute ? key.Note : root + key.Note + _settings.Transpose;
            if (note is >= 0 and <= 127 && !labels.ContainsKey(note))
            {
                labels[note] = key.Label;
            }
        }

        Piano.SetMapping(low, 2, labels);
        PianoRangeText.Text = $"{NoteNames.Get(low)} — {NoteNames.Get(Math.Min(127, low + 24))}";
    }

    private int EffectiveBaseOctave => Math.Clamp(_settings.BaseOctave + _octaveOffset, 0, 8);

    private int ResolveBindingNote(KeyBinding binding)
    {
        if (binding.IsAbsolute)
        {
            return binding.Note;
        }

        return (EffectiveBaseOctave + 1) * 12 + binding.Note + _settings.Transpose;
    }

    private void EditKeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingKeys)
        {
            ExitKeyEditMode();
        }
        else
        {
            EnterKeyEditMode();
        }
    }

    private void EnterKeyEditMode()
    {
        if (_listening)
        {
            StopListening("Key editing started.");
        }

        _editingKeys = true;
        _customMapSnapshot = CloneCustomMappings(_settings.CustomKeyMappings);
        EditKeysButton.Content = LanguageService.T("Done");
        PianoHintText.Text = LanguageService.T("Type a key · Enter applies · Esc cancels");
        KeyEditOverlay.Visibility = Visibility.Visible;
        Piano.UpdateLayout();
        BuildKeyEditors();
    }

    private void ExitKeyEditMode(bool commit = true)
    {
        if (!_editingKeys)
        {
            return;
        }

        if (commit)
        {
            foreach (var editor in _keyEditors.Values.ToArray())
            {
                CommitKeyEditor(editor, false);
            }
        }
        else if (_customMapSnapshot is not null)
        {
            _settings.CustomKeyMappings = _customMapSnapshot;
        }

        _customMapSnapshot = null;
        _editingKeys = false;
        EditKeysButton.Content = LanguageService.T("Edit keys");
        PianoHintText.Text = LanguageService.T("Keys light up as you play");
        KeyEditOverlay.Children.Clear();
        _keyEditors.Clear();
        KeyEditOverlay.Visibility = Visibility.Collapsed;
        _engine.Preset = GetEffectivePreset();
        UpdatePianoMapping();
        SettingsStore.Save(_settings);
    }

    private static Dictionary<string, Dictionary<int, CustomKeyBinding>> CloneCustomMappings(
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

    private void BuildKeyEditors()
    {
        if (_buildingKeyEditors)
        {
            return;
        }

        _buildingKeyEditors = true;
        try
        {
            KeyEditOverlay.Children.Clear();
            _keyEditors.Clear();

            var preset = GetEffectivePreset();
            var bindingsByNote = new Dictionary<int, KeyBinding>();
            foreach (var binding in preset.Keys)
            {
                var note = ResolveBindingNote(binding);
                bindingsByNote.TryAdd(note, binding);
            }

            var layouts = Piano.GetKeyLayouts();
            foreach (var layout in layouts)
            {
                if (!bindingsByNote.TryGetValue(layout.Note, out var binding))
                {
                    continue;
                }

                var rect = layout.Rect;
                var width = Math.Clamp(rect.Width * 0.72, 22, 38);
                var top = layout.IsBlack ? rect.Bottom - 29 : rect.Bottom - 35;
                var left = rect.X + (rect.Width - width) / 2;

                var editor = new TextBox
                {
                    Text = binding.Label,
                    Width = width,
                    Height = 21,
                    FontSize = 10,
                    TextAlignment = TextAlignment.Center,
                    Padding = new Thickness(0),
                    MaxLength = 1,
                    Tag = binding.Note,
                    ToolTip = "Type a letter, digit or symbol"
                };
                editor.Style = (Style)FindResource("ModernTextBox");
                editor.Padding = new Thickness(0);
                editor.PreviewKeyDown += KeyEditor_PreviewKeyDown;
                editor.GotKeyboardFocus += (_, _) => editor.SelectAll();
                editor.LostKeyboardFocus += (_, _) =>
                {
                    if (!_buildingKeyEditors)
                    {
                        CommitKeyEditor(editor, false);
                    }
                };

                Canvas.SetLeft(editor, left);
                Canvas.SetTop(editor, top);
                KeyEditOverlay.Children.Add(editor);
                _keyEditors[binding.Note] = editor;
            }

            _keyEditors.Values.FirstOrDefault()?.Focus();
        }
        finally
        {
            _buildingKeyEditors = false;
        }
    }

    private void KeyEditor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox editor)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitKeyEditor(editor, true);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            ExitKeyEditMode(false);
            e.Handled = true;
        }
    }

    private void CommitKeyEditor(TextBox editor, bool moveFocus)
    {
        if (!_editingKeys || editor.Tag is not int bindingNote)
        {
            return;
        }

        var presetId = _settings.PresetId;
        var effective = GetEffectivePreset();
        var current = effective.Keys.FirstOrDefault(k => k.Note == bindingNote);
        if (current is null)
        {
            return;
        }

        if (!KeyTextMapper.TryGetScanCode(editor.Text, out var scanCode, out var label))
        {
            editor.Text = current.Label;
            NowPlayingSubText.Text = LanguageService.T("Use one English letter, digit or printable symbol.");
            return;
        }

        if (effective.Keys.Any(k => k.Note != bindingNote && k.ScanCode == scanCode))
        {
            editor.Text = current.Label;
            NowPlayingSubText.Text = LanguageService.F("{0} is already assigned to another note.", label);
            return;
        }

        if (!_settings.CustomKeyMappings.TryGetValue(presetId, out var customMap))
        {
            customMap = new Dictionary<int, CustomKeyBinding>();
            _settings.CustomKeyMappings[presetId] = customMap;
        }

        customMap[bindingNote] = new CustomKeyBinding
        {
            ScanCode = scanCode,
            Label = label
        };
        editor.Text = label;

        _engine.Preset = GetEffectivePreset();
        UpdatePianoMapping();
        SettingsStore.Save(_settings);
        NowPlayingSubText.Text = $"{label} → {NoteNames.Get(ResolveBindingNote(current))}.";

        if (moveFocus)
        {
            Piano.Focus();
        }
    }

    private void OctaveDown_Click(object sender, RoutedEventArgs e) => ChangeOctave(-1);
    private void OctaveUp_Click(object sender, RoutedEventArgs e) => ChangeOctave(1);

    private void ChangeOctave(int delta)
    {
        var next = Math.Clamp(_settings.BaseOctave + delta, 0, 8);
        if (next == _settings.BaseOctave && _octaveOffset == 0)
        {
            return;
        }

        _engine.Panic();
        ClearPreviewNotes();
        _heldOctaveShifts.Clear();
        _octaveOffset = 0;
        _engine.OctaveOffset = 0;
        _settings.BaseOctave = next;
        UpdateOctaveUi();
        SettingsStore.Save(_settings);
    }

    private void UpdateOctaveUi()
    {
        OctaveText.Text = EffectiveBaseOctave.ToString();
        UpdatePianoMapping();
    }

    private void TransposeDown_Click(object sender, RoutedEventArgs e) => ChangeTranspose(-1);
    private void TransposeUp_Click(object sender, RoutedEventArgs e) => ChangeTranspose(1);

    private void ChangeTranspose(int delta)
    {
        var next = Math.Clamp(_settings.Transpose + delta, -36, 36);
        if (next == _settings.Transpose)
        {
            return;
        }

        _engine.Panic();
        ClearPreviewNotes();
        _settings.Transpose = next;
        UpdateTransposeUi();
        SettingsStore.Save(_settings);
    }

    private void UpdateTransposeUi()
    {
        TransposeText.Text = _settings.Transpose > 0 ? $"+{_settings.Transpose}" : _settings.Transpose.ToString();
        UpdatePianoMapping();
    }

    // ============================== Velocity ==============================

    private void VelocityModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || VelocityModeCombo.SelectedItem is not VelocityMode mode)
        {
            return;
        }

        _settings.VelocityMode = mode;
        HumanizeSlider.IsEnabled = mode == VelocityMode.Random;
        UpdateVelocityValuesFromUi();
    }

    private void VelocityBaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi)
        {
            return;
        }

        UpdateVelocityValuesFromUi();
    }

    private void HumanizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi)
        {
            return;
        }

        UpdateVelocityValuesFromUi();
    }

    private void UpdateVelocityValuesFromUi()
    {
        if (_suppressUi)
        {
            return;
        }

        var baseVelocity = (int)Math.Round(VelocityBaseSlider.Value);
        var spread = (int)Math.Round(HumanizeSlider.Value);

        _settings.FixedVelocity = Math.Clamp(baseVelocity, 1, 127);
        if (_settings.VelocityMode == VelocityMode.Random)
        {
            _settings.MinVelocity = Math.Clamp(baseVelocity - spread, 1, 127);
            _settings.MaxVelocity = Math.Clamp(baseVelocity + spread, 1, 127);
        }
        else
        {
            _settings.MinVelocity = _settings.FixedVelocity;
            _settings.MaxVelocity = _settings.FixedVelocity;
            spread = 0;
        }

        VelocityBaseText.Text = baseVelocity.ToString();
        HumanizeText.Text = spread > 0 ? $"±{spread}" : "off";
    }

    private void UpdateVelocityUi()
    {
        _suppressUi = true;
        VelocityModeCombo.SelectedItem = _settings.VelocityMode;
        var baseVelocity = _settings.VelocityMode == VelocityMode.Random
            ? (int)Math.Round((_settings.MinVelocity + _settings.MaxVelocity) / 2.0)
            : _settings.FixedVelocity;
        var spread = _settings.VelocityMode == VelocityMode.Random
            ? Math.Max(0, (int)Math.Round((_settings.MaxVelocity - _settings.MinVelocity) / 2.0))
            : 0;
        VelocityBaseSlider.Value = Math.Clamp(baseVelocity, 1, 127);
        HumanizeSlider.Value = Math.Clamp(spread, 0, 40);
        HumanizeSlider.IsEnabled = _settings.VelocityMode == VelocityMode.Random;
        VelocityBaseText.Text = baseVelocity.ToString();
        HumanizeText.Text = spread > 0 ? $"±{spread}" : "off";
        _suppressUi = false;
    }

    private int PickVelocity()
    {
        if (_settings.VelocityMode == VelocityMode.Fixed)
        {
            return _settings.FixedVelocity;
        }

        var min = Math.Min(_settings.MinVelocity, _settings.MaxVelocity);
        var max = Math.Max(_settings.MinVelocity, _settings.MaxVelocity);
        return Random.Shared.Next(min, max + 1);
    }

    // ============================== Piano preview ==============================

    private void Piano_NotePressed(object? sender, PianoNoteEventArgs args)
    {
        var note = args.Note;
        if (_midi.State != MidiOutputState.Ready || _previewNotes.Contains(note))
        {
            return;
        }

        var velocity = args.Velocity;
        _previewNotes.Add(note);
        _midi.Send(MidiMessage.NoteOn(_settings.ClampedMidiChannel, note, velocity));
        Piano.SetActiveNotes(_engine.ActiveNotes.Concat(_previewNotes).ToArray(), _engine.ActiveVelocities);
        UpdateNowPlayingDisplay();
        if (_engine.ActiveNotes.Count == 0 && _previewNotes.Count == 1)
        {
            NowPlayingSubText.Text = LanguageService.F("Preview · velocity {0}", velocity);
        }

        LastVelocityText.Text = velocity.ToString();
        _notesPlayed++;
        NotesPlayedText.Text = _notesPlayed.ToString();
    }

    private void Piano_NoteReleased(object? sender, int note)
    {
        if (!_previewNotes.Remove(note))
        {
            return;
        }

        _midi.Send(MidiMessage.NoteOff(_settings.ClampedMidiChannel, note));
        Piano.SetActiveNotes(_engine.ActiveNotes.Concat(_previewNotes).ToArray(), _engine.ActiveVelocities);
        UpdateNowPlayingDisplay();
        if (_engine.ActiveNotes.Count == 0 && _previewNotes.Count == 0)
        {
            SetNowPlayingIdle();
            NowPlayingSubText.Text = _listening
                ? LanguageService.F("Listening to {0}.", _selectedDevice?.DisplayName ?? "keyboard")
                : LanguageService.T("Press Start listening to play.");
        }
    }

    private void ClearPreviewNotes()
    {
        if (_previewNotes.Count == 0)
        {
            return;
        }

        foreach (var note in _previewNotes)
        {
            _midi.Send(MidiMessage.NoteOff(_settings.ClampedMidiChannel, note));
        }

        _previewNotes.Clear();
    }

    // ============================== MIDI events ==============================

    private void Engine_MessageReady(object? sender, MidiMessage message)
    {
        _midi.Send(message);

        if (message.Kind == 0x90 && message.Data2 > 0)
        {
            _notesPlayed++;
            NotesPlayedText.Text = _notesPlayed.ToString();
            LastVelocityText.Text = message.Data2.ToString();
            UpdateNowPlayingDisplay();
        }
    }

    private void Engine_ActiveNotesChanged(object? sender, EventArgs e)
    {
        var notes = _engine.ActiveNotes;
        var combined = notes.Concat(_previewNotes).Distinct().OrderBy(n => n).ToArray();
        Piano.SetActiveNotes(combined, _engine.ActiveVelocities);
        ActiveNotesText.Text = notes.Count.ToString();

        if (combined.Length == 0)
        {
            SetNowPlayingIdle();
            NowPlayingSubText.Text = _listening
                ? LanguageService.F("Listening to {0}.", _selectedDevice?.DisplayName ?? "keyboard")
                : LanguageService.T("Press Start listening to play.");
            return;
        }

        UpdateNowPlayingDisplay();
    }

    private void UpdateNowPlayingDisplay()
    {
        var notes = _engine.ActiveNotes
            .Concat(_previewNotes)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        if (notes.Length == 0)
        {
            SetNowPlayingIdle();
            return;
        }

        if (notes.Length == 1)
        {
            var note = notes[0];
            SetNowPlayingNote(note);
            if (_engine.ActiveVelocities.TryGetValue(note, out var velocity))
            {
                NowPlayingSubText.Text = LanguageService.F(
                    "{0}  ·  velocity {1}",
                    LanguageService.T(_engine.Preset.Name),
                    velocity);
            }

            return;
        }

        if (!_engine.Preset.IsDrum)
        {
            var keyPitchClass = ((_settings.Transpose % 12) + 12) % 12;
            var chord = ChordDetector.Detect(notes, keyPitchClass);
            if (chord is not null)
            {
                ShowChord(chord, keyPitchClass);
                return;
            }
        }

        ShowNotes(notes);
    }

    private void ShowChord(ChordMatch chord, int keyPitchClass)
    {
        SetNowPlayingText(chord.Name);

        var keyName = LanguageService.F("{0} major", NoteNames.GetPitchClass(keyPitchClass));
        var noteNames = string.Join(" ", chord.Notes.Select(NoteNames.GetPitchClass));
        NowPlayingSubText.Text = LanguageService.F(
            "{0} · degree {1} · {2}",
            keyName,
            chord.Degree,
            noteNames);
    }

    private void ShowNotes(IReadOnlyList<int> notes)
    {
        var names = notes.Select(NoteNames.Get).ToArray();
        SetNowPlayingText(string.Join(" ", names));
        NowPlayingSubText.Text = LanguageService.T("No matching chord");
    }

    private void SetNowPlayingText(string text)
    {
        NowPlayingText.Text = text;
        NowPlayingText.FontSize = text.Length <= 6 ? 54 : text.Length <= 10 ? 42 : 30;
        NowPlayingText.Foreground = (Brush)FindResource("AccentBrush");
    }

    private void SetNowPlayingIdle()
    {
        NowPlayingText.Text = "—";
        NowPlayingText.FontSize = 54;
        NowPlayingText.Foreground = (Brush)FindResource("TextMutedBrush");
    }

    private void SetNowPlayingNote(int note)
    {
        SetNowPlayingText(NoteNames.Get(note));
    }

    // ============================== Settings ==============================

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingKeys)
        {
            ExitKeyEditMode();
        }

        var dialog = new SettingsWindow(_settings.Clone())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var oldName = _settings.VirtualDeviceName;
        var oldPitchKey = _settings.PitchBendKeyScanCode;
        var wasListening = _listening;

        ReleasePedals();
        _settings = dialog.Result;
        _settings.Normalize();
        LanguageService.SetLanguage(_settings.Language);
        _engine.Settings = _settings;
        ResetOctaveShift();
        _capture.PitchBendKeyScanCode = _settings.PitchBendKeyScanCode;

        Piano.ShowLabels = _settings.ShowKeyLabels;
        ThemeService.Apply(_settings.Theme, _settings.AccentColor);
        ThemeService.ApplyWindowChrome(this);
        ApplyBackgroundImage();
        AutoStartService.SetEnabled(_settings.AutoStart);

        if (wasListening && oldPitchKey != _settings.PitchBendKeyScanCode)
        {
            StopListening("Pitch bend key changed.");
        }

        UpdatePresetUi();
        UpdateOctaveUi();
        UpdateTransposeUi();
        UpdateVelocityUi();
        UpdateMidiUi();
        UpdateListeningUi();
        UpdatePedalUi();

        if (!string.Equals(oldName, _settings.VirtualDeviceName, StringComparison.Ordinal))
        {
            var renamed = await _midi.RenameAsync(_settings.VirtualDeviceName);
            if (!renamed)
            {
                NowPlayingSubText.Text = LanguageService.T("Could not rename the virtual MIDI port.");
            }
        }

        SettingsStore.Save(_settings);
    }

    private void ApplyBackgroundImage()
    {
        var path = _settings.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            HeroImage.Source = null;
            HeroImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            HeroImage.Source = image;
            HeroImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Could not load background image.", ex);
            HeroImage.Source = null;
            HeroImage.Visibility = Visibility.Collapsed;
        }
    }

    // ============================== Theme ==============================

    private void ThemeTimer_Tick(object? sender, EventArgs e)
    {
        if (_settings.Theme != AppTheme.System)
        {
            return;
        }

        var dark = ThemeService.SystemPrefersDark();
        if (dark == ThemeService.IsDark)
        {
            return;
        }

        ThemeService.Apply(_settings.Theme, _settings.AccentColor);
        ThemeService.ApplyWindowChrome(this);
        Piano.InvalidateVisual();
    }

    // ============================== Activation ==============================

    internal void ShowFromBackground()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        InvalidateVisual();
        UpdateLayout();
        Focus();
    }

    internal void RequestExit()
    {
        Close();
    }

    // ============================== Window lifecycle ==============================

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        _themeTimer.Stop();
        LanguageService.LanguageChanged -= LanguageService_LanguageChanged;
        if (_editingKeys)
        {
            ExitKeyEditMode();
        }

        StopListening("Closing Midi Broad.");
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SettingsStore.Save(_settings);

        try
        {
            await Task.Run(() => _midi.Dispose());
        }
        catch (Exception ex)
        {
            Log.Error("Could not dispose virtual MIDI output.", ex);
        }

        _rawInput.Dispose();
        _capture.Dispose();

        _allowClose = true;
        Close();
    }
}
