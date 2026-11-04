using System.Xml.Linq;

namespace Iso20022Kit.Validation.Rules;

internal static class RuleSet
{
    private static readonly IRule[] Common = [new AmountDecimalsRule(), new IbanCheckDigitsRule()];
    private static readonly IRule[] Pacs008 = [new Pacs008NumberOfTransactionsRule(), new Pacs008ControlSumRule(), new Pacs008TotalAmountRule()];
    private static readonly IRule[] Camt053 = [new Camt053BalanceRule(), new Camt053SummaryRule()];

    public static IEnumerable<IRule> For(MessageId id) => id.Family switch
    {
        "pacs.008" => Common.Concat(Pacs008),
        "camt.053" => Common.Concat(Camt053),
        _ => Common,
    };

    /// <summary>Every rule code with a one-line description (for docs and tools).</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>
    {
        ["SCHEMA"] = "The message must match the official ISO 20022 schema for its type and version.",
        ["AMOUNT-DECIMALS"] = "An amount may not have more decimals than its currency allows in ISO 4217 (USD 2, JPY 0, KWD 3).",
        ["CURRENCY-UNKNOWN"] = "Warning: the currency code is not an active ISO 4217 code with minor units.",
        ["IBAN-CHECKSUM"] = "An IBAN's two check digits must be correct (ISO 13616 mod 97).",
        ["PACS008-NBOFTXS"] = "pacs.008: GrpHdr/NbOfTxs must equal the number of CdtTrfTxInf transactions.",
        ["PACS008-CTRLSUM"] = "pacs.008: GrpHdr/CtrlSum, when present, must equal the sum of every IntrBkSttlmAmt.",
        ["PACS008-TOTAL"] = "pacs.008: GrpHdr/TtlIntrBkSttlmAmt, when present, must equal the sum of every IntrBkSttlmAmt, all in its currency.",
        ["CAMT053-BALANCE"] = "camt.053: opening booked balance + booked credits − booked debits must equal the closing booked balance.",
        ["CAMT053-SUMMARY"] = "camt.053: TxsSummry counts and sums must match the entries in the statement.",
    };
}

/// <summary>AMOUNT-DECIMALS and CURRENCY-UNKNOWN: every element with a Ccy attribute holding an amount.</summary>
internal sealed class AmountDecimalsRule : IRule
{
    public string Code => "AMOUNT-DECIMALS";

    public void Check(RuleContext c)
    {
        foreach (var e in c.Document.Descendants())
        {
            var ccy = e.Attribute("Ccy");
            if (ccy is null || e.HasElements) continue;
            var code = ccy.Value.Trim();
            var text = e.Value.Trim();
            var allowed = Iso4217.Decimals(code);
            if (allowed is null)
            {
                if (!c.Options.DisabledRules.Contains("CURRENCY-UNKNOWN"))
                    c.Warn("CURRENCY-UNKNOWN", $"\"{code}\" is not an active ISO 4217 currency with minor units, so its decimals can't be checked.", ccy);
                continue;
            }
            var dot = text.IndexOf('.');
            var decimals = dot < 0 ? 0 : text.Length - dot - 1;
            if (decimals > allowed)
                c.Error(Code, $"{text} {code} has {decimals} decimal{(decimals == 1 ? "" : "s")}, but {code} allows {(allowed == 0 ? "none" : allowed.ToString())}.", e);
        }
    }
}

/// <summary>IBAN-CHECKSUM: every IBAN element.</summary>
internal sealed class IbanCheckDigitsRule : IRule
{
    public string Code => "IBAN-CHECKSUM";

    public void Check(RuleContext c)
    {
        foreach (var e in c.Document.Descendants(c.Ns + "IBAN"))
        {
            var iban = e.Value.Trim();
            if (!Iban.HasValidCheckDigits(iban))
                c.Error(Code, $"{iban} is not a valid IBAN: its check digits ({iban[2..Math.Min(4, iban.Length)]}) don't match the rest of the number.", e);
        }
    }
}

internal sealed class Pacs008NumberOfTransactionsRule : IRule
{
    public string Code => "PACS008-NBOFTXS";

