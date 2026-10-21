using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LocalTranslate.Input;
using LocalTranslate.Screen;
using LocalTranslate.Text;
using LocalTranslate.UI;

namespace LocalTranslate;

/// <summary>
/// Hover: poll the cursor; once it rests on new text for HoverDelay ms, find the
/// sentence under it (UI Automation, or OCR for images), highlight it and show
/// the translation below. Moving off the sentence hides it.
/// Selection: on mouse-up after a drag / double-click / shift-click, read the
/// selection and translate it into a bubble that stays until the next click.
/// Region select (Ctrl+Alt+F11): select text inside images via OCR.
/// Screen mode (Ctrl+Alt+F10): translate everything on screen in place.
/// </summary>
internal sealed class Controller : IDisposable
{
    const double StayOnSentenceSlack = 6;
    const int MaxSelection = 5000;
    const int ToggleHotkey = 1, RegionHotkey = 2, ScreenHotkey = 3;

    public Settings Settings { get; private set; }
    public Translator Translator { get; } = new();
    public event Action? Changed;
    public event Action<string, string>? Problem;

    readonly TextLocator locator = new();
    readonly OverlayWindow hoverBubble = new(interactive: false);
    readonly OverlayWindow selectionBubble = new(interactive: true);
    readonly OverlayWindow toast = new(interactive: false);
    readonly HighlightWindow highlight = new();
    readonly HighlightWindow selectionMark = new();
    readonly MouseHook mouse = new();
    readonly Hotkey hotkeys = new();
    readonly ScreenTranslator screen;
    readonly DispatcherTimer poll;
    SettingsWindow? settingsWindow;
    RegionSelectWindow? region;
    long regionClosedAt;

    // Hover state
    Native.POINT lastPos;
    long lastMove = Environment.TickCount64;
    Native.POINT? queriedAt;
    bool locating;
    int lookupSeq;
    TextHit? hoverHit;

    // Selection state
    bool leftDown;
    Native.POINT downPos;
    long lastUpTime;
    Native.POINT lastUpPos;
    int selectionSeq;
    TextHit? selectionHit;

    public bool ScreenMode => screen.Running;

    public Controller(Settings settings)
    {
        Settings = settings;
        screen = new ScreenTranslator(Translator, () => Settings);
        screen.Error += message => Problem?.Invoke(Loc.T("problem.screen"), message);

        mouse.LeftDown += OnLeftDown;
        mouse.LeftUp += OnLeftUp;
        mouse.OtherDown += OnOtherDown;
        mouse.Wheel += OnWheel;

        hotkeys.Pressed += id =>
        {
            if (id == ToggleHotkey) Toggle();
            else if (id == RegionHotkey) App.Background(StartRegionSelectAsync());
            else if (id == ScreenHotkey) ToggleScreenMode();
        };
        RegisterHotkeys();

        poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        poll.Tick += (_, _) => Tick();
        poll.Start();
    }

    // The click that closes the region window must not count as a text selection.
    bool RegionBusy => region != null || Environment.TickCount64 - regionClosedAt < 400;

    // ---------------- Hover ----------------

    void Tick()
    {
        if (!Settings.Enabled || RegionBusy || screen.Running || !Native.GetCursorPos(out var p)) return;
        long now = Environment.TickCount64;

        if (p != lastPos)
        {
            lastPos = p;
            lastMove = now;
            if (hoverHit != null && !hoverHit.Contains(p, StayOnSentenceSlack)) HideHover();
        }

        if (!Settings.Hover || leftDown || selectionHit != null || locating) return;
        if (now - lastMove < Settings.HoverDelay) return;
        if (queriedAt == p) return;                                              // already looked here
        if (hoverHit != null && hoverHit.Contains(p, StayOnSentenceSlack)) return; // same sentence

        queriedAt = p;
        locating = true;
        App.Background(LookupHoverAsync(p, ++lookupSeq));
    }

    async Task LookupHoverAsync(Native.POINT p, int seq)
    {
        TextHit? hit;
        try { hit = await locator.SentenceAtAsync(p, Settings.OcrFallback); }
        catch { hit = null; }
        finally { locating = false; }

        if (seq != lookupSeq || !Settings.Enabled || !Settings.Hover || selectionHit != null || screen.Running) return;
        if (lastPos.DistanceTo(p) > StayOnSentenceSlack) return; // the mouse moved on; Tick will look again
        if (hit == null) { HideHover(); return; }

        HideHover();
        hoverHit = hit;
        highlight.ShowRects(hit.Rects);
        await TranslateIntoAsync(hoverBubble, hit, () => hoverHit == hit, isHover: true);
    }

