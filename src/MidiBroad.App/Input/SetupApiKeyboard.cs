using System.Runtime.InteropServices;
using System.Text;

namespace MidiBroad.Input;

internal sealed record SetupKeyboardDevice(
    string InterfacePath,
    string InstanceId,
    string FriendlyName,
    string Manufacturer,
    bool IsPs2)
{
    public string NormalizedPath => NormalizePath(InterfacePath);

    public static string NormalizePath(string path)
    {
        var value = path;
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        var guidIndex = value.IndexOf("#{", StringComparison.Ordinal);
        if (guidIndex >= 0)
        {
            value = value[..guidIndex];
        }

        return value.TrimEnd('#').ToUpperInvariant();
    }
}

internal static class SetupApiKeyboard
{
    private static readonly Guid KeyboardClassGuid = new("4D36E96B-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid KeyboardInterfaceGuid = new("884B96C3-56EF-11D1-BC8C-00A0C91405DD");

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;

    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpManufacturer = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;

    public static IReadOnlyList<SetupKeyboardDevice> Enumerate()
    {
        var results = new List<SetupKeyboardDevice>();
        var classGuid = KeyboardClassGuid;
        var interfaceGuid = KeyboardInterfaceGuid;
        var set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return results;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    Size = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                };

                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                {
                    break;
                }

                uint required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                if (required == 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize is 8 on 64-bit because the DevicePath member is aligned.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    var info = new SP_DEVINFO_DATA
                    {
                        Size = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
                    };

                    if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, required, ref required, ref info))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
                    var instance = GetInstanceId(set, ref info);
                    var friendly = GetProperty(set, ref info, SpdrpFriendlyName);
                    var description = GetProperty(set, ref info, SpdrpDeviceDesc);
                    var manufacturer = GetProperty(set, ref info, SpdrpManufacturer);
                    var isPs2 = IsPs2Device(instance, path, manufacturer);

                    results.Add(new SetupKeyboardDevice(
                        path,
                        instance,
                        !string.IsNullOrWhiteSpace(friendly) ? friendly : description,
                        manufacturer,
                        isPs2));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return results;
    }

    private static bool IsPs2Device(string instance, string path, string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false;
        }

        if (instance.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase) ||
            instance.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ||
            instance.Contains("PNP0303", StringComparison.OrdinalIgnoreCase) ||
            instance.Contains("PNP030B", StringComparison.OrdinalIgnoreCase) ||
            instance.Contains("RDP_KBD", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PNP0303", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PNP030B", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return manufacturer.Contains("Standard 101/102", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var builder = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref info, builder, (uint)builder.Capacity, out _)
            ? builder.ToString()
            : string.Empty;
    }

    private static string GetProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        uint type;
        uint size;
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, null, 0, out size) || size == 0)
        {
            return string.Empty;
        }

        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, buffer, size, out size))
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public IntPtr Reserved;

        public SP_DEVINFO_DATA()
        {
            Size = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
            ClassGuid = Guid.Empty;
            DeviceInstance = 0;
            Reserved = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        ref uint requiredSize,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        ref uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
