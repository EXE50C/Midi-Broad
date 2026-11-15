using System.Collections.Concurrent;
using MidiBroad.Core;
using MidiBroad.Models;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;
using Windows.Devices.Midi2.Transports.Loopback;
using Windows.Devices.Midi2.Transports.Virtual;

namespace MidiBroad.Midi;

/// <summary>
/// Owns the service-side MIDI port. The preferred transport is a transient
/// loopback endpoint pair: it is independent of this process lifetime, so
/// closing the app no longer tears down a virtual device (which can wedge
/// midisrv). A virtual device is kept as a fallback for systems without the
/// loopback transport.
/// </summary>
public sealed class WmsMidiOutput : IMidiOutput
{
    private const string LoopbackUniqueA = "midibroad-main-a";
    private const string LoopbackUniqueB = "midibroad-main-b";

    private readonly BlockingCollection<Action> _queue = new(8192);
    private Thread? _worker;
    private MidiSession? _session;
    private MidiEndpointConnection? _connection;
    private MidiVirtualDevice? _virtualDevice;
    private MidiLoopbackEntry? _loopbackEntry;
    private bool _usingLoopback;
    private string _endpointName = "Midi Broad";
    private volatile MidiOutputState _state = MidiOutputState.NotStarted;
    private volatile string _statusText = "Not started";
    private volatile bool _startAbandoned;
    private bool _disposed;

    public MidiOutputState State => _state;
    public string StatusText => _statusText;
    public string EndpointName => _endpointName;

    public event EventHandler? StateChanged;

