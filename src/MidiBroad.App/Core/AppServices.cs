using System.IO;
using System.Reflection;
using System.Text.Json;
using MidiBroad.Models;

namespace MidiBroad.Core;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MidiBroad");

    public static string LocalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidiBroad");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogFile => Path.Combine(LocalDirectory, "midi-broad.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LocalDirectory);
    }
}

public static class SettingsStore
{
    private const string ConfigFileName = "config.json";
    private const string LegacyFileName = "settings.json";

    private static readonly string PortableConfigFile = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    private static readonly string UserConfigFile = Path.Combine(AppPaths.DataDirectory, ConfigFileName);
    private static readonly string PortableLegacyFile = Path.Combine(AppContext.BaseDirectory, LegacyFileName);

    /// <summary>
    /// The file that an ordinary Save writes to. The portable file is preferred so a
    /// copied build folder carries its own configuration. If the folder is read-only
    /// (for example Program Files), the per-user file is used instead.
    /// </summary>
    public static string ActiveConfigPath { get; private set; } = PortableConfigFile;

    public static string DefaultExportPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MidiBroad-config.json");

    public static AppSettings Load()
    {
        var candidates = new[]
        {
            PortableConfigFile,
            UserConfigFile,
            PortableLegacyFile,
            AppPaths.SettingsFile
        };

        // config.json is authoritative. settings.json is only a legacy fallback,
        // so an old AppData file can never override a newer portable config.
        var ordered = candidates
            .Select((path, index) => new
            {
                Path = path,
                Index = index,
                IsConfig = string.Equals(Path.GetFileName(path), ConfigFileName, StringComparison.OrdinalIgnoreCase),
                WriteTime = TryGetWriteTime(path)
            })
            .Where(c => c.WriteTime is not null)
            .OrderBy(c => c.IsConfig ? 0 : 1)
            .ThenByDescending(c => c.WriteTime)
            .ThenBy(c => c.Index);

        foreach (var candidate in ordered)
        {
            try
            {
                var json = File.ReadAllText(candidate.Path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options);
                if (settings is null)
                {
                    continue;
                }

                settings.Normalize();
                ActiveConfigPath = candidate.Path;

                // Migrate an older settings.json (or a user config) to the portable
                // config so future launches read one unambiguous file.
                if (!string.Equals(candidate.Path, PortableConfigFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryWrite(PortableConfigFile, JsonSerializer.Serialize(settings, SettingsJson.Options)))
                    {
                        ActiveConfigPath = PortableConfigFile;
                    }
                }

                return settings;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read settings from '{candidate.Path}'; trying the next location.", ex);
            }
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        ActiveConfigPath = PortableConfigFile;
        return defaults;
    }

    public static bool Save(AppSettings settings)
    {
        settings.Normalize();
        var json = JsonSerializer.Serialize(settings, SettingsJson.Options);

        if (TryWrite(PortableConfigFile, json))
        {
            ActiveConfigPath = PortableConfigFile;
            return true;
        }

        if (TryWrite(UserConfigFile, json))
        {
            ActiveConfigPath = UserConfigFile;
            return true;
        }

        Log.Error("Could not save settings to either the portable or per-user config file.");
        return false;
    }

    public static bool Export(AppSettings settings, string path, out string? error)
    {
        try
        {
            settings.Normalize();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(settings, SettingsJson.Options));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"Could not export config to '{path}'.", ex);
            return false;
        }
    }

    public static bool TryImport(string path, out AppSettings? settings, out string? error)
    {
        settings = null;
        try
        {
            var json = File.ReadAllText(path);
            var imported = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options);
            if (imported is null)
            {
                error = "The file does not contain a Midi Broad config.";
                return false;
            }

            imported.Normalize();
            settings = imported;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"Could not import config from '{path}'.", ex);
            return false;
        }
    }

    private static DateTime? TryGetWriteTime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryWrite(string file, string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = file + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, file, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save settings to '{file}'.", ex);
            return false;
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            AppPaths.EnsureCreated();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex is not null)
            {
                line += Environment.NewLine + ex;
            }

            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", true);
                }

                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Some portable/redirected environments cannot write to LocalAppData.
            // Fall back to a file next to the executable so diagnostics still work.
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex is not null)
                {
                    line += Environment.NewLine + ex;
                }

                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "midi-broad.log"), line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MidiBroad";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(), Microsoft.Win32.RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not update auto-start.", ex);
            return false;
        }
    }

    private static string BuildCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) ||
            string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            exe = Assembly.GetEntryAssembly()?.Location ?? exe ?? "MidiBroad.exe";
        }

        return $"\"{exe}\" --minimized";
    }
}
