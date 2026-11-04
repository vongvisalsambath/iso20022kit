using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace Iso20022Kit.Internal;

/// <summary>The messages this library supports: their schema and generated root type.</summary>
internal static class Catalog
{
    private sealed record Entry(MessageId Id, Type DocumentType);

    private static readonly Entry[] Entries =
    [
        new(MessageId.Pacs008V08, typeof(Messages.Pacs008V08.Document)),
        new(MessageId.Camt053V08, typeof(Messages.Camt053V08.Document)),
    ];

    private static readonly ConcurrentDictionary<MessageId, XmlSchemaSet> SchemaSets = new();
    private static readonly ConcurrentDictionary<Type, XmlSerializer> Serializers = new();

    public static IReadOnlyList<MessageId> Supported { get; } = Entries.Select(e => e.Id).ToArray();

    public static bool IsSupported(MessageId id) => Entries.Any(e => e.Id == id);

    public static Type? DocumentType(MessageId id) => Entries.FirstOrDefault(e => e.Id == id)?.DocumentType;

    public static MessageId? IdOf(Type documentType) => Entries.FirstOrDefault(e => e.DocumentType == documentType)?.Id;

    /// <summary>The compiled official schema for a message (loaded once, then shared; XmlSchemaSet is safe for concurrent validation once compiled).</summary>
    public static XmlSchemaSet Schemas(MessageId id) => SchemaSets.GetOrAdd(id, static key =>
    {
        var name = $"Iso20022Kit.Schemas.{key}.xsd";
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded schema {name} is missing.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var set = new XmlSchemaSet { XmlResolver = null };
        set.Add(XmlSchema.Read(reader, null)!);
        set.Compile();
        return set;
    });

    public static XmlSerializer Serializer(Type documentType) => Serializers.GetOrAdd(documentType, static t => new XmlSerializer(t));
}
