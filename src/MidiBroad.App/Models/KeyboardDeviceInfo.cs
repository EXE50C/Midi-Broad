namespace MidiBroad.Models;

public sealed record KeyboardDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public string RawDeviceName { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string BusType { get; init; } = string.Empty;
    public bool IsPhysical { get; init; } = true;
    public bool IsPs2 { get; init; }
    public bool IsConnected { get; init; } = true;

    public string Detail
    {
        get
        {
            var vidPid = VendorId != 0 || ProductId != 0
                ? $"VID_{VendorId:X4}  PID_{ProductId:X4}"
                : BusType;
            return string.IsNullOrWhiteSpace(Manufacturer)
                ? vidPid
                : $"{Manufacturer}  ·  {vidPid}";
        }
    }

    public override string ToString() => DisplayName;
}

public readonly record struct KeyTransition(
    int ScanCode,
    int VirtualKey,
    bool IsDown,
    bool IsExtended,
    DateTime Timestamp,
    string? DeviceId);
