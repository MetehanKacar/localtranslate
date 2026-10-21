using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalTranslate.Text;
using Forms = System.Windows.Forms;
using Rect = System.Windows.Rect;

namespace LocalTranslate.Screen;

/// <summary>
/// "Translate the whole screen" mode. Every few hundred ms each monitor is
/// screenshotted and compared tile by tile with the previous shot:
///  - translations over tiles that changed are removed at once (so nothing is
///    left floating over scrolled/replaced text);
///  - once the screen has been still for a moment, it is OCR'd again and every
///    block not already in the target language is painted over with its
///    translation (cached ones instantly, new ones as LibreTranslate answers).
/// Tiles that change all the time (video, animations) are ignored so they don't
/// keep the screen "busy" forever.
/// </summary>
internal sealed class ScreenTranslator
{
    const int Tile = 24;               // px
    const int Step = 3;                // sample every 3rd pixel inside a tile
    const float ChangeThreshold = 2.5f; // mean brightness difference (0-255)
    const int AnimatedAfter = 4;        // changed this many checks in a row = animation
    const int MinChangedTiles = 3;      // smaller changes (blinking caret, clock) don't trigger a re-read
    const int StableMs = 250;
    const int IntervalMs = 200;
    const double OcrScale = 1.5;

    readonly Translator translator;
    readonly Func<Settings> settings;
    readonly OcrReader ocr = new();
    readonly Dictionary<string, MonitorState> monitors = new();
    readonly string? debugDir = Environment.GetEnvironmentVariable("LOCALTRANSLATE_DEBUG_DUMP");
    CancellationTokenSource? cts;
    bool errorReported;

    public event Action<string>? Error;
    public bool Running => cts != null;

    sealed class MonitorState(Native.RECT rect, ScreenOverlayWindow overlay)
    {
        public Native.RECT Rect = rect;
        public readonly ScreenOverlayWindow Overlay = overlay;
        public float[]? Signature;
        public int[] Streak = Array.Empty<int>();
        public bool Dirty = true;
        public long ChangedAt;
        public int Generation;
    }

    public ScreenTranslator(Translator translator, Func<Settings> settings)
    {
        this.translator = translator;
        this.settings = settings;
    }

    public void Start()
    {
        if (Running) return;
        if (!ocr.Available)
        {
            Error?.Invoke(Loc.T("error.ocr"));
            return;
        }
        errorReported = false;
        cts = new CancellationTokenSource();
        _ = LoopAsync(cts.Token);
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        foreach (var m in monitors.Values)
        {
            m.Generation++;
            m.Overlay.Clear();
            m.Overlay.Hide();
        }
        monitors.Clear();
    }

