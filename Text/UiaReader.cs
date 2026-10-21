using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Interop.UIAutomationClient;

namespace LocalTranslate.Text;

/// <summary>
/// Reads text through Windows UI Automation — the accessibility API that Word,
/// Chrome/Edge, Notepad, Outlook, most WPF/WinForms/UWP apps expose.
/// Must be used from a single MTA thread (see <see cref="TextLocator"/>).
/// </summary>
internal sealed class UiaReader
{
    public enum Outcome
    {
        Found,
        NoTextHere,   // the app exposes text, but the cursor isn't on any (margin, image…)
        Unsupported,  // the app exposes nothing useful
        OwnWindow     // one of our own bubbles: never read it
    }

    const int TextPatternId = 10014;
    const int MaxParagraph = 20000;

    // Controls whose Name is their visible label.
    static readonly HashSet<int> LabelTypes = new()
    {
        50000, // Button
        50002, // CheckBox
        50005, // Hyperlink
        50007, // ListItem
        50011, // MenuItem
        50013, // RadioButton
        50019, // TabItem
        50020, // Text
        50024, // TreeItem
        50029, // DataItem
        50031, // SplitButton
        50035, // HeaderItem
    };

    readonly IUIAutomation uia;
    readonly IUIAutomationTreeWalker walker;
    readonly int ownPid = Environment.ProcessId;

    public UiaReader()
    {
        var automation = new CUIAutomation8();
        // A hung app must not freeze us: give up on slow providers quickly.
        if (automation is IUIAutomation2 a2)
        {
            a2.ConnectionTimeout = 1000;
            a2.TransactionTimeout = 2000;
        }
        uia = automation;
        walker = uia.RawViewWalker;
    }

    // ---------------- Hover: sentence under a point ----------------

    public (Outcome, TextHit?) SentenceAt(Native.POINT p)
    {
        var pt = new tagPOINT { x = p.X, y = p.Y };
        IUIAutomationElement? element;
        try { element = uia.ElementFromPoint(pt); }
        catch (COMException) { return (Outcome.Unsupported, null); }
        if (element == null) return (Outcome.Unsupported, null);
        if (Safe(() => element.CurrentProcessId) == ownPid) return (Outcome.OwnWindow, null);
        // An image's "text" is its alt text or a placeholder like "Image": read the pixels instead.
        if (Safe(() => element.CurrentControlType) == ImageType) return (Outcome.Unsupported, null);

        var pattern = FindTextPattern(element);
        if (pattern != null)
        {
            var (outcome, hit) = FromTextPattern(pattern, pt);
            if (outcome != Outcome.Unsupported) return (outcome, hit);
        }

        var label = FromLabel(element);
        if (label != null) return (Outcome.Found, label);

        return (Outcome.Unsupported, null);
    }

    (Outcome, TextHit?) FromTextPattern(IUIAutomationTextPattern pattern, tagPOINT pt)
    {
        try
        {
            var caret = pattern.RangeFromPoint(pt);
            if (caret == null) return (Outcome.Unsupported, null);

            // RangeFromPoint snaps to the nearest text even over empty space:
            // only continue if the word there is really under the cursor.
            var word = caret.Clone();
            word.ExpandToEnclosingUnit(TextUnit.TextUnit_Word);
            var wordRects = ToRects(word.GetBoundingRectangles());
            if (wordRects.Count == 0) return (Outcome.Unsupported, null);
            if (!AnyContains(wordRects, pt, 2)) return (Outcome.NoTextHere, null);
            if (IsImage(word)) return (Outcome.Unsupported, null); // picture embedded in the text

            var block = caret.Clone();
            block.ExpandToEnclosingUnit(TextUnit.TextUnit_Paragraph);
            var text = block.GetText(MaxParagraph);
            if (text.Length >= MaxParagraph)
            {
                // Provider has no paragraphs (falls back to the whole document): use the line.
                block = caret.Clone();
                block.ExpandToEnclosingUnit(TextUnit.TextUnit_Line);
                text = block.GetText(MaxParagraph);
            }
            if (string.IsNullOrWhiteSpace(text)) return (Outcome.NoTextHere, null);

            // Character offset of the hovered word inside the block.
            var before = block.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, word, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            int offset = before.GetText(MaxParagraph).Length;

            var bounds = Sentence.Bounds(text, offset);
            if (bounds == null) return (Outcome.NoTextHere, null);
            var (start, end) = bounds.Value;
            var sentence = Sentence.Clean(text[start..end]);
            if (!Sentence.HasLetters(sentence)) return (Outcome.NoTextHere, null);

            // Range of just the sentence, for the highlight and the bubble position.
            var range = block.Clone();
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, start);
            range.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, end - start);
            var rects = ToRects(range.GetBoundingRectangles());
            if (rects.Count == 0 || !AnyContains(rects, pt, 24)) rects = wordRects; // character counts drifted

