using System.Globalization;
using System.Text.RegularExpressions;
using Iso20022Kit.Internal;
using Iso20022Kit.Validation.Rules;

namespace Iso20022Kit.Mt;

/// <summary>A customer or institution as an MT field gives it: option letter, account or identifier, BIC, name and address lines.</summary>
/// <param name="Option">The field's letter: A (BIC), F (structured), K or D (name and address), C (account only), or empty.</param>
/// <param name="Account">The account or party identifier line, without its leading slash.</param>
/// <param name="Bic">The BIC, for option A.</param>
/// <param name="Lines">Name and address lines (for option F, as written, with their 1/, 2/… prefixes).</param>
public sealed record MtParty(string Option, string? Account, string? Bic, IReadOnlyList<string> Lines);

/// <summary>An MT103 single customer credit transfer, field by field.</summary>
public sealed record Mt103
{
    /// <summary>The sending bank (from the basic or application header).</summary>
    public string? SenderBic { get; init; }
    /// <summary>The receiving bank (from the application header).</summary>
    public string? ReceiverBic { get; init; }
    /// <summary>The UETR from the user header (field 121).</summary>
    public string? Uetr { get; init; }
    /// <summary>Field 20, the sender's reference.</summary>
    public required string Reference { get; init; }
    /// <summary>Field 23B: CRED, CRTS, SPAY, SPRI or SSTD.</summary>
    public required string BankOperationCode { get; init; }
    /// <summary>Field 23E, each "CODE" or "CODE/text".</summary>
    public IReadOnlyList<string> InstructionCodes { get; init; } = [];
    /// <summary>Field 32A: value date, currency and interbank settled amount.</summary>
    public required DateOnly ValueDate { get; init; }
    /// <summary>Field 32A's currency.</summary>
    public required string Currency { get; init; }
    /// <summary>Field 32A's amount.</summary>
    public required decimal Amount { get; init; }
    /// <summary>Field 33B: the instructed currency and amount.</summary>
    public (string Currency, decimal Amount)? Instructed { get; init; }
    /// <summary>Field 36: the exchange rate.</summary>
    public decimal? ExchangeRate { get; init; }
    /// <summary>Field 50a: the ordering customer (the payer).</summary>
    public required MtParty OrderingCustomer { get; init; }
    /// <summary>Field 52a: the payer's bank.</summary>
    public MtParty? OrderingInstitution { get; init; }
    /// <summary>Field 53a.</summary>
    public MtParty? SendersCorrespondent { get; init; }
    /// <summary>Field 54a.</summary>
    public MtParty? ReceiversCorrespondent { get; init; }
    /// <summary>Field 55a.</summary>
    public MtParty? ThirdReimbursementInstitution { get; init; }
    /// <summary>Field 56a.</summary>
    public MtParty? Intermediary { get; init; }
    /// <summary>Field 57a: the payee's bank.</summary>
    public MtParty? AccountWithInstitution { get; init; }
    /// <summary>Field 59a: the beneficiary (the payee).</summary>
    public required MtParty Beneficiary { get; init; }
    /// <summary>Field 70, up to 4 lines of 35.</summary>
    public IReadOnlyList<string> RemittanceInformation { get; init; } = [];
    /// <summary>Field 71A: OUR, SHA or BEN.</summary>
    public required string ChargesCode { get; init; }
    /// <summary>Field 71F, the sender's charges (repeatable).</summary>
    public IReadOnlyList<(string Currency, decimal Amount)> SendersCharges { get; init; } = [];
    /// <summary>Field 71G, the receiver's charges.</summary>
    public (string Currency, decimal Amount)? ReceiversCharges { get; init; }
    /// <summary>Field 72, up to 6 lines of 35.</summary>
    public IReadOnlyList<string> SenderToReceiverInformation { get; init; } = [];
    /// <summary>Field 77B, up to 3 lines of 35.</summary>
    public IReadOnlyList<string> RegulatoryReporting { get; init; } = [];
    /// <summary>Fields that were read but have no place in the translation (13C, 26T, 51A, 77T…).</summary>
    public IReadOnlyList<MtField> Other { get; init; } = [];

    /// <summary>The result of reading an MT103.</summary>
    public sealed record ReadResult(Mt103? Message, IReadOnlyList<MtIssue> Issues)
    {
        /// <summary>True when the message was read with no errors.</summary>
        public bool IsValid => Message is not null && Issues.All(i => !i.IsError);
    }

