using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MidiBroad.Core;
using MidiBroad.Input;
using MidiBroad.Models;
using Microsoft.Win32;

namespace MidiBroad;

public partial class SettingsWindow : Window
{
    private static readonly string[] AccentChoices =
    {
        "#8B5CF6", "#22D3EE", "#F472B6", "#34D399", "#FBBF24", "#FB7185"
    };

    private readonly List<Border> _accentBorders = new();
    private readonly string _originalLanguage;
    private string _accentHex = "#8B5CF6";
    private string? _backgroundPath;
    private bool _updatingUi = true;
    private bool _saved;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        _originalLanguage = LanguageService.Current;

        ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
        LanguageCombo.ItemsSource = LanguageService.Available;
        VelocityModeCombo.ItemsSource = new[] { VelocityMode.Random, VelocityMode.Fixed };
        PitchBendKeyCombo.ItemsSource = KeyCatalog.PitchBendKeys;

        var ranges = KeyCatalog.PitchBendRanges
            .Select(v => new RangeOption(v, $"±{v} semitone{(v == 1 ? string.Empty : "s")}"))
            .ToArray();
        PitchBendRangeCombo.ItemsSource = ranges;
        PitchBendDirectionCombo.ItemsSource = Enum.GetValues<PitchBendDirection>();

