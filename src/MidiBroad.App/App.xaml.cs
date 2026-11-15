using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using MidiBroad.Core;

namespace MidiBroad;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateThread;
    private volatile bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var testInstance = e.Args.Any(a => string.Equals(a, "--test-instance", StringComparison.OrdinalIgnoreCase));
        var createdNew = true;
        if (!testInstance)
        {
            _singleInstance = new Mutex(true, "MidiBroad.SingleInstance", out createdNew);
        }

        if (!testInstance && !createdNew)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting("MidiBroad.Activate");
                activate.Set();
            }
            catch
            {
                var existing = FindWindow(null, "Midi Broad");
                if (existing != IntPtr.Zero)
                {
                    ShowWindow(existing, 9);
                    SetForegroundWindow(existing);
                }
                else
                {
                    MessageBox.Show(
                        "Midi Broad is already running.",
                        "Midi Broad",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }

            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MidiBroad.Activate");
        _activateThread = new Thread(ActivationThreadMain)
        {
            IsBackground = true,
            Name = "MidiBroad.Activation"
        };
        _activateThread.Start();

        SessionEnding += (_, _) =>
        {
            if (MainWindow is global::MidiBroad.MainWindow window)
            {
                window.RequestExit();
            }
        };

        // Keep the app's own windows out of IME composition. Some input methods
        // otherwise consume keystrokes before the low-level hook sees them.
        ImmDisableIME(0);

        AppPaths.EnsureCreated();
        Log.Info("Midi Broad starting.");

        var settings = SettingsStore.Load();
        LanguageService.Initialize(settings.Language);
        // If the user removed the Run entry outside the app, reflect reality.
        if (settings.AutoStart && !AutoStartService.IsEnabled())
        {
            AutoStartService.SetEnabled(true);
        }

        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            var settingsWindow = new SettingsWindow(settings.Clone());
            MainWindow = settingsWindow;
            settingsWindow.Show();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void ActivationThreadMain()
    {
        try
        {
            while (!_shuttingDown && _activateEvent is not null && _activateEvent.WaitOne())
            {
                if (_shuttingDown)
                {
                    break;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is global::MidiBroad.MainWindow window)
                    {
                        window.ShowFromBackground();
                    }
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Activation listener stopped.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        try
        {
            _activateEvent?.Set();
            _activateThread?.Join(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best effort.
        }

        _activateEvent?.Dispose();
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch
        {
            // The mutex may not have been acquired.
        }

        _singleInstance?.Dispose();
        Log.Info("Midi Broad stopped.");
        base.OnExit(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmDisableIME(uint threadId);
}
