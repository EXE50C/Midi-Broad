using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using MidiBroad.Core;
using MidiBroad.Models;

namespace MidiBroad.Input;

public sealed class RawInputService : IDisposable
{
    private readonly Dictionary<IntPtr, string> _handleToName = new();
    private readonly DispatcherTimer _refreshTimer;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _disposed;

    public RawInputService()
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshDevices();
        };
    }

    public event EventHandler<IReadOnlyList<KeyboardDeviceInfo>>? DevicesChanged;
    public event EventHandler<KeyTransition>? KeySeen;

    public IReadOnlyList<KeyboardDeviceInfo> Devices { get; private set; } = Array.Empty<KeyboardDeviceInfo>();
    public string? LastRawDeviceName { get; private set; }

    public void Attach(IntPtr hwnd)
    {
        if (_hwnd == hwnd)
        {
            return;
        }

        Detach();
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        // Deliberately do NOT call RegisterRawInputDevices for keyboards here.
        // Registering a keyboard for Raw Input disables WH_KEYBOARD_LL delivery
        // in some Windows/WPF combinations. We only need enumeration and
        // WM_DEVICECHANGE, neither of which requires a Raw Input registration.
        Log.Info("Raw Input keyboard registration skipped; using enumeration + WM_DEVICECHANGE.");

        RefreshDevices();
    }

    public void RefreshDevices()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var setupDevices = SetupApiKeyboard.Enumerate()
                .Where(d => !d.IsPs2)
                .ToDictionary(d => d.NormalizedPath, d => d);

            var rawDevices = EnumerateRawKeyboards();
            var results = new List<KeyboardDeviceInfo>();
            var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in rawDevices)
            {
                var normalized = SetupKeyboardDevice.NormalizePath(raw.Name);
                setupDevices.TryGetValue(normalized, out var setup);

                if (setup?.IsPs2 == true || IsSoftOrPs2(raw.Name))
                {
                    continue;
                }

                var (vid, pid) = ParseVidPid(raw.Name);
                var name = setup?.FriendlyName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "HID Keyboard";
                }

                usedNames.TryGetValue(name, out var nameCount);
                usedNames[name] = nameCount + 1;
                if (nameCount > 0)
                {
                    name = $"{name} ({vid:X4}:{pid:X4})";
                }

                var instanceId = setup?.InstanceId ?? string.Empty;
                var deviceId = string.IsNullOrWhiteSpace(instanceId) ? raw.Name : instanceId;
                var bus = BusType(raw.Name);

                results.Add(new KeyboardDeviceInfo
                {
                    DeviceId = deviceId,
                    DisplayName = name,
                    RawDeviceName = raw.Name,
                    InstanceId = instanceId,
                    Manufacturer = setup?.Manufacturer ?? string.Empty,
                    VendorId = vid,
                    ProductId = pid,
                    BusType = bus,
                    IsPhysical = true,
                    IsPs2 = false,
                    IsConnected = true
                });

                _handleToName[raw.Handle] = raw.Name;
            }

            var ordered = results
                .OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.DeviceId, StringComparer.Ordinal)
                .ToArray();

            var changed = !DeviceListsEqual(Devices, ordered);
            Devices = ordered;
            if (changed)
            {
                DevicesChanged?.Invoke(this, ordered);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate keyboard devices.", ex);
        }
    }

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_INPUT:
                ProcessRawInput(lParam);
                break;
            case NativeMethods.WM_INPUT_DEVICE_CHANGE:
                ScheduleRefresh();
                break;
            case 0x0219: // WM_DEVICECHANGE
                ScheduleRefresh();
                break;
        }

        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        NativeMethods.GetRawInputData(rawInputHandle, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(rawInputHandle, NativeMethods.RID_INPUT, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var raw = Marshal.PtrToStructure<NativeMethods.RAWINPUT>(buffer);
            if (raw.Header.Type != NativeMethods.RIM_TYPEKEYBOARD)
            {
                return;
            }

            var keyboard = raw.Keyboard;
            if (keyboard.VKey == 0xFF || keyboard.MakeCode == 0)
            {
                return;
            }

            if (!_handleToName.TryGetValue(raw.Header.Device, out var deviceName))
            {
                deviceName = GetDeviceName(raw.Header.Device) ?? string.Empty;
                if (!string.IsNullOrEmpty(deviceName))
                {
                    _handleToName[raw.Header.Device] = deviceName;
                }
            }

            LastRawDeviceName = deviceName;

            var isDown = (keyboard.Flags & NativeMethods.RI_KEY_BREAK) == 0;
            var isExtended = (keyboard.Flags & NativeMethods.RI_KEY_E0) != 0;
            var transition = new KeyTransition(
                keyboard.MakeCode,
                keyboard.VKey,
                isDown,
                isExtended,
                DateTime.UtcNow,
                deviceName);

            KeySeen?.Invoke(this, transition);
        }
        catch (Exception ex)
        {
            Log.Error("Could not decode raw keyboard input.", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<(IntPtr Handle, string Name)> EnumerateRawKeyboards()
    {
        var results = new List<(IntPtr, string)>();
        uint count = 0;
        var listSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();
        var query = NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, listSize);
        if (query == unchecked((uint)-1) || count == 0)
        {
            return results;
        }

        var buffer = Marshal.AllocHGlobal((int)(count * listSize));
        try
        {
            if (NativeMethods.GetRawInputDeviceList(buffer, ref count, listSize) == unchecked((uint)-1))
            {
                return results;
            }

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.RAWINPUTDEVICELIST>(
                    IntPtr.Add(buffer, (int)(i * listSize)));
                if (item.Type != NativeMethods.RIM_TYPEKEYBOARD)
                {
                    continue;
                }

                var name = GetDeviceName(item.Device);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    results.Add((item.Device, name));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    private static string? GetDeviceName(IntPtr device)
    {
        uint size = 0;
        NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            var result = NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICENAME, buffer, ref size);
            return result == unchecked((uint)-1) ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsSoftOrPs2(string rawName)
    {
        return rawName.Contains("RDP_KBD", StringComparison.OrdinalIgnoreCase) ||
               rawName.Contains("PNP0303", StringComparison.OrdinalIgnoreCase) ||
               rawName.Contains("PNP030B", StringComparison.OrdinalIgnoreCase) ||
               rawName.StartsWith(@"\\?\ACPI", StringComparison.OrdinalIgnoreCase);
    }

    private static (ushort Vid, ushort Pid) ParseVidPid(string name)
    {
        return (ParseHexAfter(name, "VID_"), ParseHexAfter(name, "PID_"));
    }

    private static ushort ParseHexAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + marker.Length + 4 > text.Length)
        {
            return 0;
        }

        return ushort.TryParse(
            text.Substring(index + marker.Length, 4),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : (ushort)0;
    }

    private static string BusType(string rawName)
    {
        if (rawName.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase))
        {
            return "Bluetooth";
        }

        if (rawName.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return "USB";
        }

        return "HID";
    }

    private static bool DeviceListsEqual(
        IReadOnlyList<KeyboardDeviceInfo> left,
        IReadOnlyList<KeyboardDeviceInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].DeviceId != right[i].DeviceId ||
                left[i].DisplayName != right[i].DisplayName)
            {
                return false;
            }
        }

        return true;
    }

    public void Detach()
    {
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _hwnd = IntPtr.Zero;
        _handleToName.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        Detach();
    }
}
