using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Iso20022Kit.Internal;
using Iso20022Kit.Model;
using Iso20022Kit.Validation;

namespace Iso20022Kit;

/// <summary>
/// Read, validate and write ISO 20022 messages.
/// </summary>
/// <example>
/// <code>
/// var result = Iso20022.ReadCreditTransfer(xml);
/// if (!result.Report.IsValid) foreach (var e in result.Report.Errors) Console.WriteLine(e);
/// foreach (var tx in result.Value!.Transactions) Console.WriteLine($"{tx.Debtor.Name} pays {tx.Amount} to {tx.Creditor.Name}");
/// </code>
/// </example>
public static class Iso20022
{
    /// <summary>The messages this version supports.</summary>
    public static IReadOnlyList<MessageId> SupportedMessages => Catalog.Supported;

    /// <summary>Every validation code with a one-line description.</summary>
    public static IReadOnlyDictionary<string, string> RuleDescriptions => Validation.Rules.RuleSet.Descriptions;

    /// <summary>What message this is (from its namespace), or null when it isn't ISO 20022 or can't be parsed. Works for unsupported messages too.</summary>
    public static MessageId? Detect(string xml)
    {
        var parsed = Validator.Parse(new StringReader(xml));
        return parsed.Id;
    }

    /// <summary>Checks a message against its official schema, then the business rules (see docs/rules.md).</summary>
    public static ValidationReport Validate(string xml, ValidationOptions? options = null) =>
        Validator.Validate(new StringReader(xml), options ?? ValidationOptions.Default);

    /// <summary>Checks a message read from a stream (UTF-8 unless the XML declaration says otherwise).</summary>
    public static ValidationReport Validate(Stream xml, ValidationOptions? options = null)
    {
        using var reader = new StreamReader(xml, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Validator.Validate(reader, options ?? ValidationOptions.Default);
    }

    /// <summary>Reads a pacs.008 customer credit transfer. <see cref="ReadResult{T}.Value"/> is set when the message matches the schema, even if a business rule fails.</summary>
    public static ReadResult<CreditTransfer> ReadCreditTransfer(string xml, ValidationOptions? options = null) =>
        ReadModel(xml, "pacs.008", options, (m, d) => m.CreditTransfer(d));

    /// <summary>Reads a camt.053 bank-to-customer statement. <see cref="ReadResult{T}.Value"/> is set when the message matches the schema, even if a business rule fails.</summary>
    public static ReadResult<BankStatement> ReadBankStatement(string xml, ValidationOptions? options = null) =>
        ReadModel(xml, "camt.053", options, (m, d) => m.BankStatement(d));

    /// <summary>
    /// Reads a message into its generated type, with every field of the schema, e.g.
    /// <c>ReadDocument&lt;Iso20022Kit.Messages.Pacs008V08.Document&gt;(xml)</c>.
    /// </summary>
    public static ReadResult<T> ReadDocument<T>(string xml, ValidationOptions? options = null) where T : class
    {
        var expected = Catalog.IdOf(typeof(T)) ?? throw new ArgumentException($"{typeof(T).FullName} is not a supported ISO 20022 document type.", nameof(T));
        var parsed = Validator.Parse(new StringReader(xml));
        if (parsed.Issues.Count > 0 || parsed.Document is null || parsed.Id is not MessageId id) return new ReadResult<T>(new ValidationReport(parsed.Id, parsed.Issues), null);
        if (id != expected) return WrongType<T>(id, expected.ToString(), parsed.Document);
        var report = Validator.Validate(parsed.Document, id, options ?? ValidationOptions.Default);
        if (report.Issues.Any(i => i.Code == "SCHEMA" && i.Severity == Severity.Error)) return new ReadResult<T>(report, null);
        using var reader = parsed.Document.CreateReader();
        var value = (T?)Catalog.Serializer(typeof(T)).Deserialize(reader);
        return new ReadResult<T>(report, value);
    }

    /// <summary>
    /// Writes a generated document (e.g. <c>Iso20022Kit.Messages.Pacs008V08.Document</c>) as XML, then validates it.
    /// Throws <see cref="Iso20022ValidationException"/> when the result isn't valid, unless <paramref name="validate"/> is false.
    /// </summary>
    public static string Write(object document, bool validate = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        var id = Catalog.IdOf(document.GetType()) ?? throw new ArgumentException($"{document.GetType().FullName} is not a supported ISO 20022 document type.", nameof(document));
        var ns = new XmlSerializerNamespaces();
        ns.Add("", id.Namespace);
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(new Utf8StringWriter(sb), new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) }))
            Catalog.Serializer(document.GetType()).Serialize(writer, document, ns);
        var xml = sb.ToString();
        if (validate)
        {
            var report = Validate(xml);
            if (!report.IsValid) throw new Iso20022ValidationException(report);
        }
        return xml;
    }

    private static ReadResult<T> ReadModel<T>(string xml, string family, ValidationOptions? options, Func<Mapper, XElement, T> map) where T : class
    {
        var parsed = Validator.Parse(new StringReader(xml));
        if (parsed.Document is null || parsed.Id is not MessageId id) return new ReadResult<T>(new ValidationReport(parsed.Id, parsed.Issues), null);
        if (id.Family != family) return WrongType<T>(id, family, parsed.Document);
        if (parsed.Issues.Count > 0) return new ReadResult<T>(new ValidationReport(id, parsed.Issues), null);
        var report = Validator.Validate(parsed.Document, id, options ?? ValidationOptions.Default);
        var schemaOk = !report.Issues.Any(i => i.Code == "SCHEMA" && i.Severity == Severity.Error);
        return new ReadResult<T>(report, schemaOk ? map(new Mapper(parsed.Document.Name.Namespace), parsed.Document) : null);
    }

    private static ReadResult<T> WrongType<T>(MessageId actual, string expected, XElement at) where T : class
    {
        var (line, col) = XmlInput.Position(at);
        var issue = new Issue(Severity.Error, "MSG-WRONG-TYPE", $"Expected {expected}, but this is {actual}.", "/Document", line, col);
        return new ReadResult<T>(new ValidationReport(actual, [issue]), null);
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}

/// <summary>A value read from a message, with the validation report.</summary>
public sealed class ReadResult<T> where T : class
{
    internal ReadResult(ValidationReport report, T? value)
    {
        Report = report;
        Value = value;
    }

    /// <summary>Everything found while checking the message.</summary>
    public ValidationReport Report { get; }

    /// <summary>The message content, or null when it couldn't be read (wrong type, malformed, or not matching the schema).</summary>
    public T? Value { get; }

    /// <summary>True when the message was read and has no errors.</summary>
    public bool IsValid => Value is not null && Report.IsValid;

    /// <summary>The value; throws <see cref="Iso20022ValidationException"/> when the message has errors.</summary>
    public T ValueOrThrow() => IsValid ? Value! : throw new Iso20022ValidationException(Report);
}
