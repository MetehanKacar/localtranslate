using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LocalTranslate.Input;

namespace LocalTranslate.UI;

public partial class SettingsWindow : Window
{
    sealed record Item(string Code, string Label);

    readonly Controller controller;

    internal SettingsWindow(Controller controller)
    {
        InitializeComponent();
        this.controller = controller;
        var s = controller.Settings;

        ApplyTexts();
        FillUiLanguages(s.Language);
        FillLanguages(s.Target);
        HoverBox.IsChecked = s.Hover;
        SelectionBox.IsChecked = s.Selection;
        DelaySlider.Value = s.HoverDelay;
        DelayText.Text = $"{s.HoverDelay} ms";
        DelaySlider.ValueChanged += (_, e) => DelayText.Text = $"{(int)e.NewValue} ms";
        OcrBox.IsChecked = s.OcrFallback;
        ClipboardBox.IsChecked = s.ClipboardFallback;
        HotkeyBox.Text = s.Hotkey;
        ScreenHotkeyBox.Text = s.ScreenHotkey;
        RegionHotkeyBox.Text = s.RegionHotkey;
        ServerBox.Text = s.Server;
        ApiKeyBox.Text = s.ApiKey;
        StartupBox.IsChecked = Settings.StartWithWindows;

        Loaded += async (_, _) =>
        {
            StatusText.Text = Loc.T("set.checking");
            StatusText.Foreground = Brushes.Gray;
            if (await controller.Translator.PingAsync(controller.Settings))
            {
                FillLanguages((string?)TargetBox.SelectedValue ?? s.Target);
                StatusText.Text = Loc.T("set.connected", controller.Translator.Languages.Count);
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
            }
            else
            {
                StatusText.Text = Loc.T("set.unreachable");
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            }
        };
    }

    void ApplyTexts()
    {
        Title = Loc.T("set.title");
        LanguageHeading.Text = Loc.T("set.language");
        TargetHeading.Text = Loc.T("set.target");
        SourceHint.Text = Loc.T("set.sourceAuto");
        MethodsHeading.Text = Loc.T("set.methods");
        HoverBox.Content = Loc.T("set.hover");
        DelayLabel.Text = Loc.T("set.delay");
        SelectionBox.Content = Loc.T("set.selection");
        FallbackHeading.Text = Loc.T("set.fallbacks");
        OcrBox.Content = Loc.T("set.ocr");
        ClipboardBox.Content = Loc.T("set.clipboard");
        ClipboardHint.Text = Loc.T("set.clipboardHint");
        ShortcutsHeading.Text = Loc.T("set.shortcuts");
        ToggleLabel.Text = Loc.T("set.scToggle");
        ScreenLabel.Text = Loc.T("set.scScreen");
        RegionLabel.Text = Loc.T("set.scRegion");
        ShortcutHint.Text = Loc.T("set.scHint");
        ServerHeading.Text = Loc.T("set.server");
        ApiKeyLabel.Text = Loc.T("set.apiKey");
        StartupBox.Content = Loc.T("set.startup");
        CancelButton.Content = Loc.T("set.cancel");
        SaveButton.Content = Loc.T("set.save");
    }

    void FillUiLanguages(string selected)
    {
        UiLanguageBox.ItemsSource = new List<Item>
        {
            new(Loc.Auto, Loc.T("set.langAuto")),
            new(Loc.Turkish, "Türkçe"),
            new(Loc.English, "English")
        };
        UiLanguageBox.SelectedValue = selected is Loc.Turkish or Loc.English ? selected : Loc.Auto;
    }

    void FillLanguages(string selected)
    {
        var items = controller.Translator.NamedLanguages().Select(l => new Item(l.Code, $"{l.Name} ({l.Code})")).ToList();
        if (items.All(i => i.Code != selected))
            items.Insert(0, new Item(selected, $"{Translator.DisplayName(selected)} ({selected})"));
        TargetBox.ItemsSource = items;
        TargetBox.SelectedValue = selected;
    }

    void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var text = Hotkey.Format(Keyboard.Modifiers, key);
        if (text != null && Hotkey.TryParse(text, out _, out _)) ((System.Windows.Controls.TextBox)sender).Text = text;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var updated = controller.Settings.Clone();
        updated.Language = (string?)UiLanguageBox.SelectedValue ?? Loc.Auto;
        updated.Target = (string?)TargetBox.SelectedValue ?? updated.Target;
        updated.Hover = HoverBox.IsChecked == true;
        updated.Selection = SelectionBox.IsChecked == true;
        updated.HoverDelay = (int)DelaySlider.Value;
        updated.OcrFallback = OcrBox.IsChecked == true;
        updated.ClipboardFallback = ClipboardBox.IsChecked == true;
        updated.Hotkey = HotkeyBox.Text;
        updated.ScreenHotkey = ScreenHotkeyBox.Text;
        updated.RegionHotkey = RegionHotkeyBox.Text;
        updated.Server = string.IsNullOrWhiteSpace(ServerBox.Text) ? "http://localhost:5000" : ServerBox.Text.Trim().TrimEnd('/');
        updated.ApiKey = ApiKeyBox.Text.Trim();

        controller.ReplaceSettings(updated);
        try { Settings.StartWithWindows = StartupBox.IsChecked == true; } catch { /* registry blocked by policy */ }
        Close();
    }
}
