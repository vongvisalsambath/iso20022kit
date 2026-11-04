using System.Globalization;

namespace Iso20022Kit.Model;

/// <summary>An amount in a currency. In statements, debits are negative.</summary>
public sealed record Money(decimal Amount, string Currency)
{
    /// <summary>e.g. <c>1,000.00 USD</c> (decimals as written in the message).</summary>
    public override string ToString() => $"{Amount.ToString("#,0.##########", CultureInfo.InvariantCulture)} {Currency}";
}

/// <summary>A person or company (debtor, creditor, account owner).</summary>
public sealed record Party(string? Name, string? Country = null, IReadOnlyList<string>? AddressLines = null)
{
    /// <summary>Address lines and town/country as written (may be empty).</summary>
    public IReadOnlyList<string> AddressLines { get; init; } = AddressLines ?? [];
}

/// <summary>An account: an IBAN, or another identifier (a local account number).</summary>
public sealed record Account(string? Iban, string? Other, string? Currency = null, string? Name = null)
{
    /// <summary>The IBAN, else the other identifier.</summary>
    public string? Number => Iban ?? Other;
}

/// <summary>A bank: by BIC, or by clearing system member id (e.g. a US ABA routing number), or by name.</summary>
public sealed record Agent(string? Bic, string? Name = null, string? ClearingSystem = null, string? ClearingMemberId = null)
{
    /// <summary>The best single identifier: BIC, else clearing member id, else name.</summary>
    public string? Display => Bic ?? (ClearingMemberId is null ? Name : $"{ClearingSystem}:{ClearingMemberId}".TrimStart(':'));
}
