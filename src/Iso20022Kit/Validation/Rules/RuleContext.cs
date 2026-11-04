using System.Globalization;
using System.Xml.Linq;
using Iso20022Kit.Internal;

namespace Iso20022Kit.Validation.Rules;

/// <summary>A business rule: something the schema can't express, such as "the totals add up".</summary>
internal interface IRule
{
    /// <summary>Stable code, documented in docs/rules.md and used by the conformance suite.</summary>
    string Code { get; }
    void Check(RuleContext context);
}

internal sealed class RuleContext(XElement document, MessageId id, List<Issue> issues, ValidationOptions options)
{
    public XElement Document { get; } = document;
    public MessageId Id { get; } = id;
    public ValidationOptions Options { get; } = options;
    public XNamespace Ns { get; } = document.Name.Namespace;

    public void Report(Severity severity, string code, string message, XObject at)
    {
        var (line, col) = XmlInput.Position(at);
        issues.Add(new Issue(severity, code, message, XmlInput.PathOf(at), line, col));
    }

    public void Error(string code, string message, XObject at) => Report(Severity.Error, code, message, at);
    public void Warn(string code, string message, XObject at) => Report(Severity.Warning, code, message, at);

    // ---- small helpers for reading the XML by ISO short names ----

    public XElement? El(XElement? parent, params string[] path)
    {
        var cur = parent;
        foreach (var name in path)
        {
            if (cur is null) return null;
            cur = cur.Element(Ns + name);
        }
        return cur;
    }

    public IEnumerable<XElement> Els(XElement? parent, string name) => parent?.Elements(Ns + name) ?? [];

    public string? Text(XElement? parent, params string[] path) => El(parent, path)?.Value.Trim();

    public static decimal? Decimal(XElement? e) =>
        e is not null && decimal.TryParse(e.Value.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static string Amount(decimal value) => value.ToString("0.00########", CultureInfo.InvariantCulture);
}
