using MidiBroad.Models;

namespace MidiBroad.Midi;

public enum MidiOutputState
{
    NotStarted,
    Starting,
    Ready,
    Unavailable,
    Error
}

public interface IMidiOutput : IDisposable
{
    MidiOutputState State { get; }
    string StatusText { get; }
    string EndpointName { get; }
    event EventHandler? StateChanged;

    Task<bool> StartAsync(string endpointName, CancellationToken cancellationToken = default);
    void Send(MidiMessage message);
    Task<bool> RenameAsync(string endpointName, CancellationToken cancellationToken = default);
}
