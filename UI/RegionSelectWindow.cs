using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LocalTranslate.Text;
using Bitmap = System.Drawing.Bitmap;

namespace LocalTranslate.UI;

/// <summary>
/// Freezes one monitor as a screenshot, shows the words OCR found on it, and
/// lets the user select text that is otherwise unselectable (images, videos,
/// canvases): drag across words, or click a word to take its whole sentence.
/// </summary>
internal sealed class RegionSelectWindow : Window
{
    public event Action<TextHit>? Selected;

    readonly Native.RECT monitor;
    readonly Canvas words = new();
    readonly Canvas marks = new();
    readonly Rectangle dragBox;
    readonly TextBlock hint;
    readonly Brush accent, accentFill, wordFill;
    Task<List<OcrLine>>? ocr;
    List<OcrLine> lines = new();
    Point? dragStart;
    double scale = 1;
    IntPtr hwnd;
    bool finished;

    public RegionSelectWindow(Bitmap shot, Native.RECT monitor)
    {
        this.monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;

        accent = Theme.Light.Accent;
        accentFill = new SolidColorBrush(Color.FromArgb(0x55, 0x63, 0x66, 0xF1));
        wordFill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        dragBox = new Rectangle { Stroke = accent, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0x63, 0x66, 0xF1)), Visibility = Visibility.Collapsed };
        var dragLayer = new Canvas();
        dragLayer.Children.Add(dragBox);

        hint = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI"), Text = Loc.T("region.reading") };
        var hintCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x18, 0x27)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Child = hint,
            IsHitTestVisible = false
        };

        var grid = new Grid();
        grid.Children.Add(new Image { Source = ToBitmapSource(shot), Stretch = Stretch.Fill });
        grid.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)) });
        grid.Children.Add(words);
        grid.Children.Add(marks);
        grid.Children.Add(dragLayer);
        grid.Children.Add(hintCard);
        Content = grid;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.ExcludeFromCapture(hwnd);
            CoverMonitor();
        };
        Loaded += async (_, _) =>
        {
            CoverMonitor(); // again, after any DPI change from moving to this monitor
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Activate();
            Focus();
            await ShowWordsAsync();
        };

        MouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(this);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (dragStart is not { } start) return;
            var r = new Rect(start, e.GetPosition(this));
            dragBox.Visibility = Visibility.Visible;
            Canvas.SetLeft(dragBox, r.X);
            Canvas.SetTop(dragBox, r.Y);
            dragBox.Width = r.Width;
            dragBox.Height = r.Height;
            Mark(ToScreen(r));
        };
        MouseLeftButtonUp += async (_, e) =>
        {
            if (dragStart is not { } start) return;
            ReleaseMouseCapture();
            dragStart = null;
            await FinishAsync(start, e.GetPosition(this));
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (!finished) Close(); };
    }

    public void SetOcr(Task<List<OcrLine>> task) => ocr = task;

    void CoverMonitor() =>
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, Native.SWP_SHOWWINDOW);

    async Task<List<OcrLine>> LinesAsync()
    {
        if (ocr == null) return lines;
        try { lines = await ocr; } catch { lines = new(); }
        return lines;
    }

    async Task ShowWordsAsync()
    {
        var found = await LinesAsync();
        foreach (var word in found.SelectMany(l => l.Words))
        {
            var r = ToDip(word.Box);
            var box = new Rectangle { Width = r.Width + 4, Height = r.Height + 2, RadiusX = 2, RadiusY = 2, Fill = wordFill, Stroke = accent, StrokeThickness = 0.6, Opacity = 0.9 };
            Canvas.SetLeft(box, r.X - 2);
            Canvas.SetTop(box, r.Y - 1);
            words.Children.Add(box);
        }
        hint.Text = found.Count == 0
            ? Loc.T("region.none")
            : Loc.T("region.help");
    }

    void Mark(Rect screenArea)
    {
        marks.Children.Clear();
        foreach (var word in lines.SelectMany(l => l.Words).Where(w => w.Box.IntersectsWith(screenArea)))
        {
            var r = ToDip(word.Box);
            var mark = new Rectangle { Width = r.Width + 4, Height = r.Height + 2, RadiusX = 2, RadiusY = 2, Fill = accentFill };
            Canvas.SetLeft(mark, r.X - 2);
            Canvas.SetTop(mark, r.Y - 1);
            marks.Children.Add(mark);
        }
    }

    async Task FinishAsync(Point a, Point b)
    {
        var found = await LinesAsync();
        TextHit? hit = null;
        if ((a - b).Length < 4)
        {
            var p = ToScreen(b);
            if (OcrReader.FindWord(found, p.X, p.Y, 4 * scale, out int li, out int wi))
                hit = OcrReader.SentenceFromLines(found, li, wi);
        }
        else
        {
            var area = ToScreen(new Rect(a, b));
            hit = OcrReader.FromWords(found, w => w.Box.IntersectsWith(area));
        }

        if (hit == null)
        {
            dragBox.Visibility = Visibility.Collapsed;
            marks.Children.Clear();
            hint.Text = Loc.T("region.noneThere");
            return;
        }

        finished = true;
        Close();
        Selected?.Invoke(hit);
    }

    Rect ToDip(Rect screen) =>
        new((screen.X - monitor.Left) / scale, (screen.Y - monitor.Top) / scale, screen.Width / scale, screen.Height / scale);

    Point ToScreen(Point dip) => new(monitor.Left + dip.X * scale, monitor.Top + dip.Y * scale);

    Rect ToScreen(Rect dip) => new(ToScreen(dip.TopLeft), ToScreen(dip.BottomRight));

    static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * data.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