    public async Task<bool> StartAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        _endpointName = string.IsNullOrWhiteSpace(endpointName) ? "Midi Broad" : endpointName.Trim();
        _startAbandoned = false;
        EnsureWorker();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(() =>
            {
                try
                {
                    tcs.TrySetResult(StartCore(_endpointName));
                }
                catch (Exception ex)
                {
                    Log.Error("Virtual MIDI startup failed.", ex);
                    SetState(MidiOutputState.Error, "Virtual MIDI error");
                    tcs.TrySetResult(false);
                }
            }))
        {
            return false;
        }

        var completed = await Task.WhenAny(
            tcs.Task,
            Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);

        if (completed != tcs.Task)
        {
            _startAbandoned = true;
            SetState(MidiOutputState.Error,
                "Windows MIDI service is not responding. Restart the MIDI service or reboot.");
            return false;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public Task<bool> RenameAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(endpointName))
        {
            return Task.FromResult(false);
        }

        var name = endpointName.Trim();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(() =>
            {
                try
                {
                    var ok = false;
                    if (_usingLoopback && _loopbackEntry is not null)
                    {
                        ok = UpdateLoopbackNames(_loopbackEntry.AssociationId, name);
                    }
                    else if (_virtualDevice is not null)
                    {
                        ok = _virtualDevice.UpdateEndpointName(name);
                    }

                    if (ok)
                    {
                        _endpointName = name;
                        SetStatus($"Ready · {name}");
                    }

                    tcs.TrySetResult(ok);
                }
                catch (Exception ex)
                {
                    Log.Error("MIDI endpoint rename failed.", ex);
                    tcs.TrySetResult(false);
                }
            }))
        {
            return Task.FromResult(false);
        }

        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public void Send(MidiMessage message)
    {
        if (_disposed || _state != MidiOutputState.Ready)
        {
            return;
        }

        if (!_queue.TryAdd(() => SendCore(message)))
        {
            Log.Warn("Virtual MIDI queue full; message dropped.");
        }
    }

    private bool StartCore(string endpointName)
    {
        SetState(MidiOutputState.Starting, "Starting MIDI service…");
        Log.Info("WMS: EnsureServiceAvailable begin.");
        if (!MidiApi.EnsureServiceAvailable())
        {
            Log.Warn("WMS: EnsureServiceAvailable returned false.");
            SetState(MidiOutputState.Unavailable, "Windows MIDI service unavailable");
            return false;
        }

        Log.Info("WMS: EnsureServiceAvailable done.");

        if (MidiLoopbackManager.IsTransportAvailable)
        {
            Log.Info("WMS: using loopback transport.");
            return StartLoopbackCore(endpointName);
        }

        Log.Warn("WMS: loopback transport unavailable; using virtual device fallback.");
        return StartVirtualCore(endpointName);
    }

    private bool StartLoopbackCore(string endpointName)
    {
        CloseCore();

        _loopbackEntry = FindLoopbackEntry();
        if (_loopbackEntry is null)
        {
            Log.Info("WMS: creating transient loopback pair.");
            var definitionA = new MidiLoopbackEndpointDefinition(
                endpointName,
                "Midi Broad output — open this port in your DAW",
                LoopbackUniqueA);
            var definitionB = new MidiLoopbackEndpointDefinition(
                InternalName(endpointName),
                "Midi Broad internal send endpoint. Do not open.",
                LoopbackUniqueB);

            var response = MidiLoopbackManager.CreateTransientLoopback(
                new MidiLoopbackCreationConfig(definitionA, definitionB));

            if (!response.Success)
            {
                Log.Warn($"WMS: loopback creation failed: {response.ErrorCode} {response.ErrorMessage}");
                SetState(MidiOutputState.Error, "Could not create MIDI loopback");
                return false;
            }

            _loopbackEntry = response.CreatedLoopbackEntry;
        }
        else
        {
            Log.Info("WMS: reusing existing loopback pair.");
            var expectedInternal = InternalName(endpointName);
            if (!string.Equals(_loopbackEntry.EndpointA.Name, endpointName, StringComparison.Ordinal) ||
                !string.Equals(_loopbackEntry.EndpointB.Name, expectedInternal, StringComparison.Ordinal))
            {
                UpdateLoopbackNames(_loopbackEntry.AssociationId, endpointName);
            }
        }

        if (_loopbackEntry is null)
        {
            SetState(MidiOutputState.Error, "Loopback endpoint missing");
            return false;
        }

        // The public endpoint A is what the DAW opens. Send into the internal
        // endpoint B; messages emerge from A.
        _usingLoopback = true;
        _session = MidiSession.Create(endpointName);
        if (_session is null)
        {
            Log.Warn("WMS: session is null.");
            SetState(MidiOutputState.Error, "Could not create MIDI session");
            return false;
        }

        _connection = _session.CreateEndpointConnection(_loopbackEntry.EndpointB.EndpointDeviceId);
        if (_connection is null)
        {
            Log.Warn("WMS: loopback connection is null.");
            SetState(MidiOutputState.Error, "Could not open MIDI loopback");
            return false;
        }

        if (!_connection.Open())
        {
            Log.Warn("WMS: loopback open failed.");
            SetState(MidiOutputState.Error, "MIDI loopback failed to open");
            return false;
        }

        SetState(MidiOutputState.Ready, $"Ready · {endpointName}");
        Log.Info($"Loopback MIDI endpoint '{endpointName}' is ready.");
        return true;
    }

    private bool StartVirtualCore(string endpointName)
    {
        if (!MidiVirtualDeviceManager.IsTransportAvailable)
        {
            Log.Warn("WMS: virtual transport unavailable.");
            SetState(MidiOutputState.Unavailable, "Virtual MIDI transport unavailable");
            return false;
        }

        CloseCore();

        Log.Info("WMS: creating session.");
        _session = MidiSession.Create(endpointName);
        if (_session is null)
        {
            Log.Warn("WMS: session is null.");
            SetState(MidiOutputState.Error, "Could not create MIDI session");
            return false;
        }

        Log.Info("WMS: creating virtual device.");
        var config = BuildConfig(endpointName);
        _virtualDevice = MidiVirtualDeviceManager.CreateVirtualDevice(config);
        if (_startAbandoned)
        {
            CloseCore();
            return false;
        }

        if (_virtualDevice is null)
        {
            Log.Warn("WMS: virtual device is null.");
            SetState(MidiOutputState.Error, "Could not create virtual device");
            return false;
        }

        _virtualDevice.SuppressHandledMessages = true;

        Log.Info("WMS: creating endpoint connection.");
        _connection = _session.CreateEndpointConnection(_virtualDevice.DeviceEndpointDeviceId);
        if (_connection is null)
        {
            Log.Warn("WMS: endpoint connection is null.");
            SetState(MidiOutputState.Error, "Could not open virtual endpoint");
            return false;
        }

        Log.Info("WMS: adding virtual device plugin.");
        if (_connection.AddMessageProcessingPlugin(_virtualDevice) != MidiMessageProcessingPluginAddResult.Succeeded)
        {
            Log.Warn("WMS: plugin add failed.");
            SetState(MidiOutputState.Error, "Could not attach virtual device");
            return false;
        }

        Log.Info("WMS: opening endpoint.");
        if (!_connection.Open())
        {
            Log.Warn("WMS: endpoint open failed.");
            SetState(MidiOutputState.Error, "Virtual endpoint failed to open");
            return false;
        }

        SetState(MidiOutputState.Ready, $"Ready · {endpointName}");
        Log.Info($"Virtual MIDI endpoint '{endpointName}' is ready.");
        return true;
    }

    private static MidiLoopbackEntry? FindLoopbackEntry()
    {
        try
        {
            foreach (var entry in MidiLoopbackManager.GetActiveLoopbackEntries())
            {
                if (entry.EndpointA.EndpointDeviceId.Contains(LoopbackUniqueA, StringComparison.OrdinalIgnoreCase) ||
                    entry.EndpointB.EndpointDeviceId.Contains(LoopbackUniqueB, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate MIDI loopback endpoints.", ex);
        }

        return null;
    }

    private static string InternalName(string endpointName)
    {
        var name = endpointName.Length > 48 ? endpointName[..48] : endpointName;
        return $"{name} (internal)";
    }

    private static bool UpdateLoopbackNames(Guid associationId, string endpointName)
    {
        try
        {
            var config = new MidiLoopbackUpdateConfig(associationId)
            {
                EndpointAName = endpointName,
                EndpointADescription = "Midi Broad output — open this port in your DAW",
                EndpointBName = InternalName(endpointName),
                EndpointBDescription = "Midi Broad internal send endpoint. Do not open.",
                IsMuted = false
            };

            var response = MidiLoopbackManager.UpdateLoopback(config);
            if (!response.Success)
            {
                Log.Warn($"WMS: loopback rename failed: {response.ErrorCode} {response.ErrorMessage}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not rename MIDI loopback endpoints.", ex);
            return false;
        }
    }

    private void SendCore(MidiMessage message)
    {
        try
        {
            if (_connection is null || !_connection.IsOpen)
            {
                return;
            }

            // MIDI 1.0 channel-voice UMP, group 0. Building the word directly
            // avoids any ambiguity in the preview message builder.
            var word = (2u << 28) |
                       ((uint)message.Status << 16) |
                       ((uint)message.Data1 << 8) |
                       message.Data2;
            var packet = new MidiMessage32(0, word);

            var result = _connection.SendSingleMessagePacket(packet);
            if (!MidiEndpointConnection.SendMessageSucceeded(result))
            {
                Log.Warn($"Virtual MIDI send failed: {result}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Virtual MIDI send failed.", ex);
        }
    }

    private static MidiVirtualDeviceCreationConfig BuildConfig(string endpointName)
    {
        var declared = new MidiDeclaredEndpointInfo
        {
            Name = endpointName,
            ProductInstanceId = "MidiBroad_01",
            SpecificationVersionMajor = 1,
            SpecificationVersionMinor = 1,
            SupportsMidi10Protocol = true,
            SupportsMidi20Protocol = false,
            SupportsReceivingJitterReductionTimestamps = false,
            SupportsSendingJitterReductionTimestamps = false,
            HasStaticFunctionBlocks = true
        };

        var identity = new MidiDeclaredDeviceIdentity();
        var userInfo = new MidiEndpointUserSuppliedInfo
        {
            Name = endpointName,
            Description = "Virtual keyboard created by Midi Broad"
        };

        var config = new MidiVirtualDeviceCreationConfig(
            endpointName,
            "Midi Broad virtual keyboard",
            "Midi Broad",
            declared,
            identity,
            userInfo);

        var block = new MidiFunctionBlock
        {
            Number = 0,
            Name = "Keys",
            IsActive = true,
            UIHint = MidiFunctionBlockUIHint.Sender,
            FirstGroup = new MidiGroup(0),
            GroupCount = 1,
            Direction = MidiFunctionBlockDirection.Bidirectional,
            RepresentsMidi10Connection = MidiFunctionBlockRepresentsMidi10Connection.YesBandwidthRestricted,
            MaxSystemExclusive8Streams = 0,
            MidiCIMessageVersionFormat = 0
        };

        config.FunctionBlocks.Add(block);
        return config;
    }

    private void EnsureWorker()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "MidiBroad.Midi"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    private void WorkerMain()
    {
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Virtual MIDI worker stopped.", ex);
        }
        finally
        {
            // Give the service a moment to consume the last short messages before disconnect.
            Thread.Sleep(500);
            CloseCore();
        }
    }

    private void CloseCore()
    {
        try
        {
            if (_connection is not null && _virtualDevice is not null)
            {
                _connection.RemoveMessageProcessingPlugin(_virtualDevice.PluginId);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove virtual device plugin: {ex.Message}");
        }

        try
        {
            if (_connection is not null && _session is not null)
            {
                _session.DisconnectEndpointConnection(_connection.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disconnect MIDI endpoint: {ex.Message}");
        }

        try
        {
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not dispose MIDI session: {ex.Message}");
        }

        _connection = null;
        _virtualDevice = null;
        _session = null;
        _loopbackEntry = null;
        _usingLoopback = false;
    }

    private void SetState(MidiOutputState state, string status)
    {
        _state = state;
        _statusText = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string status)
    {
        _statusText = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(4));
        _queue.Dispose();
        SetState(MidiOutputState.NotStarted, "Stopped");
    }
}
