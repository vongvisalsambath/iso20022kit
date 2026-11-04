using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Iso20022Kit.Internal;
using Iso20022Kit.Validation.Rules;

namespace Iso20022Kit.Validation;

/// <summary>A parsed input: the whole XML, the ISO Document element in it, and what message it is.</summary>
internal sealed record ParsedInput(XDocument? Xml, XElement? Document, MessageId? Id, List<Issue> Issues);

internal static partial class Validator
{
    public static ParsedInput Parse(TextReader input)
    {
        var issues = new List<Issue>();
        XDocument xml;
        try
        {
            xml = XmlInput.Load(input);
        }
        catch (XmlException e)
        {
            var dtd = e.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase);
            issues.Add(new Issue(Severity.Error, dtd ? "XML-DTD" : "XML-MALFORMED",
                dtd ? "DTDs are not allowed in ISO 20022 messages (and are a security risk), so the message was not read." : e.Message,
                null, e.LineNumber > 0 ? e.LineNumber : null, e.LinePosition > 0 ? e.LinePosition : null));
            return new ParsedInput(null, null, null, issues);
        }

        var document = XmlInput.FindDocument(xml);
        if (document is null)
        {
            var root = xml.Root!;
            var (line, col) = XmlInput.Position(root);
            issues.Add(new Issue(Severity.Error, "MSG-NO-DOCUMENT",
                $"No ISO 20022 Document element found. The root element is <{root.Name.LocalName}> in namespace \"{root.Name.NamespaceName}\"; " +
                $"expected <Document xmlns=\"{MessageId.NamespacePrefix}…\">.", "/" + root.Name.LocalName, line, col));
            return new ParsedInput(xml, null, null, issues);
        }

        if (!MessageId.TryParse(document.Name.NamespaceName, out var id))
        {
            var (line, col) = XmlInput.Position(document);
            issues.Add(new Issue(Severity.Error, "MSG-UNKNOWN", $"\"{document.Name.NamespaceName}\" is not a recognisable ISO 20022 message namespace.", "/Document", line, col));
            return new ParsedInput(xml, document, null, issues);
        }

        if (!Catalog.IsSupported(id))
        {
            var (line, col) = XmlInput.Position(document);
            var supported = string.Join(", ", Catalog.Supported);
            var sameFamily = Catalog.Supported.Where(s => s.Family == id.Family).ToList();
            var hint = sameFamily.Count > 0 ? $" This library supports {string.Join(", ", sameFamily)} of {id.Family}." : "";
            issues.Add(new Issue(Severity.Error, "MSG-UNSUPPORTED", $"{id} is not supported yet. Supported messages: {supported}.{hint}", "/Document", line, col));
        }

        return new ParsedInput(xml, document, id, issues);
    }

    public static ValidationReport Validate(TextReader input, ValidationOptions options)
    {
        var parsed = Parse(input);
        if (parsed.Issues.Count > 0 || parsed.Document is null || parsed.Id is not MessageId id)
            return new ValidationReport(parsed.Id, parsed.Issues);
        return Validate(parsed.Document, id, options);
    }

    public static ValidationReport Validate(XElement document, MessageId id, ValidationOptions options)
    {
        var issues = new List<Issue>();
        ValidateSchema(document, id, issues);
        if (issues.Count == 0 || options.AlwaysRunRules)
        {
            var context = new RuleContext(document, id, issues, options);
            foreach (var rule in RuleSet.For(id))
            {
                if (options.DisabledRules.Contains(rule.Code)) continue;
                rule.Check(context);
            }
        }
        var ordered = issues
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.Line ?? int.MaxValue)
            .ThenBy(i => i.Column ?? int.MaxValue)
            .ToList();
        return new ValidationReport(id, ordered);
    }

    private static void ValidateSchema(XElement document, MessageId id, List<Issue> issues)
    {
        var schemas = Catalog.Schemas(id);
        XDocument target;
        Dictionary<XObject, XObject>? map = null;
        if (document.Parent is null && document.Document is not null)
        {
            target = document.Document;
        }
        else
        {
            // Inside an envelope: validate a copy of the Document element and map errors back to the original nodes.
            var copy = new XElement(document);
            target = new XDocument(copy);
            map = new Dictionary<XObject, XObject>(ReferenceEqualityComparer.Instance);
            using var a = document.DescendantsAndSelf().GetEnumerator();
            using var b = copy.DescendantsAndSelf().GetEnumerator();
            while (a.MoveNext() && b.MoveNext())
            {
                map[b.Current] = a.Current;
                foreach (var (ca, cb) in a.Current.Attributes().Zip(b.Current.Attributes())) map[cb] = ca;
            }
        }

        target.Validate(schemas, (sender, e) =>
        {
            var node = sender as XObject;
            if (node is not null && map is not null && map.TryGetValue(node, out var original)) node = original;
            var (line, col) = node is null ? (null, null) : XmlInput.Position(node);
            issues.Add(new Issue(e.Severity == XmlSeverityType.Error ? Severity.Error : Severity.Warning, "SCHEMA",
                Tidy(e.Message), node is null ? null : XmlInput.PathOf(node), line, col));
        });
    }

    /// <summary>.NET's schema messages repeat the long ISO namespace everywhere; drop it so the message is readable.</summary>
    private static string Tidy(string message) =>
        QualifiedName().Replace(NamespaceNoise().Replace(message, ""), "'").Replace("  ", " ").Trim();

    [GeneratedRegex(@" in namespace 'urn:iso:std:iso:20022:tech:xsd:[a-z]{4}\.\d{3}\.\d{3}\.\d{2}'")]
    private static partial Regex NamespaceNoise();

    [GeneratedRegex(@"'urn:iso:std:iso:20022:tech:xsd:[a-z]{4}\.\d{3}\.\d{3}\.\d{2}:")]
    private static partial Regex QualifiedName();
}

/// <summary>Options for validation.</summary>
public sealed class ValidationOptions
{
    /// <summary>The defaults: every rule on, business rules only when the schema check passes.</summary>
    public static ValidationOptions Default { get; } = new();

    /// <summary>Rule codes to skip, e.g. <c>CURRENCY-UNKNOWN</c>.</summary>
    public ISet<string> DisabledRules { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Run business rules even when the schema check fails (they may then report knock-on problems). Default false.</summary>
    public bool AlwaysRunRules { get; init; }
}
