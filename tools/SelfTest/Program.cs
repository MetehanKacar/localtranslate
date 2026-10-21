using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LocalTranslate;
using LocalTranslate.Text;

// Usage:
//   SelfTest              unit + text-locator tests against a helper window
//   SelfTest e2e <exe>    also runs the real app, moves the mouse and saves screenshots
//   SelfTest window       (internal) the helper window

static class Program
{
    const string P1 = "LibreTranslate is a free and open source translation API. It runs entirely on your own machine, so your text never leaves the computer. Version 1.9.6 was released recently, see example.com for details!";
    const string P2 = "Der schnelle braune Fuchs springt über den faulen Hund. Das Wetter ist heute sehr schön.";
    const string P3 = "Bu cümle zaten Türkçe olduğu için çevrilmemeli.";
    const string Painted = "The quick brown fox jumps over the lazy dog. This line is drawn as pixels only.";
    const string ImageText = "Bilder können auch Text enthalten.";

    static int failures;

    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0 && args[0] == "window") { RunWindow(); return 0; }
        return RunTests(args).GetAwaiter().GetResult();
    }

    // ---------------- Helper window (separate process: the reader skips its own process) ----------------

    static void RunWindow()
    {
        var form = new Form
        {
            Text = "LocalTranslate SelfTest",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            ClientSize = new Size(1000, 470),
            TopMost = true,
            BackColor = Color.White
        };
        var rtbFont = new Font("Segoe UI", 13f);
        var rtb = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 300,
            Font = rtbFont,
            BorderStyle = BorderStyle.None,
            Text = P1 + "\n" + P2 + "\n" + P3 + "\n"
        };

        // A picture of text inside the text box: UI Automation sees only an image here.
        int imageIndex = rtb.TextLength;
        const int imgW = 520, imgH = 44;
        using (var img = new Bitmap(imgW, imgH))
        {
            using (var g = Graphics.FromImage(img))
            {
                g.Clear(Color.FromArgb(255, 244, 214));
                TextRenderer.DrawText(g, ImageText, new Font("Segoe UI", 16f), new Point(10, 8), Color.FromArgb(120, 40, 0), TextFormatFlags.NoPadding);
            }
            using var png = new MemoryStream();
            img.Save(png, ImageFormat.Png);
            var hex = Convert.ToHexString(png.ToArray());
            rtb.Select(imageIndex, 0);
            rtb.SelectedRtf = $@"{{\rtf1\ansi{{\pict\pngblip\picw{imgW}\pich{imgH}\picwgoal{imgW * 15}\pichgoal{imgH * 15} {hex}}}}}";
        }
        var paintFont = new Font("Segoe UI", 15f);
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        panel.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, Painted, paintFont, new Point(20, 50), Color.Black, TextFormatFlags.NoPadding);
        form.Controls.Add(panel);
        form.Controls.Add(rtb);

        form.Shown += (_, _) =>
        {
            Application.DoEvents();
            Point Char(string needle, int extra = 2)
            {
                int i = rtb.Text.IndexOf(needle, StringComparison.Ordinal) + extra;
                var p = rtb.GetPositionFromCharIndex(i);
                return rtb.PointToScreen(new Point(p.X + 3, p.Y + rtb.Font.Height / 2));
            }
            Point CharEnd(string needle)
            {
                int i = rtb.Text.IndexOf(needle, StringComparison.Ordinal) + needle.Length - 1;
                var p = rtb.GetPositionFromCharIndex(i);
                return rtb.PointToScreen(new Point(p.X + 8, p.Y + rtb.Font.Height / 2));
            }
            void Report(string name, Point p) => Console.WriteLine($"{name} {p.X} {p.Y}");

            Report("RTB_NEVER", Char("never"));
            Report("RTB_WETTER", Char("Wetter"));
            Report("RTB_TR", Char("Türkçe"));
            var selStart = Char("Das Wetter", 0);
            Report("SEL_START", new Point(selStart.X - 3, selStart.Y));
            Report("SEL_END", CharEnd("sehr schön."));
            var imgPos = rtb.GetPositionFromCharIndex(imageIndex);
            Report("RTB_IMG", rtb.PointToScreen(new Point(imgPos.X + 60, imgPos.Y + imgH / 2)));

            var prefix = TextRenderer.MeasureText("The quick brown fox jumps over the ", paintFont, Size.Empty, TextFormatFlags.NoPadding);
            Report("OCR_LAZY", panel.PointToScreen(new Point(20 + prefix.Width + 12, 50 + paintFont.Height / 2)));
            var second = TextRenderer.MeasureText("The quick brown fox jumps over the lazy dog. ", paintFont, Size.Empty, TextFormatFlags.NoPadding);
            var whole = TextRenderer.MeasureText(Painted, paintFont, Size.Empty, TextFormatFlags.NoPadding);
            Report("REGION_START", panel.PointToScreen(new Point(20 + second.Width - 4, 50 - 6)));
            Report("REGION_END", panel.PointToScreen(new Point(20 + whole.Width + 6, 50 + paintFont.Height + 6)));
            var b = form.Bounds;
            Console.WriteLine($"FORM {b.X} {b.Y} {b.Width} {b.Height}");
            Console.WriteLine("READY");
            Console.Out.Flush();
        };
        Application.Run(form);
    }

    // ---------------- Tests ----------------

    static async Task<int> RunTests(string[] args)
    {
        SentenceTests();

        var settings = new Settings();
        var translator = new Translator();
        try
        {
            var r = await translator.TranslateAsync("The weather is very nice today.", settings);
            Check("LibreTranslate en→tr", r.Detected == "en" && r.Text.Length > 0, $"'{r.Text}' (algılanan: {r.Detected})");
        }
        catch (Exception ex) { Check("LibreTranslate en→tr", false, ex.Message); }

        var self = Environment.ProcessPath!;
        using var child = Process.Start(new ProcessStartInfo(self, "window") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var points = new Dictionary<string, int[]>();
        string? line;
        while ((line = child.StandardOutput.ReadLine()) != null && line != "READY")
        {
            var parts = line.Split(' ');
            points[parts[0]] = parts.Skip(1).Select(int.Parse).ToArray();
        }
        await Task.Delay(1500); // let the window finish appearing (first launch is slower)
        Native.POINT P(string name) => new(points[name][0], points[name][1]);

        try
        {
            using var locator = new TextLocator();
            await Expect(locator, "UIA: İngilizce cümle (RichTextBox)", P("RTB_NEVER"),
                "It runs entirely on your own machine, so your text never leaves the computer.", "uia");
            await Expect(locator, "UIA: Almanca ikinci cümle", P("RTB_WETTER"), "Das Wetter ist heute sehr schön.", "uia");
            await Expect(locator, "UIA: Türkçe cümle", P("RTB_TR"), P3, "uia");
            await Expect(locator, "OCR: sadece piksel olarak çizilmiş metin", P("OCR_LAZY"), "The quick brown fox jumps over the lazy dog.", "ocr");
            await Expect(locator, "OCR: metin kutusunun içindeki resim", P("RTB_IMG"), ImageText, "ocr");
            await ExpectNothing(locator, "Boş alanda hiçbir şey bulunmamalı", new Native.POINT(points["FORM"][0] + points["FORM"][2] - 40, points["FORM"][1] + points["FORM"][3] - 30));

            if (args.Length >= 2 && args[0] == "e2e") await EndToEnd(args[1], P, points["FORM"]);
            if (args.Length >= 2 && args[0] == "screen") await ScreenOnly(args[1]);
        }
        finally
        {
            try { child.Kill(); } catch { }
        }

        Console.WriteLine(failures == 0 ? "\nTÜM TESTLER GEÇTİ" : $"\n{failures} TEST BAŞARISIZ");
        return failures == 0 ? 0 : 1;
    }

    static void SentenceTests()
    {
        string Around(string s, string needle)
        {
            var b = Sentence.Bounds(s, s.IndexOf(needle, StringComparison.Ordinal));
            return b == null ? "<null>" : s[b.Value.Start..b.Value.End];
        }
        Check("Cümle: ortadaki cümle", Around(P1, "never") == "It runs entirely on your own machine, so your text never leaves the computer.");
        Check("Cümle: 1.9.6 ve example.com bölmüyor", Around(P1, "released") == "Version 1.9.6 was released recently, see example.com for details!");
        Check("Cümle: paragraf sonu ayırıyor", Around("Line one without a period\rLine two", "two") == "Line two");
        Check("Cümle: satır kaydırma birleşiyor", Around("This sentence wraps\nacross two lines. Next.", "across") == "This sentence wraps\nacross two lines.");
        Check("Cümle: tırnak dahil", Around("He said \"Stop!\" Then left.", "Stop") == "He said \"Stop!\"");
    }

    static async Task Expect(TextLocator locator, string name, Native.POINT p, string expected, string source)
    {
        var sw = Stopwatch.StartNew();
        var hit = await locator.SentenceAtAsync(p, allowOcr: true);
        var ms = sw.ElapsedMilliseconds;
        if (hit == null) { Check(name, false, "hiçbir şey bulunamadı"); return; }
        bool ok = hit.Source == source && (source == "ocr" ? Similar(hit.Text, expected) : hit.Text == expected);
        Check(name, ok, $"[{hit.Source}, {ms} ms, {hit.Rects.Count} kutu] '{hit.Text}'");
    }

    static async Task ExpectNothing(TextLocator locator, string name, Native.POINT p)
    {
        var hit = await locator.SentenceAtAsync(p, allowOcr: true);
        Check(name, hit == null, hit == null ? "" : $"[{hit.Source}] '{hit.Text}'");
    }

    // OCR may get a character or two wrong.
    static bool Similar(string a, string b)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var x = Norm(a);
        var y = Norm(b);
        int diff = Math.Abs(x.Length - y.Length) + x.Zip(y).Count(t => t.First != t.Second);
        return diff <= Math.Max(2, y.Length / 15);
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "GEÇTİ " : "HATA  ")} {name}{(detail.Length > 0 ? "  →  " + detail : "")}");
    }

    // ---------------- End to end: the real app, real mouse ----------------

    static async Task EndToEnd(string exe, Func<string, Native.POINT> P, int[] form)
    {
        foreach (var old in Process.GetProcessesByName("LocalTranslate")) { try { old.Kill(); old.WaitForExit(); } catch { } }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        var outDir = Path.Combine(AppContext.BaseDirectory, "e2e");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        psi.Environment["LOCALTRANSLATE_DEBUG_CAPTURE"] = "1";
        psi.Environment["LOCALTRANSLATE_DEBUG_DUMP"] = Path.Combine(outDir, "screen");
        using var app = Process.Start(psi)!;
        var area = new Rectangle(form[0] - 10, form[1] - 10, form[2] + 20, form[3] + 260);

        try
        {
            await Task.Delay(3000);
            await HoverAt(P("RTB_NEVER"));
            Shot(area, Path.Combine(outDir, "1-hover-uia.png"));

            await HoverAt(P("OCR_LAZY"));
            Shot(area, Path.Combine(outDir, "2-hover-ocr.png"));

            await HoverAt(P("RTB_TR"));
            Shot(area, Path.Combine(outDir, "3-hover-turkish.png"));

            Drag(P("SEL_START"), P("SEL_END"));
            await Task.Delay(2500);
            Shot(area, Path.Combine(outDir, "4-selection.png"));
            Click(new Native.POINT(form[0] + form[2] - 40, form[1] + form[3] - 30)); // dismiss, clear selection

            // Ctrl+Alt+F9: off → toast, and hovering shows nothing; then back on.
            await Task.Delay(300);
            Keys(0x11, 0x12, 0x78);
            await Task.Delay(400);
            Shot(area, Path.Combine(outDir, "5-toggle-off-toast.png"));
            await HoverAt(P("RTB_WETTER"));
            Shot(area, Path.Combine(outDir, "6-off-hover-nothing.png"));
            Keys(0x11, 0x12, 0x78);
            await HoverAt(P("RTB_WETTER"));
            Shot(area, Path.Combine(outDir, "7-back-on.png"));

            // Picture inside a text box: hover reads it with OCR.
            await HoverAt(P("RTB_IMG"));
            Shot(area, Path.Combine(outDir, "8-hover-image.png"));

            // Ctrl+Alt+F11: frozen screen with the recognised words; drag over the second sentence.
            Click(new Native.POINT(form[0] + form[2] - 40, form[1] + form[3] - 30));
            await Task.Delay(500);
            Keys(0x11, 0x12, 0x7A);
            await Task.Delay(2500);
            Shot(area, Path.Combine(outDir, "9-region-words.png"));
            Drag(P("REGION_START"), P("REGION_END"));
            await Task.Delay(3000);
            Shot(area, Path.Combine(outDir, "10-region-selected.png"));
            Click(new Native.POINT(form[0] + form[2] - 40, form[1] + form[3] - 30));

            // Ctrl+Alt+F10: whole screen. The app dumps "screenshot + overlay" PNGs (the
            // overlay itself is invisible to screen capture by design).
            await Task.Delay(800);
            SetCursorPos(P("RTB_NEVER").X, P("RTB_NEVER").Y);
            Keys(0x11, 0x12, 0x79);
            await Task.Delay(15000);
            Keys(0x11, 0x12, 0x79);
            await Task.Delay(500);
            Console.WriteLine($"Ekran görüntüleri: {outDir}");
        }
        finally
        {
            try { app.Kill(); } catch { }
        }
    }

    /// <summary>Only the whole-screen mode, without moving the mouse.</summary>
    static async Task ScreenOnly(string exe)
    {
        foreach (var old in Process.GetProcessesByName("LocalTranslate")) { try { old.Kill(); old.WaitForExit(); } catch { } }
        var outDir = Path.Combine(AppContext.BaseDirectory, "e2e", "screen");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["LOCALTRANSLATE_DEBUG_DUMP"] = outDir;
        using var app = Process.Start(psi)!;
        try
        {
            await Task.Delay(3000);
            Keys(0x11, 0x12, 0x79);
            await Task.Delay(12000);
            Keys(0x11, 0x12, 0x79);
            await Task.Delay(500);
            Console.WriteLine($"Ekran dökümleri: {outDir}");
        }
        finally { try { app.Kill(); } catch { } }
    }

    static async Task HoverAt(Native.POINT p)
    {
        SetCursorPos(p.X - 30, p.Y + 40); // arrive from elsewhere, like a real mouse
        await Task.Delay(150);
        SetCursorPos(p.X, p.Y);
        await Task.Delay(3000);
    }

    static void Drag(Native.POINT from, Native.POINT to)
    {
        SetCursorPos(from.X, from.Y);
        Thread.Sleep(150);
        MouseButton(down: true);
        for (int i = 1; i <= 15; i++)
        {
            SetCursorPos(from.X + (to.X - from.X) * i / 15, from.Y + (to.Y - from.Y) * i / 15);
            Thread.Sleep(25);
        }
        MouseButton(down: false);
    }

    static void Click(Native.POINT p)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(80);
        MouseButton(true);
        MouseButton(false);
    }

    static void Keys(params ushort[] vks)
    {
        Native.INPUT Key(ushort vk, bool up) => new()
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } }
        };
        var inputs = vks.Select(v => Key(v, false)).Concat(vks.Reverse().Select(v => Key(v, true))).ToArray();
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    static void MouseButton(bool down)
    {
        var input = new Native.INPUT { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dwFlags = down ? 0x2u : 0x4u } } };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    static void Shot(Rectangle area, string path)
    {
        using var bmp = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        bmp.Save(path, ImageFormat.Png);
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
}
