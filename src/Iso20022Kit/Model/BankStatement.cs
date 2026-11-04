namespace Iso20022Kit.Model;

/// <summary>
/// A camt.053 bank-to-customer statement message, simplified. It can hold several statements (one per account or period).
/// </summary>
public sealed record BankStatement(string MessageId, DateTimeOffset? CreatedAt, IReadOnlyList<Statement> Statements);

/// <summary>One account statement: balances and the entries between them.</summary>
public sealed record Statement(
    string Id,
    Account Account,
    Party? Owner,
    Agent? Servicer,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyList<Balance> Balances,
    IReadOnlyList<Entry> Entries)
{
    /// <summary>The opening booked balance (OPBD, or PRCD when only that is given).</summary>
    public Balance? Opening => Balances.FirstOrDefault(b => b.Type == "OPBD") ?? Balances.FirstOrDefault(b => b.Type == "PRCD");

    /// <summary>The closing booked balance (CLBD).</summary>
    public Balance? Closing => Balances.FirstOrDefault(b => b.Type == "CLBD");
}

/// <summary>A balance. <see cref="Type"/> is the ISO code: OPBD opening booked, CLBD closing booked, CLAV closing available, PRCD previously closed booked, ITBD interim booked…</summary>
public sealed record Balance(string Type, Money Amount, DateOnly? Date)
{
    /// <summary>What the balance type code means, in plain English.</summary>
    public string TypeName => Type switch
    {
        "OPBD" => "Opening booked",
        "CLBD" => "Closing booked",
        "OPAV" => "Opening available",
        "CLAV" => "Closing available",
        "FWAV" => "Forward available",
        "ITBD" => "Interim booked",
        "ITAV" => "Interim available",
        "PRCD" => "Previously closed booked",
        "INFO" => "Information",
        _ => Type,
    };
}

/// <summary>One statement entry: money in (positive) or out (negative).</summary>
public sealed record Entry(
    Money Amount,
    string Status,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    string? Reference,
    string? Description,
    bool IsReversal,
    string? Counterparty,
    string? EndToEndId,
    IReadOnlyList<string> RemittanceInformation)
{
    /// <summary>True for money in (CRDT).</summary>
    public bool IsCredit => Amount.Amount >= 0;
}
