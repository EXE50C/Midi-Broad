using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MidiBroad.Models;

namespace MidiBroad.Core;

public static class ThemeService
{
    public static bool IsDark { get; private set; } = true;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    public static bool ResolveIsDark(AppTheme mode) => mode switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemPrefersDark()
    };

    public static void Apply(AppTheme mode, string accentHex)
    {
        var isDark = ResolveIsDark(mode);
        IsDark = isDark;

        var source = new Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var dictionary = new ResourceDictionary { Source = source };

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(dictionary);
        }
        else
        {
            app.Resources.MergedDictionaries[0] = dictionary;
        }

        var accent = ParseColor(accentHex, isDark ? "#8B5CF6" : "#7C3AED");
        var hover = isDark ? Lighten(accent, 0.12) : Darken(accent, 0.10);
        var soft = Blend(accent, isDark ? Color.FromRgb(18, 21, 30) : Colors.White, isDark ? 0.22 : 0.14);
        var textOnAccent = Luminance(accent) > 0.62 ? Colors.Black : Colors.White;

        app.Resources["AccentColor"] = accent;
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(hover);
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(soft);
        app.Resources["AccentTextBrush"] = new SolidColorBrush(textOnAccent);

        foreach (Window window in app.Windows)
        {
            ApplyWindowChrome(window, isDark);
        }
    }

    public static void ApplyWindowChrome(Window window, bool? dark = null)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var useDark = dark ?? IsDark;
            var value = useDark ? 1 : 0;
            // 20 is the documented attribute on Windows 11; 19 is used by older builds.
            if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
            }

            var corner = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));

            // Explicit caption/border colors keep the native title bar in step with
            // the custom theme, including on builds where the immersive-dark flag
            // is ignored for WPF windows.
            var caption = useDark ? 0x0018110E : 0x00FEFCFC;   // COLORREF: BBGGRR
            var captionText = useDark ? 0x00FCF7F5 : 0x001E1614;
            var border = useDark ? 0x00382B25 : 0x00EEE5E3;
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, 36, ref captionText, sizeof(int));
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            SetWindowTheme(hwnd, useDark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    public static Color ParseColor(string? hex, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
        }
        catch
        {
            // fall through
        }

        return (Color)ColorConverter.ConvertFromString(fallback);
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    private static Color Darken(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.G * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.B * (1 - amount), 0, 255));

    private static Color Blend(Color a, Color b, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(a.R * t + b.R * (1 - t)),
            (byte)(a.G * t + b.G * (1 - t)),
            (byte)(a.B * t + b.B * (1 - t)));
    }

    private static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
