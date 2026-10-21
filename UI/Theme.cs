using System.Windows.Media;
using Microsoft.Win32;

namespace LocalTranslate.UI;

/// <summary>Bubble colours that follow the Windows light/dark app setting.</summary>
internal sealed record Theme(Brush Background, Brush Foreground, Brush Muted, Brush Accent, Brush Error, Brush ErrorText, Brush Highlight)
{
    static Brush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static readonly Theme Light = new(B("#FFFFFF"), B("#111827"), B("#6B7280"), B("#6366F1"), B("#DC2626"), B("#B91C1C"), B("#386366F1"));
    public static readonly Theme Dark = new(B("#1F2937"), B("#F3F4F6"), B("#9CA3AF"), B("#818CF8"), B("#F87171"), B("#FCA5A5"), B("#40818CF8"));

    public static Theme Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0 ? Dark : Light;
            }
            catch { return Light; }
        }
    }
}
