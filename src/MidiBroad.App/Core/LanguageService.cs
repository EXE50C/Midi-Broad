using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MidiBroad.Core;

namespace MidiBroad.Core;

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Small JSON-backed language pack service. Packs live in the lang folder next
/// to the executable. Text is translated by walking the visual tree, so existing
/// XAML does not need a resource key for every literal.
/// </summary>
public static class LanguageService
{
    public const string English = "en";
    public const string Chinese = "zh-CN";

    private static readonly Dictionary<string, string> Forward = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Reverse = new(StringComparer.Ordinal);

    public static string Current { get; private set; } = English;

    public static IReadOnlyList<LanguageOption> Available { get; } = new[]
    {
        new LanguageOption(English, "English"),
        new LanguageOption(Chinese, "中文（简体）")
    };

    public static event EventHandler? LanguageChanged;

    public static void Initialize(string? code)
    {
        Current = Normalize(code);
        LoadPack(Current);
    }

    public static void SetLanguage(string? code)
    {
        var normalized = Normalize(code);
        if (string.Equals(Current, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RestoreEnglishAll();
        Current = normalized;
        LoadPack(Current);
        ApplyAll();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Normalize(string? code)
    {
        if (string.Equals(code, Chinese, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return Chinese;
        }

        return English;
    }

    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return Forward.TryGetValue(text, out var translated) ? translated : text;
    }

    public static string F(string format, params object?[] args) =>
        string.Format(T(format), args);

    public static void Apply(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }

        TranslateElement(root, Forward);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            Apply(VisualTreeHelper.GetChild(root, i));
        }
    }

    public static void ApplyAll()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            Apply(window);
        }
    }

    private static void RestoreEnglishAll()
    {
        if (Reverse.Count == 0 || Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            RestoreEnglish(window);
        }
    }

    private static void RestoreEnglish(DependencyObject root)
    {
        TranslateElement(root, Reverse);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            RestoreEnglish(VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void TranslateElement(DependencyObject element, IReadOnlyDictionary<string, string> map)
    {
        switch (element)
        {
            case Window window when !string.IsNullOrEmpty(window.Title):
                window.Title = Translate(window.Title, map);
                break;
            case TextBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                textBlock.Text = Translate(textBlock.Text, map);
                break;
            case Button button when button.Content is string content && !string.IsNullOrEmpty(content):
                button.Content = Translate(content, map);
                break;
            case CheckBox checkBox when checkBox.Content is string checkContent && !string.IsNullOrEmpty(checkContent):
                checkBox.Content = Translate(checkContent, map);
                break;
        }

        if (element is FrameworkElement { ToolTip: string toolTip } && !string.IsNullOrEmpty(toolTip))
        {
            ((FrameworkElement)element).ToolTip = Translate(toolTip, map);
        }
    }

    private static string Translate(string value, IReadOnlyDictionary<string, string> map) =>
        map.TryGetValue(value, out var translated) ? translated : value;

    private static void LoadPack(string code)
    {
        Forward.Clear();
        Reverse.Clear();

        if (string.Equals(code, English, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "lang", code + ".json");
        if (!File.Exists(path))
        {
            var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "lang", code + ".json"));
            if (File.Exists(parent))
            {
                path = parent;
            }
        }

        try
        {
            if (!File.Exists(path))
            {
                Log.Warn($"Language pack not found: {path}");
                return;
            }

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (entries is null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                Forward[entry.Key] = entry.Value;
                Reverse.TryAdd(entry.Value, entry.Key);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load language pack '{path}'.", ex);
        }
    }
}
