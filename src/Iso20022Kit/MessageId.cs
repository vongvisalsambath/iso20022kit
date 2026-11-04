using System.Text.RegularExpressions;

namespace Iso20022Kit;

/// <summary>
/// An ISO 20022 message identifier such as <c>pacs.008.001.08</c>:
/// business area, message number, variant and version.
/// </summary>
public readonly partial record struct MessageId(string Area, string Number, string Variant, string Version)
{
    /// <summary>The ISO 20022 XML namespace prefix used by every message.</summary>
    public const string NamespacePrefix = "urn:iso:std:iso:20022:tech:xsd:";

    /// <summary>pacs.008.001.08, FI to FI customer credit transfer.</summary>
    public static readonly MessageId Pacs008V08 = Parse("pacs.008.001.08");

    /// <summary>camt.053.001.08, bank to customer statement.</summary>
    public static readonly MessageId Camt053V08 = Parse("camt.053.001.08");

    /// <summary>The XML namespace of this message, e.g. <c>urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08</c>.</summary>
    public string Namespace => NamespacePrefix + ToString();

    /// <summary>The message without its version, e.g. <c>pacs.008</c>.</summary>
    public string Family => $"{Area}.{Number}";

    /// <inheritdoc />
    public override string ToString() => $"{Area}.{Number}.{Variant}.{Version}";

    /// <summary>Parses <c>pacs.008.001.08</c> (or a full namespace). Throws <see cref="FormatException"/> when it isn't one.</summary>
    public static MessageId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"\"{text}\" is not an ISO 20022 message identifier such as pacs.008.001.08.");

    /// <summary>Parses <c>pacs.008.001.08</c> or a full namespace such as <c>urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08</c>.</summary>
    public static bool TryParse(string? text, out MessageId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith(NamespacePrefix, StringComparison.Ordinal)) s = s[NamespacePrefix.Length..];
        var m = IdPattern().Match(s);
        if (!m.Success) return false;
        id = new MessageId(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        return true;
    }

    [GeneratedRegex(@"^([a-z]{4})\.(\d{3})\.(\d{3})\.(\d{2})$")]
    private static partial Regex IdPattern();
}
