using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LocalTranslate.Screen;

/// <summary>
/// Transparent, click-through layer over one monitor that paints each translation
/// on top of the original text (Google Lens style). It is always excluded from
/// screen capture — otherwise the next OCR pass would read our own translations.
/// </summary>
internal sealed class ScreenOverlayWindow : Window
{
    readonly Canvas canvas = new();
    readonly Dictionary<ScreenBlock, FrameworkElement> shown = new();
    readonly List<Rect> invalidated = new(); // areas that changed after the last OCR pass
    readonly Native.RECT monitor;
    IntPtr hwnd;
    double scale = 1;

    public ScreenOverlayWindow(Native.RECT monitor)
    {
        this.monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Content = canvas;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
    }

    public void ShowOverMonitor()
    {
        if (!IsVisible) Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    public void Clear()
    {
        canvas.Children.Clear();
        shown.Clear();
        invalidated.Clear();
    }

    /// <summary>Drops translations whose text just changed underneath (scrolling, typing, new page).</summary>
    public void RemoveIn(IReadOnlyList<Rect> changed)
    {
        invalidated.AddRange(changed);
        foreach (var (block, element) in shown.Where(x => changed.Any(r => r.IntersectsWith(x.Key.Box))).ToList())
        {
            canvas.Children.Remove(element);
            shown.Remove(block);
        }
    }

    public void Add(ScreenBlock block, string translation)
    {
        // The text moved since it was read: this translation would land on the wrong spot.
        if (invalidated.Any(r => r.IntersectsWith(block.Box)) || shown.ContainsKey(block)) return;

        double x = (block.Box.X - monitor.Left) / scale;
        double y = (block.Box.Y - monitor.Top) / scale;
        double w = block.Box.Width / scale;
        double h = block.Box.Height / scale;
        const double pad = 3;

        var text = new TextBlock
        {
            Text = translation,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(block.Foreground),
            FontFamily = new FontFamily("Segoe UI"),
        };

        // Largest font (up to the original's size) that fits the original's box.
        double font = Math.Clamp(block.LineHeight / scale * 0.82, 8, 48);
        while (true)
        {
            text.FontSize = font;
            text.Measure(new Size(w + 2 * pad, double.PositiveInfinity));
            if (text.DesiredSize.Height <= h + 2 || font <= 8) break;
            font -= 0.5;
        }

        var card = new Border
        {
            Background = Brush(block.Background),
            Padding = new Thickness(pad, 0, pad, 0),
            Width = w + 4 * pad,
            MinHeight = h + 4,
            CornerRadius = new CornerRadius(2),
            Child = text
        };
        Canvas.SetLeft(card, x - 2 * pad);
        Canvas.SetTop(card, y - 2);
        canvas.Children.Add(card);
        shown[block] = card;
    }

    static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Debug aid: the screenshot with this layer painted on top, as a PNG.</summary>
    public void SaveComposite(System.Drawing.Bitmap shot, string path)
    {
        canvas.UpdateLayout();
        int pw = shot.Width, ph = shot.Height;
        var layer = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        layer.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(layer));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var overlay = new System.Drawing.Bitmap(ms);
        using var result = new System.Drawing.Bitmap(shot);
        using (var g = System.Drawing.Graphics.FromImage(result)) g.DrawImage(overlay, 0, 0, pw, ph);
        result.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
