using System.Globalization;
using System.Text;

namespace Iso20022Kit.Mt;

/// <summary>
/// The SWIFT "X" character set that MT messages use: a–z, A–Z, 0–9, space and / - ? : ( ) . , ' +
/// (and CrLf between lines).
/// </summary>
public static class XCharset
{
    /// <summary>True if <paramref name="c"/> may appear in an MT field value.</summary>
    public static bool IsX(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '/' or '-' or '?' or ':' or '(' or ')' or '.' or ',' or '\'' or '+';

    /// <summary>True if every character of <paramref name="s"/> is in the X set.</summary>
    public static bool IsX(string s) => s.All(IsX);

    /// <summary>
    /// Converts text to the X set: accents are removed (an accented e becomes e), a few characters get a usual substitute (&amp; → +,
    /// @ → (at), _ → -), line breaks and tabs become spaces, and anything else becomes a full stop.
    /// <paramref name="replaced"/> counts the characters that were changed into something else.
    /// </summary>
    public static string Transliterate(string s, out int replaced)
    {
        replaced = 0;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue; // the accent of a decomposed letter
            if (IsX(ch)) { sb.Append(ch); continue; }
            replaced++;
            sb.Append(ch switch
            {
                '&' => "+",
                '@' => "(at)",
                '_' => "-",
                '"' or '`' or '\u2018' or '\u2019' => "'",
                '\r' or '\n' or '\t' or '\u2028' or '\u2029' or '\u00A0' => " ",
                '\u00DF' => "ss",
                '\u00C6' => "AE", '\u00E6' => "ae", '\u00D8' => "O", '\u00F8' => "o", '\u0152' => "OE", '\u0153' => "oe", '\u0110' => "D", '\u0111' => "d", '\u0141' => "L", '\u0142' => "l",
                _ => ".",
            });
        }
        return sb.ToString();
    }
}