    public void Check(RuleContext c)
    {
        var body = c.El(c.Document, "FIToFICstmrCdtTrf");
        var nb = c.El(body, "GrpHdr", "NbOfTxs");
        if (body is null || nb is null) return;
        var count = c.Els(body, "CdtTrfTxInf").Count();
        if (!int.TryParse(nb.Value.Trim(), out var declared) || declared != count)
            c.Error(Code, $"NbOfTxs says {nb.Value.Trim()}, but the message contains {count} transaction{(count == 1 ? "" : "s")} (CdtTrfTxInf).", nb);
    }
}

internal sealed class Pacs008ControlSumRule : IRule
{
    public string Code => "PACS008-CTRLSUM";

    public void Check(RuleContext c)
    {
        var body = c.El(c.Document, "FIToFICstmrCdtTrf");
        var ctrl = c.El(body, "GrpHdr", "CtrlSum");
        if (body is null || ctrl is null) return;
        var sum = c.Els(body, "CdtTrfTxInf").Select(t => RuleContext.Decimal(c.El(t, "IntrBkSttlmAmt")) ?? 0m).Sum();
        var declared = RuleContext.Decimal(ctrl);
        if (declared != sum)
            c.Error(Code, $"CtrlSum is {ctrl.Value.Trim()}, but the transaction amounts (IntrBkSttlmAmt) add up to {RuleContext.Amount(sum)}.", ctrl);
    }
}

internal sealed class Pacs008TotalAmountRule : IRule
{
    public string Code => "PACS008-TOTAL";

    public void Check(RuleContext c)
    {
        var body = c.El(c.Document, "FIToFICstmrCdtTrf");
        var total = c.El(body, "GrpHdr", "TtlIntrBkSttlmAmt");
        if (body is null || total is null) return;
        var ccy = total.Attribute("Ccy")?.Value.Trim();
        var amounts = c.Els(body, "CdtTrfTxInf").Select(t => c.El(t, "IntrBkSttlmAmt")).OfType<XElement>().ToList();
        foreach (var a in amounts.Where(a => a.Attribute("Ccy")?.Value.Trim() != ccy))
            c.Error(Code, $"TtlIntrBkSttlmAmt is in {ccy}, but this transaction settles in {a.Attribute("Ccy")?.Value.Trim()}. A total can only add up amounts in one currency.", a);
        var sum = amounts.Select(a => RuleContext.Decimal(a) ?? 0m).Sum();
        if (RuleContext.Decimal(total) != sum)
            c.Error(Code, $"TtlIntrBkSttlmAmt is {total.Value.Trim()} {ccy}, but the transaction amounts add up to {RuleContext.Amount(sum)}.", total);
    }
}

/// <summary>Shared camt.053 reading: balances and entries with their sign.</summary>
internal static class Camt053
{
    public static decimal? Signed(RuleContext c, XElement holder)
    {
        var amt = RuleContext.Decimal(c.El(holder, "Amt"));
        if (amt is null) return null;
        return c.Text(holder, "CdtDbtInd") == "DBIT" ? -amt.Value : amt.Value;
    }

    public static string? BalanceCode(RuleContext c, XElement bal) => c.Text(bal, "Tp", "CdOrPrtry", "Cd");

    public static string? EntryStatus(RuleContext c, XElement ntry) => c.Text(ntry, "Sts", "Cd") ?? c.Text(ntry, "Sts", "Prtry");
}

internal sealed class Camt053BalanceRule : IRule
{
    public string Code => "CAMT053-BALANCE";

