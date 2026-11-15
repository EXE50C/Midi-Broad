using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;

Console.WriteLine("MIDI probe start.");
if (!MidiApi.EnsureServiceAvailable())
{
    Console.WriteLine("Service unavailable.");
    return;
}

MidiEndpointDeviceInformation? endpoint = null;
for (var attempt = 0; attempt < 80; attempt++)
{
    var all = MidiEndpointDeviceInformation.FindAll();
    Console.WriteLine($"Endpoints: {all.Count}");
    foreach (var info in all)
    {
        Console.WriteLine($"  {info.Name} | {info.EndpointDeviceId}");
    }

    endpoint = all.FirstOrDefault(e =>
        e.Name.Contains("Midi Broad", StringComparison.OrdinalIgnoreCase) &&
        !e.EndpointDeviceId.Contains("loop", StringComparison.OrdinalIgnoreCase));
    if (endpoint is not null)
    {
        break;
    }

    Thread.Sleep(250);
}

if (endpoint is null)
{
    Console.WriteLine("Midi Broad endpoint not found.");
    return;
}

Console.WriteLine($"Opening {endpoint.Name}");
var session = MidiSession.Create("MidiBroadProbe");
if (session is null)
{
    Console.WriteLine("Session is null.");
    return;
}

var connection = session.CreateEndpointConnection(endpoint.EndpointDeviceId);
if (connection is null)
{
    Console.WriteLine("Connection is null.");
    return;
}

connection.MessageReceived += (sender, args) =>
{
    Console.WriteLine($"RX {args.PeekFirstWord():X8}");
};
if (!connection.Open())
{
    Console.WriteLine("Open failed.");
    return;
}

Console.WriteLine("Probe listening for 30 seconds...");
Thread.Sleep(30000);
session.DisconnectEndpointConnection(connection.ConnectionId);
session.Dispose();
Console.WriteLine("Probe done.");
