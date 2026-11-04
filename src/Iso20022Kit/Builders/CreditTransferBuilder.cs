using Iso20022Kit.Messages.Pacs008V08;

namespace Iso20022Kit.Builders;

/// <summary>How the instructed bank is paid (pacs.008 SttlmMtd).</summary>
public enum SettlementMethod
{
    /// <summary>INDA: the instructed agent debits an account it holds for the instructing agent.</summary>
    InstructedAgent,
    /// <summary>INGA: the instructing agent credits an account it holds for the instructed agent.</summary>
    InstructingAgent,
    /// <summary>COVE: settled through a separate cover payment.</summary>
    CoverMethod,
    /// <summary>CLRG: settled through a clearing system (e.g. Fedwire, FedNow, TARGET2).</summary>
    ClearingSystem,
}

/// <summary>Who pays the charges (ChrgBr).</summary>
public enum ChargeBearer
{
    /// <summary>SHAR: each party pays its own bank's charges.</summary>
    Shared,
    /// <summary>DEBT: the debtor pays all charges.</summary>
    Debtor,
    /// <summary>CRED: the creditor pays all charges.</summary>
    Creditor,
    /// <summary>SLEV: as agreed in the service level (e.g. SEPA).</summary>
    FollowingServiceLevel,
}

/// <summary>
/// Builds a valid pacs.008.001.08 customer credit transfer without touching the XML.
/// </summary>
/// <example>
/// <code>
/// var xml = CreditTransferBuilder.Create("MSG-20261104-001")
///     .Settlement(SettlementMethod.InstructedAgent)
///     .AddTransaction(t => t
///         .EndToEndId("INV-445")
///         .Amount(1000.00m, "USD", new DateOnly(2026, 11, 4))
///         .Debtor("Sokha Chan", account: "001234567", agentBic: "ABAAKHPP")
///         .Creditor("Acme Corp", account: "987654321", agentBic: "CHASUS33")
///         .Remittance("Invoice 445"))
///     .ToXml();
/// </code>
/// </example>
public sealed class CreditTransferBuilder
{
    private readonly string _messageId;
    private DateTimeOffset _created = DateTimeOffset.UtcNow;
    private SettlementMethod _method = SettlementMethod.InstructedAgent;
    private string? _clearingSystem;
    private BranchAndFinancialInstitutionIdentification6? _instructing, _instructed;
    private bool _totals = true;
    private readonly List<TransactionBuilder> _transactions = [];

    private CreditTransferBuilder(string messageId) => _messageId = messageId;

    /// <summary>Starts a message. <paramref name="messageId"/> must be unique per sender (max 35 characters).</summary>
    public static CreditTransferBuilder Create(string messageId) => new(messageId);

    /// <summary>When the message was created (default: now). Written in UTC.</summary>
    public CreditTransferBuilder CreatedAt(DateTimeOffset when) { _created = when; return this; }

    /// <summary>How the payment is settled between the banks. For <see cref="SettlementMethod.ClearingSystem"/>, give the clearing system code (e.g. "FDW" for Fedwire).</summary>
    public CreditTransferBuilder Settlement(SettlementMethod method, string? clearingSystemCode = null)
    {
        _method = method;
        _clearingSystem = clearingSystemCode;
        return this;
    }

    /// <summary>The bank sending this message, by BIC.</summary>
    public CreditTransferBuilder InstructingAgent(string bic) { _instructing = AgentBic(bic); return this; }

    /// <summary>The bank receiving this message, by BIC.</summary>
    public CreditTransferBuilder InstructedAgent(string bic) { _instructed = AgentBic(bic); return this; }

    /// <summary>Whether to fill in CtrlSum and (for one currency) TtlIntrBkSttlmAmt (default true).</summary>
    public CreditTransferBuilder WithTotals(bool on) { _totals = on; return this; }

    /// <summary>Adds one payment.</summary>
    public CreditTransferBuilder AddTransaction(Action<TransactionBuilder> configure)
    {
        var t = new TransactionBuilder();
        configure(t);
        _transactions.Add(t);
        return this;
    }

