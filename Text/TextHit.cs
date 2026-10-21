using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace LocalTranslate.Text;

/// <summary>A piece of on-screen text and where it is (physical screen pixels).</summary>
internal sealed class TextHit
{
    public string Text { get; }
    public IReadOnlyList<Rect> Rects { get; }
    public string Source { get; }
    public Rect Bounds { get; }

    public TextHit(string text, IEnumerable<Rect> rects, string source)
    {
        Text = text;
        Rects = rects.Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0).ToList();
        Source = source;
        Bounds = Rects.Count == 0 ? Rect.Empty : Rects.Aggregate(Rect.Union);
    }

    public bool Contains(Native.POINT p, double slack)
    {
        foreach (var r in Rects)
        {
            var big = r;
            big.Inflate(slack, slack);
            if (big.Contains(p.X, p.Y)) return true;
        }
        return false;
    }

    public bool IsNear(Native.POINT p, double slack)
    {
        if (Bounds.IsEmpty) return false;
        var big = Bounds;
        big.Inflate(slack, slack);
        return big.Contains(p.X, p.Y);
    }
}