    /// <summary>Target language changed: repaint everything.</summary>
    public void Refresh()
    {
        foreach (var m in monitors.Values)
        {
            m.Generation++;
            m.Overlay.Clear();
            m.Dirty = true;
        }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var screen in Forms.Screen.AllScreens)
                {
                    if (ct.IsCancellationRequested) return;
                    await CheckAsync(screen, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.Log(ex);
            }
            try { await Task.Delay(IntervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task CheckAsync(Forms.Screen screen, CancellationToken ct)
    {
        var b = screen.Bounds;
        var rect = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        if (!monitors.TryGetValue(screen.DeviceName, out var state) || state.Rect.Width != rect.Width || state.Rect.Height != rect.Height)
        {
            state?.Overlay.Close();
            state = new MonitorState(rect, new ScreenOverlayWindow(rect));
            monitors[screen.DeviceName] = state;
            state.Overlay.ShowOverMonitor();
        }

        var (shot, signature) = await Task.Run(() =>
        {
            var bmp = OcrReader.Capture(rect);
            return (bmp, Signature(bmp));
        }, ct);

        try
        {
            if (ct.IsCancellationRequested) return;
            long now = Environment.TickCount64;
            int columns = (rect.Width + Tile - 1) / Tile;

            if (state.Signature == null || state.Signature.Length != signature.Length)
            {
                state.Signature = signature;
                state.Streak = new int[signature.Length];
                state.ChangedAt = now;
                state.Dirty = true;
                return;
            }

            var changed = new List<Rect>();
            int significant = 0;
            bool settled = false;
            for (int i = 0; i < signature.Length; i++)
            {
                if (Math.Abs(signature[i] - state.Signature[i]) > ChangeThreshold)
                {
                    if (++state.Streak[i] < AnimatedAfter) significant++;
                    changed.Add(new Rect(rect.Left + i % columns * Tile, rect.Top + i / columns * Tile, Tile, Tile));
                }
                else
                {
                    if (state.Streak[i] >= AnimatedAfter) settled = true; // a long scroll just stopped
                    state.Streak[i] = 0;
                }
            }
            state.Signature = signature;

            if (changed.Count > 0) state.Overlay.RemoveIn(changed);
            if (significant >= MinChangedTiles || settled)
            {
                state.Dirty = true;
                state.ChangedAt = now;
                return;
            }
            if (!state.Dirty || now - state.ChangedAt < StableMs) return;

            state.Dirty = false;
            await RenderAsync(state, shot);
        }
        finally
        {
            shot.Dispose();
        }
    }

    async Task RenderAsync(MonitorState state, Bitmap shot)
    {
        int generation = ++state.Generation;
        var origin = new Native.POINT(state.Rect.Left, state.Rect.Top);
        var blocks = await Task.Run(async () =>
        {
            var lines = await ocr.ReadAsync(shot, origin, OcrScale);
            return BlockBuilder.Build(lines, new PixelSampler(shot, origin));
        });
        if (generation != state.Generation) return;

        var s = settings();
        state.Overlay.Clear();
        var pending = new List<ScreenBlock>();
        foreach (var block in blocks)
        {
            if (!TryCompose(block, s.Target, out var text)) pending.Add(block);
            else if (text != null) state.Overlay.Add(block, text);
        }

        // Translate what's near the mouse first: that's probably what is being read.
        Native.GetCursorPos(out var cursor);
        var order = pending.OrderBy(x => Distance(x.Box, cursor)).ToList();
        var jobs = order.Select(block => TranslateBlockAsync(state, generation, block, s)).ToList();

        if (debugDir != null)
        {
            await Task.WhenAll(jobs);
            Directory.CreateDirectory(debugDir);
            var name = Path.Combine(debugDir, $"screen-{DateTime.Now:HHmmss-fff}");
            state.Overlay.SaveComposite(shot, name + ".png");
            File.WriteAllLines(name + ".txt", blocks.Select(b =>
                $"[{(int)b.Box.X},{(int)b.Box.Y} {(int)b.Box.Width}x{(int)b.Box.Height}] {b.Text}\n" +
                string.Join("\n", BlockBuilder.Sentences(b.Text).Select(x =>
                    translator.TryGetCached(x, s.Target, out var r) ? $"    «{x}» → {r.Detected}: {r.Text}" : $"    «{x}» → (yok)"))));
        }
    }

    async Task TranslateBlockAsync(MonitorState state, int generation, ScreenBlock block, Settings s)
    {
        try
        {
            await Task.WhenAll(BlockBuilder.Sentences(block.Text).Select(x => translator.TranslateAsync(x, s)));
        }
        catch (TranslationException ex)
        {
            if (!errorReported) { errorReported = true; Error?.Invoke(ex.Message); }
            return;
        }
        if (generation == state.Generation && TryCompose(block, s.Target, out var text) && text != null)
            state.Overlay.Add(block, text);
    }

    /// <summary>
    /// Builds the block's text from its cached sentence translations; false if some
    /// aren't translated yet. Each sentence is judged on its own detected language,
    /// so a block mixing languages keeps the parts already in the target language.
    /// <paramref name="text"/> is null when nothing in the block needs translating.
    /// </summary>
    bool TryCompose(ScreenBlock block, string target, out string? text)
    {
        text = null;
        var parts = new List<string>();
        bool translatedAny = false;
        foreach (var sentence in BlockBuilder.Sentences(block.Text))
        {
            if (!translator.TryGetCached(sentence, target, out var r)) return false;
            bool needed = !Translator.SameLanguage(r.Detected, target) &&
                          !string.Equals(r.Text.Trim(), sentence.Trim(), StringComparison.OrdinalIgnoreCase);
            parts.Add(needed ? r.Text : sentence);
            translatedAny |= needed;
        }
        if (translatedAny) text = string.Join(" ", parts);
        return true;
    }

    static double Distance(Rect r, Native.POINT p)
    {
        double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
        double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>Mean brightness of every tile, sampled sparsely: cheap enough to run several times a second.</summary>
    static unsafe float[] Signature(Bitmap bmp)
    {
        int columns = (bmp.Width + Tile - 1) / Tile, rows = (bmp.Height + Tile - 1) / Tile;
        var sums = new float[columns * rows];
        var counts = new int[columns * rows];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < bmp.Height; y += Step)
            {
                byte* row = scan + y * data.Stride;
                int rowTile = y / Tile * columns;
                for (int x = 0; x < bmp.Width; x += Step)
                {
                    byte* px = row + x * 4;
                    int t = rowTile + x / Tile;
                    sums[t] += 0.114f * px[0] + 0.587f * px[1] + 0.299f * px[2];
                    counts[t]++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        for (int i = 0; i < sums.Length; i++) sums[i] /= Math.Max(1, counts[i]);
        return sums;
    }
}