    public void Check(RuleContext c)
    {
        foreach (var stmt in c.Els(c.El(c.Document, "BkToCstmrStmt"), "Stmt"))
        {
            var balances = c.Els(stmt, "Bal").ToList();
            var opening = balances.FirstOrDefault(b => Camt053.BalanceCode(c, b) is "OPBD" or "PRCD");
            var closing = balances.FirstOrDefault(b => Camt053.BalanceCode(c, b) == "CLBD");
            if (opening is null || closing is null) continue;

            var ccy = c.El(opening, "Amt")?.Attribute("Ccy")?.Value;
            var entries = c.Els(stmt, "Ntry").Where(n => Camt053.EntryStatus(c, n) == "BOOK").ToList();
            if (c.El(closing, "Amt")?.Attribute("Ccy")?.Value != ccy || entries.Any(n => c.El(n, "Amt")?.Attribute("Ccy")?.Value != ccy))
                continue; // mixed currencies: the balance can't be recomputed
            var open = Camt053.Signed(c, opening);
            var close = Camt053.Signed(c, closing);
            if (open is null || close is null) continue;
            var movement = entries.Select(n => Camt053.Signed(c, n) ?? 0m).Sum();
            var expected = open.Value + movement;
            if (expected != close.Value)
            {
                var credits = entries.Where(n => c.Text(n, "CdtDbtInd") == "CRDT").Select(n => RuleContext.Decimal(c.El(n, "Amt")) ?? 0m).Sum();
                var debits = entries.Where(n => c.Text(n, "CdtDbtInd") == "DBIT").Select(n => RuleContext.Decimal(c.El(n, "Amt")) ?? 0m).Sum();
                c.Error(Code,
                    $"The statement doesn't add up: opening {RuleContext.Amount(open.Value)} + booked credits {RuleContext.Amount(credits)} − booked debits {RuleContext.Amount(debits)} " +
                    $"= {RuleContext.Amount(expected)} {ccy}, but the closing booked balance is {RuleContext.Amount(close.Value)} {ccy} (difference {RuleContext.Amount(close.Value - expected)}).", closing);
            }
        }
    }
}

internal sealed class Camt053SummaryRule : IRule
{
    public string Code => "CAMT053-SUMMARY";

    public void Check(RuleContext c)
    {
        foreach (var stmt in c.Els(c.El(c.Document, "BkToCstmrStmt"), "Stmt"))
        {
            var summary = c.El(stmt, "TxsSummry");
            if (summary is null) continue;
            var entries = c.Els(stmt, "Ntry").ToList();
            decimal Amount(XElement n) => RuleContext.Decimal(c.El(n, "Amt")) ?? 0m;
            var credits = entries.Where(n => c.Text(n, "CdtDbtInd") == "CRDT").ToList();
            var debits = entries.Where(n => c.Text(n, "CdtDbtInd") == "DBIT").ToList();

            CheckGroup(c, c.El(summary, "TtlNtries"), "TtlNtries (all entries)", entries.Count, entries.Sum(Amount));
            CheckGroup(c, c.El(summary, "TtlCdtNtries"), "TtlCdtNtries (credit entries)", credits.Count, credits.Sum(Amount));
            CheckGroup(c, c.El(summary, "TtlDbtNtries"), "TtlDbtNtries (debit entries)", debits.Count, debits.Sum(Amount));

            var net = c.El(summary, "TtlNtries", "TtlNetNtry");
            if (net is not null)
            {
                var declared = Camt053.Signed(c, net);
                var actual = credits.Sum(Amount) - debits.Sum(Amount);
                if (declared is not null && declared.Value != actual)
                    c.Error(Code, $"TtlNetNtry is {Side(declared.Value)}, but the entries net to {Side(actual)} (credits − debits = {RuleContext.Amount(actual)}).", net);
            }
        }
    }

    private static string Side(decimal v) => $"{(v < 0 ? "a debit" : "a credit")} of {RuleContext.Amount(Math.Abs(v))}";

    private void CheckGroup(RuleContext c, XElement? group, string label, int count, decimal sum)
    {
        if (group is null) return;
        var nb = c.El(group, "NbOfNtries");
        if (nb is not null && (!int.TryParse(nb.Value.Trim(), out var n) || n != count))
            c.Error(Code, $"{label}: NbOfNtries says {nb.Value.Trim()}, but the statement has {count}.", nb);
        var s = c.El(group, "Sum");
        if (s is not null && RuleContext.Decimal(s) != sum)
            c.Error(Code, $"{label}: Sum says {s.Value.Trim()}, but the entries add up to {RuleContext.Amount(sum)}.", s);
    }
}