            return (Outcome.Found, new TextHit(sentence, rects, "uia"));
        }
        catch (COMException) { return (Outcome.Unsupported, null); }
        catch (InvalidCastException) { return (Outcome.Unsupported, null); }
    }

    TextHit? FromLabel(IUIAutomationElement element)
    {
        try
        {
            if (!LabelTypes.Contains(element.CurrentControlType)) return null;
            var name = Sentence.Clean(element.CurrentName ?? "");
            if (name.Length is 0 or > 1000 || !Sentence.HasLetters(name)) return null;
            var r = element.CurrentBoundingRectangle;
            return new TextHit(name, new[] { new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top) }, "uia-label");
        }
        catch (COMException) { return null; }
    }

    // ---------------- Selection ----------------

    public TextHit? Selection(Native.POINT p)
    {
        var candidates = new List<IUIAutomationElement>();
        try { var f = uia.GetFocusedElement(); if (f != null) candidates.Add(f); } catch (COMException) { }
        try { var e = uia.ElementFromPoint(new tagPOINT { x = p.X, y = p.Y }); if (e != null) candidates.Add(e); } catch (COMException) { }

        foreach (var element in candidates)
        {
            if (Safe(() => element.CurrentProcessId) == ownPid) continue;
            var pattern = FindTextPattern(element);
            if (pattern == null) continue;
            try
            {
                var ranges = pattern.GetSelection();
                if (ranges == null) continue;
                var text = new StringBuilder();
                var rects = new List<Rect>();
                for (int i = 0; i < ranges.Length; i++)
                {
                    var r = ranges.GetElement(i);
                    text.Append(r.GetText(10000)).Append(' ');
                    rects.AddRange(ToRects(r.GetBoundingRectangles()));
                }
                var clean = Sentence.Clean(text.ToString());
                if (clean.Length > 0) return new TextHit(clean, rects, "uia");
            }
            catch (COMException) { }
        }
        return null;
    }

    // ---------------- Helpers ----------------

    const int ImageType = 50006;

    static bool IsImage(IUIAutomationTextRange range)
    {
        try
        {
            if (range.GetEnclosingElement()?.CurrentControlType == ImageType) return true;
            var children = range.GetChildren();
            for (int i = 0; children != null && i < children.Length; i++)
                if (children.GetElement(i).CurrentControlType == ImageType) return true;
        }
        catch (COMException) { }
        return false;
    }

    IUIAutomationTextPattern? FindTextPattern(IUIAutomationElement element)
    {
        // The leaf under the cursor rarely has the pattern itself; the document/edit
        // that owns it is a few levels up (e.g. Chrome: text → paragraph → document).
        var current = element;
        for (int depth = 0; current != null && depth < 12; depth++)
        {
            try
            {
                if (current.GetCurrentPattern(TextPatternId) is IUIAutomationTextPattern tp) return tp;
                current = walker.GetParentElement(current);
            }
            catch (COMException) { return null; }
        }
        return null;
    }

    static List<Rect> ToRects(double[]? quads)
    {
        var list = new List<Rect>();
        if (quads == null) return list;
        for (int i = 0; i + 3 < quads.Length; i += 4)
            if (quads[i + 2] > 0 && quads[i + 3] > 0)
                list.Add(new Rect(quads[i], quads[i + 1], quads[i + 2], quads[i + 3]));
        return list;
    }

    static bool AnyContains(List<Rect> rects, tagPOINT pt, double slack)
    {
        foreach (var r in rects)
        {
            var big = r;
            big.Inflate(slack, slack);
            if (big.Contains(pt.x, pt.y)) return true;
        }
        return false;
    }

    static int Safe(Func<int> f)
    {
        try { return f(); } catch (COMException) { return -1; }
    }
}
