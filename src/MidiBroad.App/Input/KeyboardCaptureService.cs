using System.Runtime.InteropServices;
using MidiBroad.Core;
using MidiBroad.Models;

namespace MidiBroad.Input;

/// <summary>
/// Global low-level keyboard hook. The hook is installed on a dedicated thread
/// with its own GetMessage loop. Installing WH_KEYBOARD_LL on the WPF dispatcher
/// thread works on some machines but silently stops delivering callbacks on others;
/// a dedicated message loop is the reliable arrangement.
/// </summary>
public sealed class KeyboardCaptureService : IDisposable
{
    public const int HotkeyVirtualKey = 0x7B; // F12
    public const string HotkeyDisplay = "Ctrl + Alt + Shift + F12";

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    // Automated smoke tests can opt into synthetic input. Normal runs ignore it.
    private static readonly bool AllowInjected =
        string.Equals(Environment.GetEnvironmentVariable("MIDIBROAD_ALLOW_INJECTED"), "1",
            StringComparison.Ordinal);

    private static readonly bool Verbose =
        string.Equals(Environment.GetEnvironmentVariable("MIDIBROAD_HOOK_LOG"), "1",
            StringComparison.Ordinal);

    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly HashSet<int> _swallowedScanCodes = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private IntPtr _hiddenWindow;
    private volatile bool _capturing;
    private volatile bool _startedOk;
    private bool _disposed;

    public KeyboardCaptureService()
    {
        _callback = HookCallback;
    }

    public event EventHandler<KeyTransition>? KeyCaptured;
    public event EventHandler? ReleaseRequested;
    public event EventHandler<string>? Error;
    public event EventHandler<int>? OctaveShiftRequested;
    public event EventHandler<int>? OctaveShiftReleased;
    public event EventHandler<int>? PedalToggleRequested;
    public event EventHandler<int>? PitchBendRequested;
    public event EventHandler? PitchBendReleased;
    public event EventHandler<int>? VelocityAdjustRequested;

    public bool IsCapturing => _capturing;
    public string? DeviceName { get; private set; }
    public Func<int, bool>? IsMappedKey { get; set; }
    public int PitchBendKeyScanCode { get; set; } = 0x39;

