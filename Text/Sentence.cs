using System.Linq;
using System.Text.RegularExpressions;

namespace LocalTranslate.Text;

/// <summary>Finds the sentence around a character index in a block of text.</summary>
public static class Sentence
{
    const string Terminators = ".!?…。！？؟";
    const string CjkTerminators = "。！？";
    const string Closers = "\"'”’)]»";
    public const int MaxLength = 2000;

    /// <summary>Returns [start, end) of the sentence containing index <paramref name="c"/>, or null.</summary>
    public static (int Start, int End)? Bounds(string s, int c)
    {
        if (s.Length == 0) return null;
        c = System.Math.Clamp(c, 0, s.Length - 1);

        int start = 0;
        for (int k = c - 1; k >= 0; k--)
        {
            if (IsBreak(s, k)) { start = k + 1; break; }
            int e = EndAt(s, k);
            if (e != -1 && e <= c) { start = e; break; }
        }

        int end = s.Length;
        for (int k = c; k < s.Length; k++)
        {
            if (IsBreak(s, k)) { end = k; break; }
            int e = EndAt(s, k);
            if (e != -1) { end = e; break; }
        }

        if (end - start > MaxLength)
        {
            start = System.Math.Max(start, c - MaxLength / 2);
            end = System.Math.Min(end, start + MaxLength);
        }
        while (start < end && char.IsWhiteSpace(s[start])) start++;
        while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
        return start < end ? (start, end) : null;
    }

    /// <summary>If s[k] ends a sentence, the index just past it (and trailing closers); otherwise -1.</summary>
    static int EndAt(string s, int k)
    {
        if (Terminators.IndexOf(s[k]) < 0) return -1;
        int e = k + 1;
        while (e < s.Length && (Terminators.IndexOf(s[e]) >= 0 || Closers.IndexOf(s[e]) >= 0)) e++;
        if (e >= s.Length || char.IsWhiteSpace(s[e]) || CjkTerminators.IndexOf(s[k]) >= 0) return e;
        return -1; // "3.14", "example.com"
    }

    /// <summary>
    /// Paragraph separators always break. A single '\n' is often just a visual line
    /// wrap (PDFs, some editors): treat it as a space when the text clearly continues
    /// ("...the\nnext word"), otherwise as a break.
    /// </summary>
    static bool IsBreak(string s, int k)
    {
        char ch = s[k];
        if (ch is '\r' or '\v' or '\f' or (char)0x2029) return true; // U+2029 paragraph separator
        if (ch != '\n') return false;

        int p = k - 1;
        while (p >= 0 && s[p] is ' ' or '\t') p--;
        int n = k + 1;
        while (n < s.Length && s[n] is ' ' or '\t') n++;
        if (p < 0 || n >= s.Length || s[p] is '\n' or '\r' || s[n] is '\n' or '\r') return true;

        bool continues = char.IsLower(s[n]) && (char.IsLetterOrDigit(s[p]) || s[p] is ',' or '-' or ';' or ':');
        return !continues;
    }

    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string text) => Spaces.Replace(text, " ").Trim();

    public static bool HasLetters(string text) => text.Any(char.IsLetter);
}
