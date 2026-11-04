using System.Globalization;
using System.Xml.Linq;
using Iso20022Kit.Model;

namespace Iso20022Kit.Internal;

/// <summary>Reads the friendly model straight from the XML, keeping values exactly as written.</summary>
internal sealed class Mapper(XNamespace ns)
{
    private XElement? El(XElement? parent, params string[] path)
    {
        var cur = parent;
        foreach (var name in path)
        {
            if (cur is null) return null;
            cur = cur.Element(ns + name);
        }
        return cur;
    }

    private IEnumerable<XElement> Els(XElement? parent, string name) => parent?.Elements(ns + name) ?? [];

    private string? Text(XElement? parent, params string[] path)
    {
        var v = El(parent, path)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static decimal Dec(string? s) => decimal.Parse(s ?? "0", NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static DateOnly? Date(string? s) =>
        s is not null && DateOnly.TryParseExact(s.Length >= 10 ? s[..10] : s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>ISO date-times may omit the offset; those are read as UTC.</summary>
    private static DateTimeOffset? DateTime(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private Money? Money(XElement? amt) => amt is null ? null : new Money(Dec(amt.Value.Trim()), amt.Attribute("Ccy")?.Value.Trim() ?? "");

    private Party Party(XElement? p)
    {
        // Some messages wrap the party: Dbtr/Pty/Nm (camt.053 related parties)
        if (p is not null && El(p, "Pty") is { } inner) p = inner;
        var adr = El(p, "PstlAdr");
        var lines = Els(adr, "AdrLine").Select(a => a.Value.Trim()).Where(a => a.Length > 0).ToList();
        if (lines.Count == 0 && adr is not null)
        {
            var street = string.Join(" ", new[] { Text(adr, "StrtNm"), Text(adr, "BldgNb") }.Where(x => x is not null));
            var town = string.Join(" ", new[] { Text(adr, "PstCd"), Text(adr, "TwnNm") }.Where(x => x is not null));
            lines = new[] { street, town }.Where(x => x.Length > 0).ToList();
        }
        return new Party(Text(p, "Nm"), Text(adr, "Ctry") ?? Text(p, "CtryOfRes"), lines);
    }

    private Account? Account(XElement? a) => a is null ? null
        : new Account(Text(a, "Id", "IBAN"), Text(a, "Id", "Othr", "Id"), Text(a, "Ccy"), Text(a, "Nm"));

    private Agent? Agent(XElement? a)
    {
        var fi = El(a, "FinInstnId");
        if (fi is null) return null;
        var clr = El(fi, "ClrSysMmbId");
        return new Agent(Text(fi, "BICFI"), Text(fi, "Nm"), Text(clr, "ClrSysId", "Cd") ?? Text(clr, "ClrSysId", "Prtry"), Text(clr, "MmbId"));
    }

    private IReadOnlyList<string> Remittance(XElement? rmt)
    {
        var list = Els(rmt, "Ustrd").Select(u => u.Value.Trim()).Where(u => u.Length > 0).ToList();
        foreach (var strd in Els(rmt, "Strd"))
        {
            var cref = Text(strd, "CdtrRefInf", "Ref");
            if (cref is not null) list.Add(cref);
            list.AddRange(Els(strd, "RfrdDocInf").Select(r => Text(r, "Nb")).OfType<string>());
        }
        return list;
    }

    public CreditTransfer CreditTransfer(XElement document)
    {
        var body = El(document, "FIToFICstmrCdtTrf")!;
        var hdr = El(body, "GrpHdr")!;
        var hdrDate = Date(Text(hdr, "IntrBkSttlmDt"));
        var txs = Els(body, "CdtTrfTxInf").Select(t => new CreditTransferTransaction(
            EndToEndId: Text(t, "PmtId", "EndToEndId") ?? "",
            InstructionId: Text(t, "PmtId", "InstrId"),
            TransactionId: Text(t, "PmtId", "TxId"),
            Uetr: Text(t, "PmtId", "UETR"),
            Amount: Money(El(t, "IntrBkSttlmAmt"))!,
            SettlementDate: Date(Text(t, "IntrBkSttlmDt")) ?? hdrDate,
            InstructedAmount: Money(El(t, "InstdAmt")),
            ChargeBearer: Text(t, "ChrgBr") ?? "",
            Debtor: Party(El(t, "Dbtr")),
            DebtorAccount: Account(El(t, "DbtrAcct")),
            DebtorAgent: Agent(El(t, "DbtrAgt")) ?? new Agent(null),
            Creditor: Party(El(t, "Cdtr")),
            CreditorAccount: Account(El(t, "CdtrAcct")),
            CreditorAgent: Agent(El(t, "CdtrAgt")) ?? new Agent(null),
            Purpose: Text(t, "Purp", "Cd") ?? Text(t, "Purp", "Prtry"),
            RemittanceInformation: Remittance(El(t, "RmtInf")))).ToList();
        var ctrl = Text(hdr, "CtrlSum");
        return new CreditTransfer(
            Text(hdr, "MsgId") ?? "",
            DateTime(Text(hdr, "CreDtTm")),
            int.TryParse(Text(hdr, "NbOfTxs"), out var n) ? n : txs.Count,
            Text(hdr, "SttlmInf", "SttlmMtd") ?? "",
            Money(El(hdr, "TtlIntrBkSttlmAmt")),
            ctrl is null ? null : Dec(ctrl),
            Agent(El(hdr, "InstgAgt")),
            Agent(El(hdr, "InstdAgt")),
            txs);
    }

    public BankStatement BankStatement(XElement document)
    {
        var body = El(document, "BkToCstmrStmt")!;
        var hdr = El(body, "GrpHdr");
        var statements = Els(body, "Stmt").Select(s =>
        {
            var acct = El(s, "Acct");
            var balances = Els(s, "Bal").Select(b =>
            {
                var m = Money(El(b, "Amt"))!;
                if (Text(b, "CdtDbtInd") == "DBIT") m = m with { Amount = -m.Amount };
                return new Balance(Text(b, "Tp", "CdOrPrtry", "Cd") ?? Text(b, "Tp", "CdOrPrtry", "Prtry") ?? "", m,
                    Date(Text(b, "Dt", "Dt") ?? Text(b, "Dt", "DtTm")));
            }).ToList();
            var entries = Els(s, "Ntry").Select(e =>
            {
                var m = Money(El(e, "Amt"))!;
                var credit = Text(e, "CdtDbtInd") != "DBIT";
                if (!credit) m = m with { Amount = -m.Amount };
                var tx = El(e, "NtryDtls", "TxDtls");
                var other = credit ? El(tx, "RltdPties", "Dbtr") : El(tx, "RltdPties", "Cdtr");
                return new Entry(m,
                    Text(e, "Sts", "Cd") ?? Text(e, "Sts", "Prtry") ?? "",
                    Date(Text(e, "BookgDt", "Dt") ?? Text(e, "BookgDt", "DtTm")),
                    Date(Text(e, "ValDt", "Dt") ?? Text(e, "ValDt", "DtTm")),
                    Text(e, "AcctSvcrRef") ?? Text(e, "NtryRef"),
                    Text(e, "AddtlNtryInf"),
                    string.Equals(Text(e, "RvslInd"), "true", StringComparison.OrdinalIgnoreCase),
                    other is null ? null : Party(other).Name,
                    Text(tx, "Refs", "EndToEndId"),
                    Remittance(El(tx, "RmtInf")));
            }).ToList();
            return new Statement(
                Text(s, "Id") ?? "",
                Account(acct) ?? new Account(null, null),
                El(acct, "Ownr") is { } o ? Party(o) : null,
                Agent(El(acct, "Svcr")),
                DateTime(Text(s, "FrToDt", "FrDtTm")),
                DateTime(Text(s, "FrToDt", "ToDtTm")),
                balances,
                entries);
        }).ToList();
        return new BankStatement(Text(hdr, "MsgId") ?? "", DateTime(Text(hdr, "CreDtTm")), statements);
    }
}
