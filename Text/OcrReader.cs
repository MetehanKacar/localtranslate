using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Rect = System.Windows.Rect;

namespace LocalTranslate.Text;

/// <summary>A recognised word; Box is in physical screen pixels.</summary>
internal sealed record OcrWord(string Text, Rect Box);
internal sealed record OcrLine(IReadOnlyList<OcrWord> Words, Rect Box);

/// <summary>
/// Reads text from pixels with the built-in Windows OCR engine: text inside
/// images, and apps that don't expose their text to UI Automation.
/// </summary>
internal sealed class OcrReader
{
    readonly OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();

    public bool Available => engine != null;

    public static Bitmap Capture(Native.RECT r)
    {
        var shot = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(shot);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, shot.Size, CopyPixelOperation.SourceCopy);
        return shot;
    }

    /// <summary>OCR a screenshot taken at <paramref name="origin"/>; word boxes come back in screen pixels.</summary>
    public async Task<List<OcrLine>> ReadAsync(Bitmap shot, Native.POINT origin, double maxScale = 2.0)
    {
        var lines = new List<OcrLine>();
        if (engine == null) return lines;

        // Upscaling small UI text noticeably improves recognition.
        double max = OcrEngine.MaxImageDimension;
        double scale = Math.Min(maxScale, Math.Min(max / shot.Width, max / shot.Height));
        int w = (int)(shot.Width * scale), h = (int)(shot.Height * scale);

        SoftwareBitmap input;
        using (var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(shot, 0, 0, w, h);
            }
            input = ToSoftwareBitmap(scaled);
        }

        var result = await engine.RecognizeAsync(input);
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(x => new OcrWord(x.Text, new Rect(
                    x.BoundingRect.X / scale + origin.X, x.BoundingRect.Y / scale + origin.Y,
                    x.BoundingRect.Width / scale, x.BoundingRect.Height / scale)))
                .ToList();
            if (words.Count > 0) lines.Add(new OcrLine(words, words.Select(x => x.Box).Aggregate(Rect.Union)));
        }
        return lines;
    }

    /// <summary>The sentence under <paramref name="p"/>, read from a band of the window around it.</summary>
    public async Task<TextHit?> SentenceAtAsync(Native.POINT p)
    {
        if (engine == null) return null;
        try
        {
            var region = CaptureRegion(p);
            if (region.Width < 16 || region.Height < 16) return null;

            List<OcrLine> lines;
            using (var shot = Capture(region))
                lines = await ReadAsync(shot, new Native.POINT(region.Left, region.Top));

            var (_, _, dpi) = Native.MonitorAt(p);
            return FindWord(lines, p.X, p.Y, 3 * dpi, out int li, out int wi) ? SentenceFromLines(lines, li, wi) : null;
        }
        catch (Exception ex) when (ex is ExternalException or COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The word at (x, y): the one under it, or the nearest one when (x, y) is in a gap within a line.</summary>
    public static bool FindWord(IReadOnlyList<OcrLine> lines, double x, double y, double slack, out int line, out int word)
    {
        for (line = 0; line < lines.Count; line++)
        {
            var box = lines[line].Box;
            box.Inflate(slack, slack);
            if (!box.Contains(x, y)) continue;

            var words = lines[line].Words;
            word = 0;
            double best = double.MaxValue;
            for (int j = 0; j < words.Count; j++)
            {
                var r = words[j].Box;
                double d = x < r.Left ? r.Left - x : x > r.Right ? x - r.Right : 0;
                if (d < best) { best = d; word = j; }
            }
            return true;
        }
        line = word = -1;
        return false;
    }

    /// <summary>The sentence containing word <paramref name="wi"/> of line <paramref name="li"/>, following it across wrapped lines.</summary>
    public static TextHit? SentenceFromLines(IReadOnlyList<OcrLine> lines, int li, int wi)
    {
        int top = li, bottom = li;
        while (top > 0 && SameParagraph(lines[top - 1].Box, lines[top].Box)) top--;
        while (bottom < lines.Count - 1 && SameParagraph(lines[bottom].Box, lines[bottom + 1].Box)) bottom++;

        var text = new StringBuilder();
        var spans = new List<(int Start, int End, int Line, Rect Box)>();
        int caret = 0;
        for (int i = top; i <= bottom; i++)
        {
            for (int j = 0; j < lines[i].Words.Count; j++)
            {
                var word = lines[i].Words[j];
                if (i == li && j == wi) caret = text.Length;
                spans.Add((text.Length, text.Length + word.Text.Length, i, word.Box));
                text.Append(word.Text).Append(' ');
            }
        }

        var s = text.ToString();
        var bounds = Sentence.Bounds(s, caret);
        if (bounds == null) return null;
        var (start, end) = bounds.Value;
        var sentence = Sentence.Clean(s[start..end]);
        if (!Sentence.HasLetters(sentence)) return null;

        // One rectangle per line, for the highlight.
        var rects = spans
            .Where(x => x.End > start && x.Start < end)
            .GroupBy(x => x.Line)
            .Select(g => g.Select(x => x.Box).Aggregate(Rect.Union));
        return new TextHit(sentence, rects, "ocr");
    }

    /// <summary>The selected words in reading order; separate paragraphs stay on separate lines.</summary>
    public static TextHit? FromWords(IReadOnlyList<OcrLine> lines, Func<OcrWord, bool> isSelected)
    {
        var text = new StringBuilder();
        var rects = new List<Rect>();
        int previous = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var words = lines[i].Words.Where(isSelected).ToList();
            if (words.Count == 0) continue;
            if (text.Length > 0)
                text.Append(previous >= 0 && SameParagraph(lines[previous].Box, lines[i].Box) ? " " : "\n");
            text.Append(string.Join(" ", words.Select(x => x.Text)));
            rects.Add(words.Select(x => x.Box).Aggregate(Rect.Union));
            previous = i;
        }
        var result = text.ToString().Trim();
        return result.Length > 0 && Sentence.HasLetters(result) ? new TextHit(result, rects, "ocr") : null;
    }

    /// <summary>The window under the cursor, clipped to its monitor, in a band around the cursor.</summary>
    static Native.RECT CaptureRegion(Native.POINT p)
    {
        var (monitor, _, dpiScale) = Native.MonitorAt(p);
        var area = monitor;
        var root = Native.GetAncestor(Native.WindowFromPoint(p), Native.GA_ROOT);
        if (root != IntPtr.Zero && Native.GetWindowRect(root, out var win))
        {
            area.Left = Math.Max(area.Left, win.Left);
            area.Top = Math.Max(area.Top, win.Top);
            area.Right = Math.Min(area.Right, win.Right);
            area.Bottom = Math.Min(area.Bottom, win.Bottom);
        }
        int half = (int)(200 * dpiScale);
        return new Native.RECT
        {
            Left = area.Left,
            Right = area.Right,
            Top = Math.Max(area.Top, p.Y - half),
            Bottom = Math.Min(area.Bottom, p.Y + half)
        };
    }

    public static bool SameParagraph(Rect above, Rect below)
    {
        double h = Math.Max(above.Height, below.Height);
        double gap = below.Top - above.Bottom;
        bool stacked = gap > -h * 0.3 && gap < h * 0.9;
        bool overlapping = above.Left < below.Right && below.Left < above.Right;
        bool sameSize = Math.Abs(above.Height - below.Height) < h * 0.45;
        return stacked && overlapping && sameSize;
    }

    static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes),
                BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
