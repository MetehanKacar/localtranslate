using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LocalTranslate.UI;

/// <summary>Click-through marker drawn over the sentence being translated.</summary>
internal sealed class HighlightWindow : Window
{
    readonly Canvas canvas = new();
    IntPtr hwnd;

    public HighlightWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            Native.ExcludeFromCapture(hwnd);
        };
    }

    public void ShowRects(IReadOnlyList<Rect> rects)
    {
        if (rects.Count == 0) { Hide(); return; }

        const double padX = 2, padY = 1;
        var bounds = rects.Aggregate(Rect.Union);
        bounds.Inflate(padX, padY);
        var center = new Native.POINT((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        var (_, _, scale) = Native.MonitorAt(center);

        var fill = Theme.Current.Highlight;
        canvas.Children.Clear();
        foreach (var r in rects)
        {
            var mark = new Rectangle
            {
                Width = (r.Width + 2 * padX) / scale,
                Height = (r.Height + 2 * padY) / scale,
                RadiusX = 3,
                RadiusY = 3,
                Fill = fill
            };
            Canvas.SetLeft(mark, (r.Left - padX - bounds.Left) / scale);
            Canvas.SetTop(mark, (r.Top - padY - bounds.Top) / scale);
            canvas.Children.Add(mark);
        }

        if (!IsVisible)
        {
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            Show();
        }
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)bounds.Left, (int)bounds.Top,
            (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}