    /// <summary>Reads and checks an MT103. Never throws for bad input: everything is in <see cref="ReadResult.Issues"/>.</summary>
    public static ReadResult Read(string text)
    {
        var issues = new List<MtIssue>();
        var fin = FinMessage.Parse(text ?? "", issues);
        if (fin is null) return new(null, issues);
        if (fin.MessageType is string t && t != "103") issues.Add(new("MT-TYPE", $"This is an MT{t}, not an MT103."));
        if (fin.UserHeader.TryGetValue("119", out var validation) && validation is "REMIT" or "RFDD")
            issues.Add(new("MT-VARIANT", $"MT103 {validation} isn't supported (its field 77T carries an envelope).", IsError: true));

        var f = new Reader(fin.Fields, issues);
        foreach (var field in fin.Fields)
        {
            if (field.Lines.Any(l => l.Length > 35 && field.Tag is not ("77T")))
                issues.Add(new("MT-LINE-LENGTH", $"Field {field.Tag} has a line longer than 35 characters.", field.Line));
            // an index, not FirstOrDefault: default(char) is U+0000, which would hide a NUL
            var at = field.Value.AsSpan().IndexOfAnyExcept(XAndNewline);
            if (at >= 0) issues.Add(new("MT-CHARSET", $"Field {field.Tag} contains {SafeText.Quote(field.Value[at].ToString())}, which isn't in the SWIFT X character set.", field.Line));
        }

        string? uetr = null;
        if (fin.UserHeader.TryGetValue("121", out var u))
        {
            if (Uuid4().IsMatch(u)) uetr = u;
            else issues.Add(new("MT-UETR", $"The UETR (121) {SafeText.Quote(u)} isn't a lowercase UUID version 4."));
        }

        var reference = f.One("20", required: true);
        if (reference is not null && (reference.Length > 16 || reference.StartsWith('/') || reference.EndsWith('/') || reference.Contains("//", StringComparison.Ordinal) || reference.Contains('\n')))
            issues.Add(new("MT-20", "Field 20 is at most 16 characters, on one line, and can't start or end with '/' or contain '//'.", f.LineOf("20")));
        var op = f.One("23B", required: true);
        if (op is not null && op is not ("CRED" or "CRTS" or "SPAY" or "SPRI" or "SSTD")) issues.Add(new("MT-23B", $"Field 23B is {SafeText.Quote(op)}; it is CRED, CRTS, SPAY, SPRI or SSTD.", f.LineOf("23B")));
        var codes = f.All("23E").Select(v => v.Value).ToList();
        foreach (var c in codes)
            if (!Regex.IsMatch(c, @"^[A-Z]{4}(/.{1,30})?\z", RegexOptions.None, TimeSpan.FromSeconds(1)))
                issues.Add(new("MT-23E", $"Field 23E {SafeText.Quote(c)} should be a 4-letter code, optionally followed by /text.", f.LineOf("23E")));

        var a32 = f.One("32A", required: true);
        DateOnly date = default;
        string currency = "";
        decimal amount = 0;
        if (a32 is not null)
        {
            if (a32.Length < 10 || !TryDate(a32[..6], out date) || !TryCurrencyAmount(a32[6..], out currency, out amount, issues, "32A", f.LineOf("32A")))
            {
                if (a32.Length < 10 || !TryDate(a32[..6], out _)) issues.Add(new("MT-32A", $"Field 32A should be a date (YYMMDD), a currency and an amount, like 261104USD1000,00; it is {SafeText.Quote(a32)}.", f.LineOf("32A")));
            }
            else if (amount == 0) issues.Add(new("MT-32A", "The amount in field 32A is zero.", f.LineOf("32A")));
        }

        (string, decimal)? instructed = null;
        if (f.One("33B") is string b33)
            instructed = TryCurrencyAmount(b33, out var c33, out var a33, issues, "33B", f.LineOf("33B")) ? (c33, a33) : null;
        decimal? rate = null;
        if (f.One("36") is string r36)
        {
            if (Regex.IsMatch(r36, @"^\d{1,11},\d{0,11}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) && r36.Length <= 12 && decimal.TryParse(r36.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r) && r > 0) rate = r;
            else issues.Add(new("MT-36", $"Field 36 should be a rate like 0,9175; it is {SafeText.Quote(r36)}.", f.LineOf("36")));
        }

        var ordering = f.Party("50", "AFK", required: true);
        var beneficiary = f.Party("59", "AF", required: true, allowNoLetter: true);
        var charges = f.One("71A", required: true);
        if (charges is not null && charges is not ("OUR" or "SHA" or "BEN")) issues.Add(new("MT-71A", $"Field 71A is {SafeText.Quote(charges)}; it is OUR, SHA or BEN.", f.LineOf("71A")));
        var senders = f.All("71F").Select(v => TryCurrencyAmount(v.Value, out var c, out var a, issues, "71F", v.Line) ? (c, a) : ((string, decimal)?)null).OfType<(string, decimal)>().ToList();
        (string, decimal)? receivers = f.One("71G") is string g71 && TryCurrencyAmount(g71, out var cg, out var ag, issues, "71G", f.LineOf("71G")) ? (cg, ag) : null;

        // the network's cross-field rules that matter for translation
        if (instructed is { } ins && ins.Item1 != currency && rate is null)
            issues.Add(new("MT-RULE-36", "Field 36 (exchange rate) is required when 33B's currency differs from 32A's.", f.LineOf("33B")));
        if (rate is not null && (instructed is null || instructed.Value.Item1 == currency))
            issues.Add(new("MT-RULE-36", "Field 36 (exchange rate) is only allowed when 33B's currency differs from 32A's.", f.LineOf("36")));
        if ((senders.Count > 0 || receivers is not null) && instructed is null)
            issues.Add(new("MT-RULE-33B", "Field 33B (instructed amount) is required when 71F or 71G is present.", f.LineOf("71F") ?? f.LineOf("71G")));
        if (charges == "OUR" && senders.Count > 0) issues.Add(new("MT-RULE-71", "With 71A OUR, field 71F isn't allowed.", f.LineOf("71F")));
        if (charges == "SHA" && receivers is not null) issues.Add(new("MT-RULE-71", "With 71A SHA, field 71G isn't allowed.", f.LineOf("71G")));
        if (charges == "BEN" && senders.Count == 0) issues.Add(new("MT-RULE-71", "With 71A BEN, at least one 71F is required.", f.LineOf("71A")));
        if (charges == "BEN" && receivers is not null) issues.Add(new("MT-RULE-71", "With 71A BEN, field 71G isn't allowed.", f.LineOf("71G")));

        var known = new HashSet<string>(StringComparer.Ordinal) { "20", "23B", "23E", "32A", "33B", "36", "50A", "50F", "50K", "52A", "52D", "53A", "53B", "53D", "54A", "54B", "54D", "55A", "55B", "55D", "56A", "56C", "56D", "57A", "57B", "57C", "57D", "59", "59A", "59F", "70", "71A", "71F", "71G", "72", "77B" };
        var other = fin.Fields.Where(x => !known.Contains(x.Tag)).ToList();
        foreach (var x in other.Where(x => x.Tag is not ("13C" or "26T" or "51A" or "77T")))
            issues.Add(new("MT-FIELD", $"Field {SafeText.Quote(x.Tag)} isn't part of an MT103.", x.Line));

        foreach (var (tag, max) in new[] { ("70", 4), ("72", 6), ("77B", 3) })
            if (f.One(tag) is string v && v.Split('\n').Length > max) issues.Add(new("MT-LINES", $"Field {tag} has more than {max} lines.", f.LineOf(tag)));

        if (reference is null || op is null || a32 is null || ordering is null || beneficiary is null || charges is null || issues.Any(i => i.IsError && i.Code is "MT-32A" or "MT-AMOUNT"))
            return new(null, issues);

        return new(new Mt103
        {
            SenderBic = fin.SenderBic,
            ReceiverBic = fin.ReceiverBic,
            Uetr = uetr,
            Reference = reference,
            BankOperationCode = op,
            InstructionCodes = codes,
            ValueDate = date,
            Currency = currency,
            Amount = amount,
            Instructed = instructed,
            ExchangeRate = rate,
            OrderingCustomer = ordering,
            OrderingInstitution = f.Party("52", "AD"),
            SendersCorrespondent = f.Party("53", "ABD"),
            ReceiversCorrespondent = f.Party("54", "ABD"),
            ThirdReimbursementInstitution = f.Party("55", "ABD"),
            Intermediary = f.Party("56", "ACD"),
            AccountWithInstitution = f.Party("57", "ABCD"),
            Beneficiary = beneficiary,
            RemittanceInformation = f.One("70")?.Split('\n') ?? [],
            ChargesCode = charges,
            SendersCharges = senders,
            ReceiversCharges = receivers,
            SenderToReceiverInformation = f.One("72")?.Split('\n') ?? [],
            RegulatoryReporting = f.One("77B")?.Split('\n') ?? [],
            Other = other,
        }, issues);
    }

    private static readonly System.Buffers.SearchValues<char> XAndNewline =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 /-?:().,'+\n");

    private static Regex Uuid4() => new(@"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>YYMMDD; years 00 to 79 are 2000 to 2079, 80 to 99 are 1980 to 1999.</summary>
    internal static bool TryDate(string s, out DateOnly date)
    {
        date = default;
        if (s.Length != 6 || !s.All(char.IsAsciiDigit)) return false;
        var yy = int.Parse(s[..2], CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact((yy < 80 ? "20" : "19") + s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>"USD1000,00": a currency and an amount with a decimal comma, at most 15 characters.</summary>
    internal static bool TryCurrencyAmount(string s, out string currency, out decimal amount, List<MtIssue> issues, string tag, int? line)
    {
        currency = "";
        amount = 0;
        if (s.Length < 5 || !s[..3].All(char.IsAsciiLetterUpper)) { issues.Add(new("MT-AMOUNT", $"Field {tag} should start with a currency code such as USD; it is {SafeText.Quote(s)}.", line)); return false; }
        currency = s[..3];
        var number = s[3..];
        if (number.Length > 15 || !Regex.IsMatch(number, @"^\d+,\d*\z", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            issues.Add(new("MT-AMOUNT", $"The amount in field {tag} should be digits with a decimal comma (1000,00), at most 15 characters; it is {SafeText.Quote(number)}.", line));
            return false;
        }
        if (!decimal.TryParse(number.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            issues.Add(new("MT-AMOUNT", $"The amount in field {tag} isn't a number.", line));
            return false;
        }
        var decimals = number.Length - number.IndexOf(',') - 1;
        if (Iso4217.Decimals(currency) is not int allowed) issues.Add(new("MT-CURRENCY", $"Field {tag}: {currency} isn't an active ISO 4217 currency.", line));
        else if (decimals > allowed) issues.Add(new("MT-AMOUNT", $"Field {tag}: {currency} has {allowed} decimal places, but the amount has {decimals}.", line));
        return true;
    }

    /// <summary>Field access with the checks every field needs.</summary>
    private sealed class Reader(IReadOnlyList<MtField> fields, List<MtIssue> issues)
    {
        public IEnumerable<MtField> All(string tag) => fields.Where(f => f.Tag == tag);

        public int? LineOf(string tag) => fields.FirstOrDefault(f => f.Tag == tag)?.Line;

        public string? One(string tag, bool required = false)
        {
            var found = All(tag).ToList();
            if (found.Count == 0)
            {
                if (required) issues.Add(new("MT-MISSING", $"Field {tag} is required in an MT103."));
                return null;
            }
            if (found.Count > 1) issues.Add(new("MT-REPEATED", $"Field {tag} appears {found.Count} times; it can appear once.", found[1].Line));
            return found[0].Value;
        }

        /// <summary>Field "50", "52"… with one of the allowed option letters.</summary>
        public MtParty? Party(string number, string options, bool required = false, bool allowNoLetter = false)
        {
            var found = fields.Where(f => f.Tag.StartsWith(number, StringComparison.Ordinal) && f.Tag.Length <= 3).ToList();
            if (found.Count == 0)
            {
                if (required) issues.Add(new("MT-MISSING", $"Field {number}a is required in an MT103."));
                return null;
            }
            if (found.Count > 1) issues.Add(new("MT-REPEATED", $"Field {number}a appears {found.Count} times; it can appear once.", found[1].Line));
            var f = found[0];
            var option = f.Tag[number.Length..];
            if (option.Length == 0 ? !allowNoLetter : !options.Contains(option[0]) || option.Length > 1)
            {
                issues.Add(new("MT-OPTION", $"Field {SafeText.Quote(f.Tag)} isn't allowed; field {number} takes option {string.Join(", ", options.Select(c => c.ToString()))}{(allowNoLetter ? " or none" : "")}.", f.Line));
                return null;
            }
            var lines = f.Value.Split('\n').ToList();
            string? account = null;
            // the first line holds an account or party identifier when it starts with '/', or for option F a code/identifier
            if (lines.Count > 0 && lines[0].StartsWith('/') && option != "F")
            {
                account = lines[0].TrimStart('/');
                lines.RemoveAt(0);
            }
            else if (option == "F" && lines.Count > 0 && !Regex.IsMatch(lines[0], "^[1-8]/", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                account = lines[0].TrimStart('/');
                lines.RemoveAt(0);
            }
            string? bic = null;
            if (option == "A")
            {
                bic = lines.FirstOrDefault();
                if (bic is null || !Regex.IsMatch(bic, @"^[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}([A-Z0-9]{3})?\z", RegexOptions.None, TimeSpan.FromSeconds(1)))
                    issues.Add(new("MT-BIC", $"Field {f.Tag} should hold a BIC (8 or 11 letters and digits); it is {SafeText.Quote(bic)}.", f.Line));
                lines = lines.Skip(1).ToList();
                if (lines.Count > 0) issues.Add(new("MT-OPTION", $"Field {f.Tag} has text after its BIC.", f.Line));
            }
            if (option is "K" or "D" or "" or "F" && lines.Count > (option == "F" ? 4 : 4))
                issues.Add(new("MT-LINES", $"Field {f.Tag} has more than 4 lines of name and address.", f.Line));
            if (option is "K" or "D" or "" && lines.Count == 0 && account is null)
                issues.Add(new("MT-OPTION", $"Field {f.Tag} is empty.", f.Line));
            return new MtParty(option, account, bic, lines);
        }
    }
}
