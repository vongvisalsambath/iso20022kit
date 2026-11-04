namespace Iso20022Kit.Model;

/// <summary>
/// A pacs.008 FI to FI customer credit transfer, simplified: one bank asking another to pay customers.
/// For every field of the message, read the generated type instead (<see cref="Iso20022.ReadDocument{T}(string, Validation.ValidationOptions)"/>).
/// </summary>
public sealed record CreditTransfer(
    string MessageId,
    DateTimeOffset? CreatedAt,
    int NumberOfTransactions,
    string SettlementMethod,
    Money? TotalSettlementAmount,
    decimal? ControlSum,
    Agent? InstructingAgent,
    Agent? InstructedAgent,
    IReadOnlyList<CreditTransferTransaction> Transactions);

/// <summary>One payment inside a pacs.008.</summary>
public sealed record CreditTransferTransaction(
    string EndToEndId,
    string? InstructionId,
    string? TransactionId,
    string? Uetr,
    Money Amount,
    DateOnly? SettlementDate,
    Money? InstructedAmount,
    string ChargeBearer,
    Party Debtor,
    Account? DebtorAccount,
    Agent DebtorAgent,
    Party Creditor,
    Account? CreditorAccount,
    Agent CreditorAgent,
    string? Purpose,
    IReadOnlyList<string> RemittanceInformation);
