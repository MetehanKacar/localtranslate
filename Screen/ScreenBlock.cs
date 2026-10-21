using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using LocalTranslate.Text;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;

namespace LocalTranslate.Screen;

/// <summary>A piece of on-screen text translated as one unit (a paragraph, a label, a menu item).</summary>
internal sealed class ScreenBlock
{
    public required string Text { get; init; }
    public required Rect Box { get; init; }          // physical screen pixels
    public required double LineHeight { get; init; } // physical pixels
    public Color Background { get; set; }
    public Color Foreground { get; set; }
}

internal static class BlockBuilder
{
    /// <summary>
    /// OCR lines → blocks. A line is first split where words are far apart (menu
    /// bars, table columns, toolbar labels), then stacked pieces that look like one
    /// paragraph are merged so sentences that wrap are translated whole.
    /// </summary>
    public static List<ScreenBlock> Build(IReadOnlyList<OcrLine> lines, PixelSampler pixels)
    {
        var segments = new List<List<OcrWord>>();
        foreach (var line in lines)
        {
            double h = line.Box.Height;
            var current = new List<OcrWord> { line.Words[0] };
            for (int i = 1; i < line.Words.Count; i++)
            {
                if (line.Words[i].Box.Left - line.Words[i - 1].Box.Right > 1.3 * h)
                {
                    segments.Add(current);
                    current = new List<OcrWord>();
                }
                current.Add(line.Words[i]);
            }
            segments.Add(current);
        }

        var groups = new List<List<(List<OcrWord> Words, Rect Box)>>();
        foreach (var words in segments)
        {
            var box = words.Select(w => w.Box).Aggregate(Rect.Union);
            var group = groups.FirstOrDefault(g => OcrReader.SameParagraph(g[^1].Box, box) && Math.Abs(g[^1].Box.Left - box.Left) < 3 * box.Height);
            if (group != null) group.Add((words, box));
            else groups.Add(new() { (words, box) });
        }

        var blocks = new List<ScreenBlock>();
        foreach (var g in groups)
        {
            var text = Sentence.Clean(string.Join(" ", g.SelectMany(s => s.Words).Select(w => w.Text)));
            if (text.Count(char.IsLetter) < 3 || LooksLikeCode(text)) continue; // icons, stray marks, code
            var heights = g.Select(s => s.Box.Height).OrderBy(x => x).ToList();
            var block = new ScreenBlock
            {
                Text = text,
                Box = g.Select(s => s.Box).Aggregate(Rect.Union),
                LineHeight = heights[heights.Count / 2]
            };
            (block.Background, block.Foreground) = pixels.Colors(block.Box);
            blocks.Add(block);
        }
        return blocks;
    }

    const string CodeSymbols = "{}[]()<>;=/\\|_$#@*&^%~`\"";

    /// <summary>Commands, paths and source code: translating them only produces nonsense.</summary>
    static bool LooksLikeCode(string text)
    {
        int symbols = text.Count(c => CodeSymbols.IndexOf(c) >= 0);
        if (symbols >= 3 && symbols > text.Length * 0.06) return true;
        return text.Split(' ').Any(t => t.Length > 6 && (t.Contains("::") || t.Contains("->") || t.Contains(":/") || t.Contains(":\\") || t.Count(c => c is '/' or '\\') >= 2));
    }

    /// <summary>Sentences of a block, translated one by one so each gets its own language detection.</summary>
    public static List<string> Sentences(string text)
    {
        var result = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            var bounds = Sentence.Bounds(text, i);
            if (bounds == null) break;
            var (start, end) = bounds.Value;
            if (end <= i) break;
            result.Add(text[start..end]);
            i = end;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }
        return result.Count > 0 ? result : new List<string> { text };
    }
}

/// <summary>Reads pixel colours of a screenshot (copied once, so it can be used after the bitmap is gone).</summary>
internal sealed class PixelSampler
{
    readonly byte[] data;
    readonly int width, height, stride;
    readonly Native.POINT origin;

    public PixelSampler(Bitmap shot, Native.POINT origin)
    {
        this.origin = origin;
        width = shot.Width;
        height = shot.Height;
        var bits = shot.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = bits.Stride;
            data = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(bits.Scan0, data, 0, data.Length);
        }
        finally { shot.UnlockBits(bits); }
    }

    (byte R, byte G, byte B) At(double screenX, double screenY)
    {
        int x = Math.Clamp((int)screenX - origin.X, 0, width - 1);
        int y = Math.Clamp((int)screenY - origin.Y, 0, height - 1);
        int i = y * stride + x * 4;
        return (data[i + 2], data[i + 1], data[i]);
    }

    /// <summary>
    /// Background = median colour just around the box; text = the pixel inside the
    /// box that differs most from it (anti-aliasing makes the average useless).
    /// </summary>
    public (Color Background, Color Foreground) Colors(Rect box)
    {
        var around = new List<(byte R, byte G, byte B)>();
        double left = box.Left - 3, right = box.Right + 3, top = box.Top - 3, bottom = box.Bottom + 3;
        for (double x = left; x <= right; x += 3) { around.Add(At(x, top)); around.Add(At(x, bottom)); }
        for (double y = top; y <= bottom; y += 3) { around.Add(At(left, y)); around.Add(At(right, y)); }
        byte Median(Func<(byte R, byte G, byte B), byte> channel) => around.Select(channel).OrderBy(v => v).ElementAt(around.Count / 2);
        var bg = (R: Median(c => c.R), G: Median(c => c.G), B: Median(c => c.B));

        (byte R, byte G, byte B) fg = bg;
        int best = -1;
        for (double y = box.Top; y < box.Bottom; y += 2)
            for (double x = box.Left; x < box.Right; x += 2)
            {
                var c = At(x, y);
                int d = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (d > best) { best = d; fg = c; }
            }

        if (best < 120) // too little contrast to trust: plain black or white
        {
            bool darkBackground = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B < 128;
            fg = darkBackground ? ((byte)255, (byte)255, (byte)255) : ((byte)17, (byte)17, (byte)17);
        }
        return (Color.FromRgb(bg.R, bg.G, bg.B), Color.FromRgb(fg.R, fg.G, fg.B));
    }
}