        LoadSettingsIntoUi(settings);
        Loaded += (_, _) => LanguageService.Apply(this);
    }

    public AppSettings Result { get; }

    private void LoadSettingsIntoUi(AppSettings settings)
    {
        _updatingUi = true;
        _accentHex = settings.AccentColor;
        _backgroundPath = settings.BackgroundImagePath;

        ThemeCombo.SelectedItem = settings.Theme;
        LanguageCombo.SelectedItem = LanguageService.Available
            .FirstOrDefault(l => string.Equals(l.Code, settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? LanguageService.Available[0];
        VelocityModeCombo.SelectedItem = settings.VelocityMode;
        PitchBendKeyCombo.SelectedItem = KeyCatalog.PitchBendKeys
            .FirstOrDefault(k => k.ScanCode == settings.PitchBendKeyScanCode)
            ?? KeyCatalog.PitchBendKeys[0];
        PitchBendRangeCombo.SelectedItem = PitchBendRangeCombo.Items
            .OfType<RangeOption>()
            .FirstOrDefault(r => r.Value == settings.PitchBendRange)
            ?? PitchBendRangeCombo.Items.OfType<RangeOption>().ElementAtOrDefault(1);
        PitchBendDirectionCombo.SelectedItem = settings.PitchBendDirection;

        VirtualNameBox.Text = settings.VirtualDeviceName;
        AutoStartCheck.IsChecked = settings.AutoStart;
        AutoReconnectCheck.IsChecked = settings.AutoReconnect;
        ShowLabelsCheck.IsChecked = settings.ShowKeyLabels;

        FixedVelocitySlider.Value = Math.Clamp(settings.FixedVelocity, 1, 127);
        MinVelocitySlider.Value = Math.Clamp(settings.MinVelocity, 1, 127);
        MaxVelocitySlider.Value = Math.Clamp(settings.MaxVelocity, 1, 127);

        BackgroundPathText.Text = string.IsNullOrWhiteSpace(_backgroundPath)
            ? "No custom image"
            : _backgroundPath;
        ConfigPathText.Text = SettingsStore.ActiveConfigPath;
        ConfigPathText.ToolTip = SettingsStore.ActiveConfigPath;

        BuildAccentSwatches();
        UpdateVelocityUi();
        ValidationText.Text = string.Empty;
        _updatingUi = false;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ThemeService.ApplyWindowChrome(this);
    }

    private void BuildAccentSwatches()
    {
        AccentPanel.Children.Clear();
        _accentBorders.Clear();

        foreach (var hex in AccentChoices)
        {
            var color = ThemeService.ParseColor(hex, "#8B5CF6");
            var border = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 7, 0),
                Cursor = Cursors.Hand,
                Tag = hex,
                BorderBrush = (Brush)FindResource("TextPrimaryBrush"),
                BorderThickness = string.Equals(hex, _accentHex, StringComparison.OrdinalIgnoreCase)
                    ? new Thickness(2)
                    : new Thickness(0)
            };

            border.MouseLeftButtonDown += (_, _) => SelectAccent(hex);
            _accentBorders.Add(border);
            AccentPanel.Children.Add(border);
        }
    }

    private void SelectAccent(string hex)
    {
        _accentHex = hex;
        foreach (var border in _accentBorders)
        {
            border.BorderThickness = string.Equals((string?)border.Tag, hex, StringComparison.OrdinalIgnoreCase)
                ? new Thickness(2)
                : new Thickness(0);
        }
    }

    private void BrowseBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            _backgroundPath = dialog.FileName;
            BackgroundPathText.Text = dialog.FileName;
        }
    }

    private void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        _backgroundPath = null;
        BackgroundPathText.Text = "No custom image";
    }

    private void VelocityModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingUi)
        {
            UpdateVelocityUi();
        }
    }

    private void FixedVelocitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingUi)
        {
            FixedVelocityText.Text = ((int)Math.Round(FixedVelocitySlider.Value)).ToString();
        }
    }

    private void MinVelocitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi)
        {
            return;
        }

        if (MinVelocitySlider.Value > MaxVelocitySlider.Value)
        {
            _updatingUi = true;
            MaxVelocitySlider.Value = MinVelocitySlider.Value;
            _updatingUi = false;
        }

        UpdateVelocityText();
    }

    private void MaxVelocitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi)
        {
            return;
        }

        if (MaxVelocitySlider.Value < MinVelocitySlider.Value)
        {
            _updatingUi = true;
            MinVelocitySlider.Value = MaxVelocitySlider.Value;
            _updatingUi = false;
        }

        UpdateVelocityText();
    }

    private void UpdateVelocityUi()
    {
        _updatingUi = true;
        var random = VelocityModeCombo.SelectedItem is VelocityMode mode && mode == VelocityMode.Random;
        FixedVelocitySlider.IsEnabled = !random;
        MinVelocitySlider.IsEnabled = random;
        MaxVelocitySlider.IsEnabled = random;
        FixedVelocityText.Text = ((int)Math.Round(FixedVelocitySlider.Value)).ToString();
        UpdateVelocityText();
        _updatingUi = false;
    }

    private void UpdateVelocityText()
    {
        MinVelocityText.Text = ((int)Math.Round(MinVelocitySlider.Value)).ToString();
        MaxVelocityText.Text = ((int)Math.Round(MaxVelocitySlider.Value)).ToString();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || LanguageCombo.SelectedItem is not LanguageOption option)
        {
            return;
        }

        LanguageService.SetLanguage(option.Code);
        LanguageService.Apply(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_saved)
        {
            LanguageService.SetLanguage(_originalLanguage);
        }

        base.OnClosing(e);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyUi(Result))
        {
            return;
        }

        _saved = true;
        DialogResult = true;
    }

    private bool TryApplyUi(AppSettings target)
    {
        var name = VirtualNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Text = LanguageService.T("Virtual device name cannot be empty.");
            VirtualNameBox.Focus();
            return false;
        }

        ValidationText.Text = string.Empty;
        target.VirtualDeviceName = name;
        target.Theme = ThemeCombo.SelectedItem is AppTheme theme ? theme : AppTheme.System;
        target.AccentColor = _accentHex;
        target.Language = LanguageCombo.SelectedItem is LanguageOption language
            ? language.Code
            : LanguageService.English;
        target.BackgroundImagePath = string.IsNullOrWhiteSpace(_backgroundPath) ? null : _backgroundPath;
        target.AutoStart = AutoStartCheck.IsChecked == true;
        target.AutoReconnect = AutoReconnectCheck.IsChecked == true;
        target.ShowKeyLabels = ShowLabelsCheck.IsChecked == true;

        target.VelocityMode = VelocityModeCombo.SelectedItem is VelocityMode mode ? mode : VelocityMode.Random;
        target.FixedVelocity = (int)Math.Round(FixedVelocitySlider.Value);
        target.MinVelocity = (int)Math.Round(MinVelocitySlider.Value);
        target.MaxVelocity = (int)Math.Round(MaxVelocitySlider.Value);
        if (target.MinVelocity > target.MaxVelocity)
        {
            (target.MinVelocity, target.MaxVelocity) = (target.MaxVelocity, target.MinVelocity);
        }

        target.PitchBendKeyScanCode = PitchBendKeyCombo.SelectedItem is KeyOption key
            ? key.ScanCode
            : 0x39;
        target.PitchBendRange = PitchBendRangeCombo.SelectedItem is RangeOption range
            ? range.Value
            : 2;
        target.PitchBendDirection = PitchBendDirectionCombo.SelectedItem is PitchBendDirection direction
            ? direction
            : PitchBendDirection.Up;

        target.Normalize();
        return true;
    }

    private void ExportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyUi(Result))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Midi Broad config",
            Filter = "Midi Broad config|*.json|All files|*.*",
            FileName = "MidiBroad-config.json",
            DefaultExt = ".json",
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(SettingsStore.DefaultExportPath) ?? string.Empty,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (SettingsStore.Export(Result, dialog.FileName, out var error))
        {
            ConfigStatusText.Text = LanguageService.F(
                "Exported mappings, device name and appearance to {0}",
                dialog.FileName);
        }
        else
        {
            ConfigStatusText.Text = LanguageService.F("Export failed: {0}", error);
        }
    }

    private void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Midi Broad config",
            Filter = "Midi Broad config|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!SettingsStore.TryImport(dialog.FileName, out var imported, out var error) || imported is null)
        {
            ConfigStatusText.Text = LanguageService.F("Import failed: {0}", error);
            return;
        }

        Result.CopyFrom(imported);
        LoadSettingsIntoUi(Result);
        LanguageService.SetLanguage(Result.Language);
        LanguageService.Apply(this);
        ConfigStatusText.Text = LanguageService.F(
            "Imported {0}. Press Save to apply.",
            Path.GetFileName(dialog.FileName));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed record RangeOption(int Value, string Label);
}
