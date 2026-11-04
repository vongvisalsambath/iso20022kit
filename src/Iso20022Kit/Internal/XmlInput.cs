using System.Xml;
using System.Xml.Linq;

namespace Iso20022Kit.Internal;

/// <summary>Safe XML loading (no DTDs, no external entities, bounded size) and helpers for paths and line numbers.</summary>
internal static class XmlInput
{
    /// <summary>Largest input accepted, in characters (default 50 million, about 50 MB of ASCII).</summary>
    public static long MaxCharacters { get; set; } = 50_000_000;

    /// <summary>
    /// Deepest element nesting accepted (default 256; ISO 20022 messages are about 20 levels deep, envelopes add a few).
    /// .NET's tree builder slows down with the square of the depth, so without a limit a 1 MB file of nested tags
    /// could keep a CPU busy for minutes.
    /// </summary>
    public const int MaxDepth = 256;

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharacters,
        MaxCharactersFromEntities = 0,
        IgnoreProcessingInstructions = true,
        CheckCharacters = true,
    };

    /// <summary>Loads text that is already decoded (a string).</summary>
    public static XDocument Load(TextReader text)
    {
        using var reader = new DepthLimitedReader(XmlReader.Create(text, Settings()));
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    /// <summary>
    /// Loads bytes, letting the XML reader decode them: it honours a byte-order mark and the encoding in the XML
    /// declaration, and rejects bytes that aren't valid in that encoding instead of replacing them.
    /// </summary>
    public static XDocument Load(Stream bytes)
    {
        using var reader = new DepthLimitedReader(XmlReader.Create(bytes, Settings()));
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    /// <summary>
    /// The ISO 20022 Document elements: the root, or every descendant named Document in an ISO namespace (e.g. inside a
    /// business envelope with an AppHdr). At most two are returned: one is expected, two is an error.
    /// </summary>
    public static IReadOnlyList<XElement> FindDocuments(XDocument doc)
    {
        var root = doc.Root;
        if (root is null) return [];
        if (IsIsoDocument(root)) return [root];
        return root.Descendants().Where(IsIsoDocument).Take(2).ToList();
    }

    private static bool IsIsoDocument(XElement e) =>
        e.Name.LocalName == "Document" && e.Name.NamespaceName.StartsWith(MessageId.NamespacePrefix, StringComparison.Ordinal);

    // Position of each child among its same-named siblings, worked out once per parent: a statement can hold
    // tens of thousands of entries, and scanning them again for every issue made reports quadratic.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XElement, Dictionary<XElement, (int Index, int Count)>> SiblingIndex = new();

    private static (int Index, int Count) Siblings(XElement e)
    {
        var map = SiblingIndex.GetValue(e.Parent!, static parent =>
        {
            var result = new Dictionary<XElement, (int, int)>(ReferenceEqualityComparer.Instance);
            foreach (var group in parent.Elements().GroupBy(c => c.Name))
            {
                var n = 0;
                var total = group.Count();
                foreach (var c in group) result[c] = (++n, total);
            }
            return result;
        });
        return map.TryGetValue(e, out var v) ? v : (1, 1);
    }

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
                var (index, count) = Siblings(cur);
                if (count > 1) name += $"[{index}]";
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

    /// <summary>Passes everything through from the inner reader, and stops when elements nest deeper than <see cref="MaxDepth"/>.</summary>
    private sealed class DepthLimitedReader(XmlReader inner) : XmlReader, IXmlLineInfo, IXmlNamespaceResolver
    {
        public override bool Read()
        {
            var more = inner.Read();
            if (more && inner.NodeType == XmlNodeType.Element && inner.Depth >= MaxDepth)
            {
                var (line, col) = inner is IXmlLineInfo li ? (li.LineNumber, li.LinePosition) : (0, 0);
                throw new XmlException($"Elements are nested more than {MaxDepth} levels deep; ISO 20022 messages are far shallower, so the input was not read.", null, line, col);
            }
            return more;
        }

        public override int AttributeCount => inner.AttributeCount;
        public override string BaseURI => inner.BaseURI;
        public override int Depth => inner.Depth;
        public override bool EOF => inner.EOF;
        public override bool IsEmptyElement => inner.IsEmptyElement;
        public override string LocalName => inner.LocalName;
        public override string Name => inner.Name;
        public override string NamespaceURI => inner.NamespaceURI;
        public override XmlNameTable NameTable => inner.NameTable;
        public override XmlNodeType NodeType => inner.NodeType;
        public override string Prefix => inner.Prefix;
        public override ReadState ReadState => inner.ReadState;
        public override string Value => inner.Value;
        public override bool HasValue => inner.HasValue;
        public override bool IsDefault => inner.IsDefault;
        public override XmlReaderSettings? Settings => inner.Settings;
        public override XmlSpace XmlSpace => inner.XmlSpace;
        public override string XmlLang => inner.XmlLang;
        public override string GetAttribute(int i) => inner.GetAttribute(i);
        public override string? GetAttribute(string name) => inner.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
        public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
        public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);
        public override bool MoveToElement() => inner.MoveToElement();
        public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
        public override bool ReadAttributeValue() => inner.ReadAttributeValue();
        public override void ResolveEntity() => inner.ResolveEntity();
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }

        public bool HasLineInfo() => inner is IXmlLineInfo li && li.HasLineInfo();
        public int LineNumber => inner is IXmlLineInfo li ? li.LineNumber : 0;
        public int LinePosition => inner is IXmlLineInfo li ? li.LinePosition : 0;
        public IDictionary<string, string> GetNamespacesInScope(XmlNamespaceScope scope) =>
            inner is IXmlNamespaceResolver r ? r.GetNamespacesInScope(scope) : new Dictionary<string, string>();
        public string? LookupPrefix(string namespaceName) => inner is IXmlNamespaceResolver r ? r.LookupPrefix(namespaceName) : null;
    }
}
