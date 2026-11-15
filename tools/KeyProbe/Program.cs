using System.Runtime.InteropServices;
using static Native;

internal static class Program
{
    private static readonly LowLevelKeyboardProc Proc = HookCallback;

    private static void Main()
    {
        var hook = SetWindowsHookEx(13, Proc, GetModuleHandle(null), 0);
        Console.WriteLine($"hook={hook} error={Marshal.GetLastWin32Error()}");
        if (hook == IntPtr.Zero)
        {
            return;
        }

        Console.WriteLine("Press any key. Logging for 120 seconds...");
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(10);
        }

        UnhookWindowsHookEx(hook);
        Console.WriteLine("done.");
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            Console.WriteLine(
                $"vk=0x{info.VirtualKey:X2} scan=0x{info.ScanCode:X2} flags=0x{info.Flags:X2} " +
                $"down={((info.Flags & 0x80) == 0)} injected={((info.Flags & 0x10) != 0)}");
            Console.Out.Flush();
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}

internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint VirtualKey;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PointX;
    public int PointY;
    public uint Private;
}

internal static partial class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);
}
