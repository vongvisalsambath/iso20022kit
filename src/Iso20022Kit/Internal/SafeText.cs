using System.Globalization;
using System.Text;

namespace Iso20022Kit.Internal;

/// <summary>
/// Makes text taken from a message safe to show: invisible and direction-changing characters become visible
/// <c>\u202E</c>-style escapes, and very long text is shortened in the middle. Issue messages and paths go through this,
/// since they quote the message (a 2 MB value would otherwise be repeated in full, and a right-to-left override
/// could make a name or amount display differently from what it is).
/// </summary>
internal static class SafeText
{
    public const int MaxLength = 600;

    /// <summary>Characters that can't be seen, or that change how the text around them is displayed.</summary>
    public static bool IsHidden(int c) =>
        c is < 0x20 and not ('\t' or '\n' or '\r')
        || c is >= 0x7F and <= 0x9F              // DEL and C1 controls (U+009B is a terminal escape in some terminals)
        || c is 0xAD or 0x061C or 0x180E         // soft hyphen, Arabic letter mark, Mongolian vowel separator
        || c is >= 0x200B and <= 0x200F          // zero-width space/joiners, LTR/RTL marks
        || c is >= 0x2028 and <= 0x202E          // line/paragraph separators, bidi embeddings and overrides
        || c is >= 0x2060 and <= 0x2064          // word joiner, invisible operators
        || c is >= 0x2066 and <= 0x2069          // bidi isolates
        || c is 0xFEFF                           // zero-width no-break space
        || c is >= 0xFFF9 and <= 0xFFFB;         // interlinear annotation

    /// <summary>A short name for the hidden characters worth naming.</summary>
    public static string Describe(int c) => c switch
    {
        0x202E => "RIGHT-TO-LEFT OVERRIDE",
        0x202D => "LEFT-TO-RIGHT OVERRIDE",
        0x202B => "RIGHT-TO-LEFT EMBEDDING",
        0x202A => "LEFT-TO-RIGHT EMBEDDING",
        0x202C => "POP DIRECTIONAL FORMATTING",
        0x2066 => "LEFT-TO-RIGHT ISOLATE",
        0x2067 => "RIGHT-TO-LEFT ISOLATE",
        0x2068 => "FIRST STRONG ISOLATE",
        0x2069 => "POP DIRECTIONAL ISOLATE",
        0x200E => "LEFT-TO-RIGHT MARK",
        0x200F => "RIGHT-TO-LEFT MARK",
        0x200B => "ZERO WIDTH SPACE",
        0x200C => "ZERO WIDTH NON-JOINER",
        0x200D => "ZERO WIDTH JOINER",
        0x2060 => "WORD JOINER",
        0xFEFF => "ZERO WIDTH NO-BREAK SPACE",
        0xAD => "SOFT HYPHEN",
        _ when c is >= 0x80 and <= 0x9F => "C1 CONTROL",
        _ => "INVISIBLE CHARACTER",
    };

    public static string Escape(int c) => c <= 0xFFFF ? $"\\u{c:X4}" : $"\\U{c:X8}";

    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var needsWork = text.Length > MaxLength;
        if (!needsWork)
            foreach (var ch in text)
                if (IsHidden(ch) || char.IsSurrogate(ch)) { needsWork = true; break; }
        if (!needsWork) return text;

        var sb = new StringBuilder(Math.Min(text.Length, MaxLength + 64));
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(ch).Append(text[++i]); continue; }
            if (char.IsSurrogate(ch)) sb.Append(Escape(ch));          // a lone surrogate
            else if (IsHidden(ch)) sb.Append(Escape(ch));
            else sb.Append(ch);
        }
        var s = sb.ToString();
        if (s.Length <= MaxLength) return s;
        // keep the start and the end, which hold the element name and the reason
        var head = SafeCut(s, 0, MaxLength * 2 / 3);
        var tail = SafeCut(s, s.Length - MaxLength / 4, MaxLength / 4);
        return $"{head}…({(s.Length - head.Length - tail.Length).ToString("N0", CultureInfo.InvariantCulture)} characters omitted)…{tail}";
    }

    private static string SafeCut(string s, int start, int length)
    {
        if (start > 0 && char.IsLowSurrogate(s[start])) { start++; length--; }
        var end = Math.Min(s.Length, start + length);
        if (end < s.Length && end > start && char.IsHighSurrogate(s[end - 1])) end--;
        return s[start..end];
    }
}