    void HideHover()
    {
        hoverHit = null;
        hoverBubble.Hide();
        highlight.Hide();
    }

    // ---------------- Selection ----------------

    void OnLeftDown(Native.POINT p)
    {
        if (RegionBusy) return;
        leftDown = true;
        downPos = p;
        if (selectionBubble.ContainsScreenPoint(p)) return; // clicking "Kopyala"
        HideSelection();
        HideHover();
    }

    void OnOtherDown(Native.POINT p)
    {
        if (RegionBusy || selectionBubble.ContainsScreenPoint(p)) return;
        HideSelection();
        HideHover();
    }

    void OnWheel()
    {
        if (RegionBusy) return;
        // The text moves under the bubbles; they'd point at the wrong place.
        HideSelection();
        HideHover();
        queriedAt = null;
    }

    void OnLeftUp(Native.POINT p)
    {
        leftDown = false;
        if (RegionBusy) return;
        long now = Environment.TickCount64;
        bool doubleClick = now - lastUpTime <= Native.GetDoubleClickTime() && lastUpPos.DistanceTo(p) < 6;
        lastUpTime = now;
        lastUpPos = p;
        queriedAt = null;

        if (!Settings.Enabled || !Settings.Selection || selectionBubble.ContainsScreenPoint(p)) return;
        bool drag = downPos.DistanceTo(p) > 5;
        bool shiftClick = Native.IsKeyDown(Native.VK_SHIFT);
        if (!(drag || doubleClick || shiftClick)) return;

        App.Background(LookupSelectionAsync(p, ++selectionSeq));
    }

    async Task LookupSelectionAsync(Native.POINT p, int seq)
    {
        await Task.Delay(90); // let the app finish updating its selection
        if (seq != selectionSeq || leftDown) return;

        TextHit? hit = null;
        try { hit = await locator.SelectionAsync(p); } catch { }
        if (seq != selectionSeq || leftDown) return;

        // A selection somewhere else in the focused app is an old one, not what was just selected.
        if (hit != null && !hit.IsNear(p, 60)) hit = null;

        if (hit == null && Settings.ClipboardFallback)
        {
            var copied = await ClipboardGrabber.CopySelectionAsync();
            if (seq != selectionSeq || leftDown) return;
            if (copied != null) hit = new TextHit(copied, new[] { new Rect(p.X, p.Y - 10, 1, 20) }, "clipboard");
        }
        if (hit == null) return;

        var text = Sentence.Clean(hit.Text);
        if (text.Length is 0 or > MaxSelection || !Sentence.HasLetters(text)) return;
        if (text != hit.Text) hit = new TextHit(text, hit.Rects, hit.Source);
        ShowSelection(hit, mark: false);
    }

    void ShowSelection(TextHit hit, bool mark)
    {
        HideHover();
        selectionSeq++;
        selectionHit = hit;
        if (mark) selectionMark.ShowRects(hit.Rects); // OCR'd text has no selection of its own to show
        App.Background(TranslateIntoAsync(selectionBubble, hit, () => selectionHit == hit, isHover: false));
    }

    void HideSelection()
    {
        selectionSeq++;
        selectionHit = null;
        selectionBubble.Hide();
        selectionMark.Hide();
    }

    // ---------------- Region select (text in images) ----------------

    public async Task StartRegionSelectAsync(int delayMs = 0)
    {
        if (region != null) { region.Activate(); return; }
        HideHover();
        HideSelection();
        if (delayMs > 0) await Task.Delay(delayMs); // let a closing tray menu disappear first

        Native.GetCursorPos(out var p);
        var (monitor, _, _) = Native.MonitorAt(p);
        var shot = OcrReader.Capture(monitor);

        region = new RegionSelectWindow(shot, monitor); // copies the pixels it shows
        var origin = new Native.POINT(monitor.Left, monitor.Top);
        region.SetOcr(Task.Run(async () =>
        {
            try { return await locator.Ocr.ReadAsync(shot, origin); }
            finally { shot.Dispose(); }
        }));
        region.Selected += hit => ShowSelection(hit, mark: true);
        region.Closed += (_, _) =>
        {
            region = null;
            regionClosedAt = Environment.TickCount64;
        };
        region.Show();
    }

    // ---------------- Screen mode ----------------

    public void ToggleScreenMode()
    {
        if (screen.Running)
        {
            screen.Stop();
            toast.ShowToast(Loc.T("toast.screenOff"));
        }
        else
        {
            if (!Settings.Enabled) Update(s => s.Enabled = true);
            HideHover();
            HideSelection();
            screen.Start();
            if (screen.Running) toast.ShowToast(Loc.T("toast.screenOn", Translator.DisplayName(Settings.Target)));
        }
        Changed?.Invoke();
    }

