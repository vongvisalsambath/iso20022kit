namespace Iso20022Kit.Validation.Rules;

/// <summary>IBAN check-digit validation (ISO 13616, mod 97).</summary>
public static class Iban
{
    /// <summary>True when the IBAN's two check digits are correct. Ignores spaces; letters may be either case.</summary>
    public static bool HasValidCheckDigits(string iban)
    {
        var s = iban.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (s.Length < 5 || s.Length > 34) return false;
        // move the country code and check digits to the end, turn letters into numbers (A=10 … Z=35), then mod 97 must be 1
        var rearranged = s[4..] + s[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            int value;
            if (ch is >= '0' and <= '9') value = ch - '0';
            else if (ch is >= 'A' and <= 'Z') value = ch - 'A' + 10;
            else return false;
            remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }
}