    /// <summary>The generated document, for further changes before writing.</summary>
    public Document Build()
    {
        if (_transactions.Count == 0) throw new InvalidOperationException("Add at least one transaction.");
        var txs = _transactions.Select(t => t.Build()).ToList();
        var header = new GroupHeader93
        {
            MsgId = _messageId,
            CreDtTm = _created.UtcDateTime,
            NbOfTxs = txs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SttlmInf = new SettlementInstruction7
            {
                SttlmMtd = _method switch
                {
                    SettlementMethod.InstructingAgent => SettlementMethod1Code.Inga,
                    SettlementMethod.CoverMethod => SettlementMethod1Code.Cove,
                    SettlementMethod.ClearingSystem => SettlementMethod1Code.Clrg,
                    _ => SettlementMethod1Code.Inda,
                },
                ClrSys = _clearingSystem is null ? null : new ClearingSystemIdentification3Choice { Cd = _clearingSystem },
            },
            InstgAgt = _instructing,
            InstdAgt = _instructed,
        };
        if (_totals)
        {
            header.CtrlSum = txs.Sum(t => t.IntrBkSttlmAmt.Value);
            var currencies = txs.Select(t => t.IntrBkSttlmAmt.Ccy).Distinct().ToList();
            if (currencies.Count == 1)
                header.TtlIntrBkSttlmAmt = new ActiveCurrencyAndAmount { Ccy = currencies[0], Value = header.CtrlSum.Value };
        }
        var body = new FiToFiCustomerCreditTransferV08 { GrpHdr = header };
        foreach (var t in txs) body.CdtTrfTxInf.Add(t);
        return new Document { FiToFiCstmrCdtTrf = body };
    }

    /// <summary>The message as XML. Throws <see cref="Validation.Iso20022ValidationException"/> if it isn't valid (e.g. a bad BIC or IBAN).</summary>
    public string ToXml() => Iso20022.Write(Build());

    internal static BranchAndFinancialInstitutionIdentification6 AgentBic(string bic) =>
        new() { FinInstnId = new FinancialInstitutionIdentification18 { Bicfi = bic } };

    /// <summary>One payment in the message.</summary>
    public sealed class TransactionBuilder
    {
        private string? _endToEnd, _instrId, _txId, _uetr = Guid.NewGuid().ToString();
        private decimal? _amount;
        private string? _currency;
        private DateOnly? _date;
        private ChargeBearer _charges = ChargeBearer.Shared;
        private PartyIdentification135? _debtor, _creditor;
        private CashAccount38? _debtorAccount, _creditorAccount;
        private BranchAndFinancialInstitutionIdentification6? _debtorAgent, _creditorAgent;
        private readonly List<string> _remittance = [];

        internal TransactionBuilder() { }

        /// <summary>The end-to-end reference that travels with the payment to the creditor (required, max 35).</summary>
        public TransactionBuilder EndToEndId(string id) { _endToEnd = id; return this; }

        /// <summary>Optional instruction and transaction ids.</summary>
        public TransactionBuilder Ids(string? instructionId = null, string? transactionId = null) { _instrId = instructionId; _txId = transactionId; return this; }

        /// <summary>The UETR (a UUID v4). One is generated by default; pass null to leave it out.</summary>
        public TransactionBuilder Uetr(string? uetr) { _uetr = uetr; return this; }

        /// <summary>The interbank settlement amount and date.</summary>
        public TransactionBuilder Amount(decimal amount, string currency, DateOnly? settlementDate = null)
        {
            _amount = amount; _currency = currency; _date = settlementDate; return this;
        }

        /// <summary>Who pays the charges (default shared).</summary>
        public TransactionBuilder Charges(ChargeBearer bearer) { _charges = bearer; return this; }

