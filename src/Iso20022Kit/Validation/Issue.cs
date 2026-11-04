namespace Iso20022Kit.Validation;

/// <summary>How serious a problem is.</summary>
public enum Severity
{
    /// <summary>The message breaks the schema or a rule: a receiving bank would reject it.</summary>
    Error,
    /// <summary>Probably a mistake, but the message is still valid.</summary>
    Warning,
}

/// <summary>
/// One problem found in a message.
/// </summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Code">A stable code such as <c>SCHEMA</c>, <c>AMOUNT-DECIMALS</c> or <c>PACS008-NBOFTXS</c>. See docs/rules.md.</param>
/// <param name="Message">What is wrong, in plain English.</param>
/// <param name="Path">Where: an XPath-like path from the Document element, e.g. <c>/Document/FIToFICstmrCdtTrf/CdtTrfTxInf[2]/IntrBkSttlmAmt</c>.</param>
/// <param name="Line">1-based line in the input, when known.</param>
/// <param name="Column">1-based column in the input, when known.</param>
public sealed record Issue(Severity Severity, string Code, string Message, string? Path = null, int? Line = null, int? Column = null)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var where = Line is int l ? $" (line {l}{(Column is int c ? $", column {c}" : "")})" : "";
        var path = Path is null ? "" : $" at {Path}";
        return $"{Severity.ToString().ToLowerInvariant()} {Code}{path}{where}: {Message}";
    }
}

/// <summary>The result of validating a message.</summary>
public sealed class ValidationReport
{
    internal ValidationReport(MessageId? message, IReadOnlyList<Issue> issues)
    {
        Message = message;
        // Messages and paths quote the input: make hidden characters visible and keep the length sane.
        Issues = issues.Select(i => i with { Message = Internal.SafeText.Clean(i.Message)!, Path = Internal.SafeText.Clean(i.Path) }).ToList();
    }

    /// <summary>The message type and version, if it could be recognised.</summary>
    public MessageId? Message { get; }

    /// <summary>Every problem found, errors first, in document order.</summary>
    public IReadOnlyList<Issue> Issues { get; }

    /// <summary>The problems that make the message invalid.</summary>
    public IEnumerable<Issue> Errors => Issues.Where(i => i.Severity == Severity.Error);

    /// <summary>The problems worth a look that don't make the message invalid.</summary>
    public IEnumerable<Issue> Warnings => Issues.Where(i => i.Severity == Severity.Warning);

    /// <summary>True when there are no errors (warnings are allowed).</summary>
    public bool IsValid => !Errors.Any();

    /// <inheritdoc />
    public override string ToString() =>
        IsValid && Issues.Count == 0 ? $"{Message}: valid" : $"{Message?.ToString() ?? "unknown message"}: {string.Join("; ", Issues)}";
}

/// <summary>Thrown when writing a message that doesn't validate.</summary>
public sealed class Iso20022ValidationException(ValidationReport report)
    : Exception($"The message is not valid ISO 20022:{Environment.NewLine}{string.Join(Environment.NewLine, report.Errors)}")
{
    /// <summary>The full validation result.</summary>
    public ValidationReport Report { get; } = report;
}
