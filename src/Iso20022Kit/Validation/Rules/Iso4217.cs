namespace Iso20022Kit.Validation.Rules;

/// <summary>
/// ISO 4217 active currency codes and their minor units (decimals), as published by the ISO 4217 maintenance agency (list as of 2025).
/// Codes whose minor unit is "N.A." (precious metals, testing and fund codes such as XAU, XTS, XDR) are not listed.
/// </summary>
public static class Iso4217
{
    private static readonly string[] Zero = ["BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF"];
    private static readonly string[] Three = ["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"];
    private static readonly string[] Four = ["CLF", "UYW"];
    private static readonly string[] Two =
    [
        "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "AUD", "AWG", "AZN", "BAM", "BBD", "BDT", "BGN", "BMD", "BND", "BOB", "BOV", "BRL", "BSD",
        "BTN", "BWP", "BYN", "BZD", "CAD", "CDF", "CHE", "CHF", "CHW", "CNY", "COP", "COU", "CRC", "CUP", "CVE", "CZK", "DKK", "DOP", "DZD", "EGP",
        "ERN", "ETB", "EUR", "FJD", "FKP", "GBP", "GEL", "GHS", "GIP", "GMD", "GTQ", "GYD", "HKD", "HNL", "HTG", "HUF", "IDR", "ILS", "INR", "IRR",
        "JMD", "KES", "KGS", "KHR", "KPW", "KYD", "KZT", "LAK", "LBP", "LKR", "LRD", "LSL", "MAD", "MDL", "MGA", "MKD", "MMK", "MNT", "MOP", "MRU",
        "MUR", "MVR", "MWK", "MXN", "MXV", "MYR", "MZN", "NAD", "NGN", "NIO", "NOK", "NPR", "NZD", "PAB", "PEN", "PGK", "PHP", "PKR", "PLN", "QAR",
        "RON", "RSD", "RUB", "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD", "SSP", "STN", "SVC", "SYP", "SZL", "THB", "TJS",
        "TMT", "TOP", "TRY", "TTD", "TWD", "TZS", "UAH", "USD", "USN", "UYU", "UZS", "VED", "VES", "WST", "XCD", "XCG", "YER", "ZAR", "ZMW", "ZWG",
    ];

    private static readonly Dictionary<string, int> MinorUnits = Build();

    private static Dictionary<string, int> Build()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in Two) d[c] = 2;
        foreach (var c in Zero) d[c] = 0;
        foreach (var c in Three) d[c] = 3;
        foreach (var c in Four) d[c] = 4;
        return d;
    }

    /// <summary>All codes in the table.</summary>
    public static IReadOnlyCollection<string> Codes => MinorUnits.Keys;

    /// <summary>How many decimals an amount in this currency may have, or null when the code isn't an active ISO 4217 code with minor units.</summary>
    public static int? Decimals(string code) => MinorUnits.TryGetValue(code, out var d) ? d : null;
}
