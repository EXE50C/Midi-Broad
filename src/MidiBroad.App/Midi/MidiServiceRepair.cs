using System.ComponentModel;
using System.Diagnostics;
using MidiBroad.Core;

namespace MidiBroad.Midi;

public enum MidiServiceRepairResult
{
    Success,
    Cancelled,
    Failed,
    TimedOut
}

/// <summary>
/// Last-resort recovery for the Windows MIDI Services preview. A wedged
/// midisrv leaves the virtual endpoint offline until the service is restarted;
/// the restart needs administrator rights, so it is requested through UAC.
/// </summary>
public static class MidiServiceRepair
{
    public static async Task<MidiServiceRepairResult> RestartServiceAsync()
    {
        // Keep the script self-contained: the app can be copied anywhere and
        // does not need a separate .ps1 next to the executable.
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "Restart-Service -Name 'midisrv' -Force; " +
            "Start-Sleep -Milliseconds 800; " +
            "(Get-Service -Name 'midisrv').Status";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Log.Warn("Requesting an elevated restart of the Windows MIDI service.");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Log.Error("Could not start the elevated MIDI service restart.");
                return MidiServiceRepairResult.Failed;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Error("The elevated MIDI service restart timed out.");
                return MidiServiceRepairResult.TimedOut;
            }

            if (process.ExitCode == 0)
            {
                Log.Info("Windows MIDI service restarted.");
                return MidiServiceRepairResult.Success;
            }

            Log.Error($"The elevated MIDI service restart failed with exit code {process.ExitCode}.");
            return MidiServiceRepairResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user declined the UAC prompt.
            Log.Warn("MIDI service restart was cancelled at the UAC prompt.");
            return MidiServiceRepairResult.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Could not restart the Windows MIDI service.", ex);
            return MidiServiceRepairResult.Failed;
        }
    }
}