    // ---------------- Translation ----------------

    async Task TranslateIntoAsync(OverlayWindow bubble, TextHit hit, Func<bool> stillCurrent, bool isHover)
    {
        var settings = Settings;
        var target = settings.Target;
        string Title(string? from) => $"{(from ?? "…").ToUpperInvariant()} → {target.ToUpperInvariant()}";

        if (!Translator.TryGetCached(hit.Text, target, out var result))
        {
            bubble.ShowBelow(hit.Bounds, Title(null), Loc.T("bubble.loading"), BubbleState.Loading);
            try
            {
                result = await Translator.TranslateAsync(hit.Text, settings);
            }
            catch (Exception ex)
            {
                if (stillCurrent())
                    bubble.ShowBelow(hit.Bounds, "LibreTranslate", ex is TranslationException ? ex.Message : Loc.T("bubble.failed", ex.Message), BubbleState.Error);
                return;
            }
            if (!stillCurrent()) return;
        }

        if (Translator.SameLanguage(result.Detected, target))
        {
            if (isHover)
            {
                // Nothing to translate. Keep hoverHit so we don't ask again while the
                // mouse stays on this sentence.
                bubble.Hide();
                highlight.Hide();
            }
            else
            {
                bubble.ShowBelow(hit.Bounds, Title(result.Detected), Loc.T("bubble.sameLanguage"), BubbleState.Note);
            }
            return;
        }

        bubble.ShowBelow(hit.Bounds, Title(result.Detected), result.Text, BubbleState.Done);
    }

    // ---------------- Settings / commands ----------------

    public void Toggle()
    {
        Update(s => s.Enabled = !s.Enabled);
        toast.ShowToast(Loc.T(Settings.Enabled ? "toast.on" : "toast.off"));
    }

    public void Update(Action<Settings> change)
    {
        var before = Settings.Target;
        change(Settings);
        Settings.Save();
        Apply(targetChanged: before != Settings.Target);
    }

    public void ReplaceSettings(Settings updated)
    {
        var old = Settings;
        Settings = updated;
        Settings.Save();
        if (old.Server != updated.Server || old.ApiKey != updated.ApiKey)
        {
            Translator.ClearCache();
            _ = Translator.PingAsync(updated);
        }
        if (old.Language != updated.Language) Loc.Apply(updated.Language);
        if (old.Hotkey != updated.Hotkey || old.RegionHotkey != updated.RegionHotkey || old.ScreenHotkey != updated.ScreenHotkey)
            RegisterHotkeys();
        Apply(targetChanged: old.Target != updated.Target);
    }

    void Apply(bool targetChanged)
    {
        if (!Settings.Enabled || !Settings.Hover || targetChanged) HideHover();
        if (!Settings.Enabled || !Settings.Selection || targetChanged) HideSelection();
        if (!Settings.Enabled && screen.Running) screen.Stop();
        if (targetChanged) screen.Refresh();
        queriedAt = null;
        Changed?.Invoke();
    }

    void RegisterHotkeys()
    {
        foreach (var (id, keys) in new[] { (ToggleHotkey, Settings.Hotkey), (RegionHotkey, Settings.RegionHotkey), (ScreenHotkey, Settings.ScreenHotkey) })
        {
            if (!hotkeys.Register(id, keys))
                Problem?.Invoke(Loc.T("problem.hotkey"), Loc.T("problem.hotkeyText", keys));
        }
    }

    public void ShowSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    /// <summary>Makes sure LibreTranslate answers; starts the Docker container if it doesn't.</summary>
    public async Task EnsureServerAsync()
    {
        if (await Translator.PingAsync(Settings)) return;

        if (Uri.TryCreate(Settings.Server, UriKind.Absolute, out var uri) && uri.IsLoopback)
        {
            try
            {
                using var docker = Process.Start(new ProcessStartInfo("docker", "start libretranslate")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (docker != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try { await docker.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
                }
            }
            catch (System.ComponentModel.Win32Exception) { /* docker not installed */ }

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(3000);
                if (await Translator.PingAsync(Settings)) return;
            }
        }

        Problem?.Invoke(Loc.T("problem.server"), Loc.T("problem.serverText", Settings.Server));
    }

    public void Dispose()
    {
        poll.Stop();
        screen.Stop();
        mouse.Dispose();
        hotkeys.Dispose();
        locator.Dispose();
    }
}
