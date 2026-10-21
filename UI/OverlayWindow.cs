using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LocalTranslate.UI;

internal enum BubbleState { Done, Loading, Error, Note }

/// <summary>
/// The translation bubble. Never takes focus; the hover variant is also
/// click-through so it can't get in the way of the text below it.
/// </summary>
internal sealed class OverlayWindow : Window
{
    const double ShadowMargin = 12;
    const double MaxTextWidth = 560;

    readonly bool interactive;
    readonly Border card;
    readonly TextBlock header;
    readonly TextBlock body;
    readonly TextBlock copy;
    readonly DispatcherTimer autoHide;
    IntPtr hwnd;

    public OverlayWindow(bool interactive)
    {
        this.interactive = interactive;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;

        header = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        copy = new TextBlock
        {
            Text = Loc.T("bubble.copy"),
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(16, 0, 0, 2),
            Visibility = Visibility.Collapsed
        };
        copy.MouseLeftButtonUp += (_, _) => CopyText();

        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(copy, Dock.Right);
        top.Children.Add(copy);
        top.Children.Add(header);

        body = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = MaxTextWidth, LineHeight = 20 };

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(body);

        card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 7, 12, 9),
            Margin = new Thickness(ShadowMargin),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.3 }
        };
        Content = card;

        autoHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        autoHide.Tick += (_, _) => { autoHide.Stop(); Hide(); };

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int style = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | (interactive ? 0 : Native.WS_EX_TRANSPARENT);
            Native.AddExStyle(hwnd, style);
            Native.ExcludeFromCapture(hwnd);
        };
    }

    /// <summary>Shows the bubble just below <paramref name="anchor"/> (physical screen pixels).</summary>
    public void ShowBelow(Rect anchor, string title, string text, BubbleState state)
    {
        autoHide.Stop();
        if (anchor.IsEmpty)
        {
            Native.GetCursorPos(out var c);
            anchor = new Rect(c.X, c.Y - 10, 1, 20);
        }

        var theme = Theme.Current;
        card.Background = theme.Background;
        card.BorderBrush = state == BubbleState.Error ? theme.Error : theme.Accent;
        header.Visibility = Visibility.Visible;
        header.Foreground = theme.Muted;
        header.Text = title;
        body.Text = text;
        body.FontStyle = state == BubbleState.Loading ? FontStyles.Italic : FontStyles.Normal;
        body.Foreground = state switch
        {
            BubbleState.Loading or BubbleState.Note => theme.Muted,
            BubbleState.Error => theme.ErrorText,
            _ => theme.Foreground
        };
        copy.Foreground = theme.Accent;
        copy.Text = Loc.T("bubble.copy");
        copy.Visibility = interactive && state == BubbleState.Done ? Visibility.Visible : Visibility.Collapsed;

        var anchorPoint = new Native.POINT((int)anchor.Left, (int)anchor.Bottom);
        var (_, work, scale) = Native.MonitorAt(anchorPoint);

        // As wide as the sentence (so it reads as "the line under it"), within limits.
        double maxWidth = Math.Min(MaxTextWidth, work.Width / scale - 2 * ShadowMargin - 40);
        body.MaxWidth = maxWidth;
        body.MinWidth = Math.Clamp(anchor.Width / scale - 27, 120, maxWidth);

        Place(anchor, work, scale);
    }

    /// <summary>A short status message near the cursor that disappears by itself.</summary>
    public void ShowToast(string text)
    {
        var theme = Theme.Current;
        card.Background = theme.Background;
        card.BorderBrush = theme.Accent;
        header.Visibility = Visibility.Collapsed;
        body.Text = text;
        body.FontStyle = FontStyles.Normal;
        body.Foreground = theme.Foreground;
        body.MinWidth = 0;
        copy.Visibility = Visibility.Collapsed;

        Native.GetCursorPos(out var c);
        var (_, work, scale) = Native.MonitorAt(c);
        Place(new Rect(c.X + 8, c.Y + 8, 1, 16), work, scale);
        autoHide.Start();
    }

    /// <summary>Positions the bubble just below <paramref name="anchor"/>, or above it when there is no room.</summary>
    void Place(Rect anchor, Native.RECT work, double scale)
    {
        var content = (UIElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        int width = (int)Math.Ceiling(size.Width * scale);
        int height = (int)Math.Ceiling(size.Height * scale);
        int margin = (int)(ShadowMargin * scale);
        int gap = (int)(3 * scale);

        int x = (int)anchor.Left - margin;
        x = Math.Max(work.Left - margin, Math.Min(x, work.Right - width + margin));

        int y = (int)anchor.Bottom + gap - margin;
        if (y + height - margin > work.Bottom)
        {
            int above = (int)anchor.Top - gap - height + margin;
            y = above >= work.Top - margin ? above : Math.Max(work.Top - margin, work.Bottom - height + margin);
        }

        bool appearing = !IsVisible;
        if (appearing)
        {
            Left = -32000;
            Top = -32000;
            Opacity = 0;
            Show();
        }
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        if (appearing) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
    }

    public bool ContainsScreenPoint(Native.POINT p)
    {
        if (!IsVisible || hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    void CopyText()
    {
        try
        {
            Clipboard.SetText(body.Text);
            copy.Text = Loc.T("bubble.copied");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            copy.Text = Loc.T("bubble.copyFailed");
        }
    }
}