    public bool Start(string deviceName)
    {
        if (_capturing)
        {
            return true;
        }

        if (_hookThread is { IsAlive: true })
        {
            Stop();
        }

        DeviceName = deviceName;
        _started.Reset();
        _startedOk = false;

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "MidiBroad.KeyboardHook"
        };
        _hookThread.SetApartmentState(ApartmentState.MTA);
        _hookThread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(3)))
        {
            var message = "Keyboard hook thread did not start in time.";
            Error?.Invoke(this, message);
            Log.Error(message);
            return false;
        }

        if (!_startedOk)
        {
            var message = "Could not install the global keyboard hook.";
            Error?.Invoke(this, message);
            Log.Error(message);
            return false;
        }

        Log.Info($"Soft capture started for '{deviceName}' (injected allowed: {AllowInjected}, verbose: {Verbose}).");
        return true;
    }

    public void Stop()
    {
        if (!_capturing && _hookThread is null)
        {
            return;
        }

        _capturing = false;
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (_hookThread is not null)
        {
            _hookThread.Join(TimeSpan.FromSeconds(2));
            if (_hookThread.IsAlive && _hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        _hookThread = null;
        _hookThreadId = 0;
        _swallowedScanCodes.Clear();
        Log.Info("Soft capture stopped.");
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();

            // Force the thread message queue into existence before installing the hook.
            NativeMethods.PeekMessage(
                out _,
                IntPtr.Zero,
                0,
                0,
                NativeMethods.PM_NOREMOVE);

            // A thread with only a message queue can install a hook that appears
            // process-local. A hidden window on the hook thread gives the hook a
            // normal desktop window to live on and keeps it global.
            _hiddenWindow = CreateWindowEx(
                0,
                "STATIC",
                "MidiBroadKeyboardHook",
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            Log.Info($"Hook helper window=0x{_hiddenWindow.ToInt64():X}.");

            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _callback,
                NativeMethods.GetModuleHandle(null),
                0);

            var error = Marshal.GetLastWin32Error();
            Log.Info($"Keyboard hook handle=0x{_hook.ToInt64():X} error={error} thread={_hookThreadId}.");

            if (_hook == IntPtr.Zero)
            {
                _startedOk = false;
                _started.Set();
                return;
            }

            _capturing = true;
            _startedOk = true;
            _started.Set();

            Log.Info("Hook thread message loop begin.");
            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    Log.Error($"GetMessage failed: {Marshal.GetLastWin32Error()}.");
                    break;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }

            Log.Info("Hook thread message loop exit.");
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook thread failed.", ex);
            _startedOk = false;
            _started.Set();
        }
        finally
        {
            _capturing = false;
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            if (_hiddenWindow != IntPtr.Zero)
            {
                DestroyWindow(_hiddenWindow);
                _hiddenWindow = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        try
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var isDown = (info.Flags & NativeMethods.LLKHF_UP) == 0;
            var isInjected = (info.Flags & NativeMethods.LLKHF_INJECTED) != 0;
            var isExtended = (info.Flags & NativeMethods.LLKHF_EXTENDED) != 0;
            var scanCode = (int)info.ScanCode;
            var virtualKey = (int)info.VirtualKey;

            if (Verbose)
            {
                Log.Info($"Hook vk=0x{virtualKey:X2} scan=0x{scanCode:X2} down={isDown} injected={isInjected} capturing={_capturing}.");
            }

            if (isInjected && !AllowInjected)
            {
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (IsHotkey(virtualKey))
            {
                if (isDown)
                {
                    Log.Info("Release hotkey pressed.");
                    ReleaseRequested?.Invoke(this, EventArgs.Empty);
                }

                return (IntPtr)1;
            }

            if (!_capturing)
            {
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (TryHandlePerformanceKey(virtualKey, scanCode, isDown))
            {
                return (IntPtr)1;
            }

            if (isDown)
            {
                var mapped = IsMappedKey?.Invoke(scanCode) == true || scanCode == PitchBendKeyScanCode;
                if (!mapped)
                {
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                _swallowedScanCodes.Add(scanCode);
                KeyCaptured?.Invoke(this, new KeyTransition(
                    scanCode,
                    virtualKey,
                    true,
                    isExtended,
                    DateTime.UtcNow,
                    null));
                return (IntPtr)1;
            }

            if (_swallowedScanCodes.Remove(scanCode))
            {
                KeyCaptured?.Invoke(this, new KeyTransition(
                    scanCode,
                    virtualKey,
                    false,
                    isExtended,
                    DateTime.UtcNow,
                    null));
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook callback failed.", ex);
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    private bool TryHandlePerformanceKey(int virtualKey, int scanCode, bool isDown)
    {
        // Ctrl/Alt combinations belong to the fixed release hotkey and normal
        // desktop shortcuts; don't turn them into performance controls.
        if (CtrlOrAltDown())
        {
            return false;
        }

        var pedalIndex = virtualKey switch
        {
            0x70 => 0, // F1
            0x71 => 1, // F2
            0x72 => 2, // F3
            0x73 => 3, // F4
            _ => scanCode switch
            {
                0x3B => 0,
                0x3C => 1,
                0x3D => 2,
                0x3E => 3,
                _ => -1
            }
        };

        if (pedalIndex >= 0)
        {
            if (isDown)
            {
                if (_swallowedScanCodes.Add(scanCode))
                {
                    PedalToggleRequested?.Invoke(this, pedalIndex);
                }

                return true;
            }

            return _swallowedScanCodes.Remove(scanCode);
        }

        var action = 0;
        var performanceKey = true;

        if (virtualKey == 0xA0 || scanCode == 0x2A)
        {
            action = 1; // left shift -> octave up
        }
        else if (virtualKey == 0xA1 || scanCode == 0x36)
        {
            action = -1; // right shift -> octave down
        }
        else if (virtualKey == 0x25 || scanCode == 0x4B)
        {
            action = -1; // left arrow -> bend down
        }
        else if (virtualKey == 0x27 || scanCode == 0x4D)
        {
            action = 1; // right arrow -> bend up
        }
        else if (virtualKey == 0x26 || scanCode == 0x48)
        {
            action = 1; // up arrow -> velocity up
        }
        else if (virtualKey == 0x28 || scanCode == 0x50)
        {
            action = -1; // down arrow -> velocity down
        }
        else
        {
            performanceKey = false;
        }

        if (!performanceKey)
        {
            return false;
        }

        var isShift = scanCode is 0x2A or 0x36;
        var isArrow = scanCode is 0x4B or 0x4D or 0x48 or 0x50;

        if (isDown)
        {
            if (!_swallowedScanCodes.Add(scanCode))
            {
                return true;
            }

            if (isShift)
            {
                OctaveShiftRequested?.Invoke(this, action);
            }
            else if (scanCode is 0x4B or 0x4D)
            {
                PitchBendRequested?.Invoke(this, action);
            }
            else if (isArrow)
            {
                VelocityAdjustRequested?.Invoke(this, action);
            }

            return true;
        }

        if (_swallowedScanCodes.Remove(scanCode))
        {
            if (scanCode is 0x4B or 0x4D)
            {
                PitchBendReleased?.Invoke(this, EventArgs.Empty);
            }
            else if (isShift)
            {
                OctaveShiftReleased?.Invoke(this, action);
            }

            return true;
        }

        return false;
    }

    private static bool CtrlOrAltDown()
    {
        return (NativeMethods.GetAsyncKeyState(VkControl) & 0x8000) != 0 ||
               (NativeMethods.GetAsyncKeyState(VkMenu) & 0x8000) != 0;
    }

    private static bool IsHotkey(int virtualKey)
    {
        if (virtualKey != HotkeyVirtualKey)
        {
            return false;
        }

        var control = (NativeMethods.GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var alt = (NativeMethods.GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        var shift = (NativeMethods.GetAsyncKeyState(VkShift) & 0x8000) != 0;
        return control && alt && shift;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _started.Dispose();
    }
}
