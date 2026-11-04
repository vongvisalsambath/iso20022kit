using System.Xml;
using System.Xml.Linq;

namespace Iso20022Kit.Internal;

/// <summary>Safe XML loading (no DTDs, no external entities, bounded size) and helpers for paths and line numbers.</summary>
internal static class XmlInput
{
    /// <summary>Largest input accepted, in characters (default 50 million, about 50 MB of ASCII).</summary>
    public static long MaxCharacters { get; set; } = 50_000_000;

    public static XDocument Load(TextReader text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(text, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    /// <summary>The ISO 20022 Document element: the root, or the first descendant named Document in an ISO namespace (e.g. inside a business envelope with an AppHdr).</summary>
    public static XElement? FindDocument(XDocument doc)
    {
        var root = doc.Root;
        if (root is null) return null;
        if (IsIsoDocument(root)) return root;
        return root.Descendants().FirstOrDefault(IsIsoDocument);
    }

    private static bool IsIsoDocument(XElement e) =>
        e.Name.LocalName == "Document" && e.Name.NamespaceName.StartsWith(MessageId.NamespacePrefix, StringComparison.Ordinal);

    /// <summary>An XPath-like path from the Document element: /Document/A/B[2]/C, with an index only where a name repeats.</summary>
    public static string PathOf(XObject node)
    {
        if (node is XAttribute a) return a.Parent is null ? "@" + a.Name.LocalName : PathOf(a.Parent) + "/@" + a.Name.LocalName;
        if (node is not XElement e) return node.Parent is null ? "" : PathOf(node.Parent);
        var parts = new List<string>();
        for (var cur = e; cur is not null; cur = cur.Parent)
        {
            var name = cur.Name.LocalName;
            if (cur.Parent is not null)
            {
                var same = cur.Parent.Elements(cur.Name).ToList();
                if (same.Count > 1) name += $"[{same.IndexOf(cur) + 1}]";
            }
            parts.Add(name);
            if (cur.Name.LocalName == "Document" && cur.Name.NamespaceName.StartsWith(MessageId.NamespacePrefix, StringComparison.Ordinal)) break;
        }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    public static (int? Line, int? Column) Position(XObject node)
    {
        IXmlLineInfo info = node;
        if (!info.HasLineInfo()) return (null, null);
        // .NET points at an element's name; report the "<" before it, as editors and the browser viewer do.
        return (info.LineNumber, node is XElement ? Math.Max(1, info.LinePosition - 1) : info.LinePosition);
    }
}
