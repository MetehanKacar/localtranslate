using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace LocalTranslate.UI;

/// <summary>Notification-area icon: left click toggles, right click opens the menu.</summary>
internal sealed class Tray : IDisposable
{
    static readonly string[] FallbackLanguages = { "tr", "en", "de", "fr", "es", "it", "ru", "ar", "fa", "zh-Hans", "ja", "ko", "pt", "nl", "pl", "uk", "az" };

    readonly Controller controller;
    readonly Forms.NotifyIcon icon;
    readonly Icon onIcon;
    readonly Icon offIcon;

    public Tray(Controller controller)
    {
        this.controller = controller;
        onIcon = LoadIcon("app.ico");
        offIcon = LoadIcon("app-off.ico");

        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        menu.Items.Add("…"); // placeholder so Opening fires

        icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) controller.Toggle();
        };
        controller.Changed += Refresh;
        Refresh();
    }

    static Icon LoadIcon(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    void Refresh()
    {
        var s = controller.Settings;
        icon.Icon = s.Enabled ? onIcon : offIcon;
        var state = controller.ScreenMode ? Loc.T("tray.screenOn") : s.Enabled ? Loc.T("tray.on") : Loc.T("tray.off");
        var text = $"LocalTranslate — {state} (→ {Translator.DisplayName(s.Target)})";
        icon.Text = text.Length > 127 ? text[..127] : text;
    }

    void BuildMenu(Forms.ContextMenuStrip menu)
    {
        var s = controller.Settings;
        menu.Items.Clear();

        var toggle = new Forms.ToolStripMenuItem(Loc.T("tray.enabled")) { Checked = s.Enabled, ShortcutKeyDisplayString = s.Hotkey };
        toggle.Click += (_, _) => controller.Toggle();
        menu.Items.Add(toggle);

        var screenMode = new Forms.ToolStripMenuItem(Loc.T("tray.screen")) { Checked = controller.ScreenMode, ShortcutKeyDisplayString = s.ScreenHotkey };
        screenMode.Click += (_, _) => controller.ToggleScreenMode();
        menu.Items.Add(screenMode);

        var regionSelect = new Forms.ToolStripMenuItem(Loc.T("tray.region")) { ShortcutKeyDisplayString = s.RegionHotkey };
        regionSelect.Click += (_, _) => App.Background(controller.StartRegionSelectAsync(delayMs: 300));
        menu.Items.Add(regionSelect);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var target = new Forms.ToolStripMenuItem(Loc.T("tray.target", Translator.DisplayName(s.Target)));
        var languages = controller.Translator.Languages.Count > 0
            ? controller.Translator.NamedLanguages()
            : FallbackLanguages.Select(c => (Code: c, Name: Translator.DisplayName(c))).ToList();
        foreach (var (code, name) in languages)
        {
            var item = new Forms.ToolStripMenuItem($"{name} ({code})") { Checked = code == s.Target };
            item.Click += (_, _) => controller.Update(x => x.Target = code);
            target.DropDownItems.Add(item);
        }
        menu.Items.Add(target);

        var hover = new Forms.ToolStripMenuItem(Loc.T("tray.hover")) { Checked = s.Hover };
        hover.Click += (_, _) => controller.Update(x => x.Hover = !x.Hover);
        menu.Items.Add(hover);

        var selection = new Forms.ToolStripMenuItem(Loc.T("tray.selection")) { Checked = s.Selection };
        selection.Click += (_, _) => controller.Update(x => x.Selection = !x.Selection);
        menu.Items.Add(selection);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.settings"), null, (_, _) => controller.ShowSettings());
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => System.Windows.Application.Current.Shutdown());
    }

    public void Notify(string title, string text) =>
        icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Warning);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        onIcon.Dispose();
        offIcon.Dispose();
    }
}