        /// <summary>The payer: name, account (IBAN or other number) and bank (BIC, or a clearing member id such as a US routing number with <paramref name="clearingSystem"/> "USABA").</summary>
        public TransactionBuilder Debtor(string name, string? iban = null, string? account = null, string? agentBic = null, string? clearingSystem = null, string? clearingMemberId = null, string? country = null)
        {
            _debtor = Party(name, country);
            _debtorAccount = Account(iban, account);
            _debtorAgent = Agent(agentBic, clearingSystem, clearingMemberId);
            return this;
        }

        /// <summary>The payee: name, account (IBAN or other number) and bank (BIC, or a clearing member id).</summary>
        public TransactionBuilder Creditor(string name, string? iban = null, string? account = null, string? agentBic = null, string? clearingSystem = null, string? clearingMemberId = null, string? country = null)
        {
            _creditor = Party(name, country);
            _creditorAccount = Account(iban, account);
            _creditorAgent = Agent(agentBic, clearingSystem, clearingMemberId);
            return this;
        }

        /// <summary>Free-text remittance information, e.g. an invoice number (each line max 140).</summary>
        public TransactionBuilder Remittance(params string[] lines) { _remittance.AddRange(lines); return this; }

        internal CreditTransferTransaction39 Build()
        {
            if (_endToEnd is null) throw new InvalidOperationException("Each transaction needs an EndToEndId.");
            if (_amount is null || _currency is null) throw new InvalidOperationException("Each transaction needs an Amount.");
            if (_debtor is null || _debtorAgent is null) throw new InvalidOperationException("Each transaction needs a Debtor with a bank (agentBic or clearingMemberId).");
            if (_creditor is null || _creditorAgent is null) throw new InvalidOperationException("Each transaction needs a Creditor with a bank (agentBic or clearingMemberId).");
            var tx = new CreditTransferTransaction39
            {
                PmtId = new PaymentIdentification7 { EndToEndId = _endToEnd, InstrId = _instrId, TxId = _txId, Uetr = _uetr },
                IntrBkSttlmAmt = new ActiveCurrencyAndAmount { Value = _amount.Value, Ccy = _currency },
                ChrgBr = _charges switch
                {
                    ChargeBearer.Debtor => ChargeBearerType1Code.Debt,
                    ChargeBearer.Creditor => ChargeBearerType1Code.Cred,
                    ChargeBearer.FollowingServiceLevel => ChargeBearerType1Code.Slev,
                    _ => ChargeBearerType1Code.Shar,
                },
                Dbtr = _debtor,
                DbtrAcct = _debtorAccount,
                DbtrAgt = _debtorAgent,
                Cdtr = _creditor,
                CdtrAcct = _creditorAccount,
                CdtrAgt = _creditorAgent,
            };
            if (_date is DateOnly d) tx.IntrBkSttlmDt = d.ToDateTime(TimeOnly.MinValue);
            if (_remittance.Count > 0)
            {
                var rmt = new RemittanceInformation16();
                foreach (var line in _remittance) rmt.Ustrd.Add(line);
                tx.RmtInf = rmt;
            }
            return tx;
        }

        private static PartyIdentification135 Party(string name, string? country) =>
            new() { Nm = name, PstlAdr = country is null ? null : new PostalAddress24 { Ctry = country } };

        private static CashAccount38? Account(string? iban, string? other) =>
            iban is null && other is null ? null
            : new CashAccount38 { Id = iban is not null ? new AccountIdentification4Choice { Iban = iban } : new AccountIdentification4Choice { Othr = new GenericAccountIdentification1 { Id = other } } };

        private static BranchAndFinancialInstitutionIdentification6? Agent(string? bic, string? clearingSystem, string? memberId)
        {
            if (bic is null && memberId is null) return null;
            var fi = new FinancialInstitutionIdentification18 { Bicfi = bic };
            if (memberId is not null)
                fi.ClrSysMmbId = new ClearingSystemMemberIdentification2
                {
                    MmbId = memberId,
                    ClrSysId = clearingSystem is null ? null : new ClearingSystemIdentification2Choice { Cd = clearingSystem },
                };
            return new BranchAndFinancialInstitutionIdentification6 { FinInstnId = fi };
        }
    }
}
