using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Iso20022Kit.Internal;
using Iso20022Kit.Messages.Pacs008V08;
using Iso20022Kit.Validation;

namespace Iso20022Kit.Mt;

/// <summary>Something a translation changed, inferred or couldn't carry, with the field or element it concerns.</summary>
/// <param name="Where">The MT field or MX element, e.g. "70" or "RmtInf/Strd".</param>
/// <param name="Message">What happened, in plain English.</param>
/// <param name="Lost">True when information was dropped or shortened; false when it was moved, inferred or converted.</param>
public sealed record TranslationNote(string Where, string Message, bool Lost);

/// <summary>The result of a translation.</summary>
/// <param name="Output">The translated message, or null when the input couldn't be translated.</param>
/// <param name="InputIssues">Problems with the input message.</param>
/// <param name="Notes">What the translation changed, inferred or lost.</param>
/// <param name="OutputIssues">Problems found when the output was checked (there should be none).</param>
public sealed record Translation(string? Output, IReadOnlyList<MtIssue> InputIssues, IReadOnlyList<TranslationNote> Notes, IReadOnlyList<MtIssue> OutputIssues)
{
    /// <summary>True when there is an output and it passed its own checks.</summary>
    public bool Ok => Output is not null && OutputIssues.Count == 0;
}

/// <summary>
/// Translates between MT103 and pacs.008.001.08 (one payment). This is a practical mapping built from the published
/// field formats, not SWIFT's licensed translation rules; every change, inference and loss is listed in
/// <see cref="Translation.Notes"/>, and the output is checked (MX against the official schema and business rules,
/// MT by reading it back).
/// </summary>
public static class MtTranslator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // MT clearing-code prefixes ("//FW021000021") and ISO 20022 clearing system codes
    private static readonly (string Mt, string Mx)[] ClearingCodes =
    [
        ("FW", "USABA"), ("CP", "USPID"), ("SC", "GBDSC"), ("BL", "DEBLZ"), ("AT", "ATBLZ"), ("AU", "AUBSB"), ("CC", "CACPA"),
        ("SW", "CHBCC"), ("HK", "HKNCC"), ("IN", "INFSC"), ("NZ", "NZNCC"), ("IT", "ITNCC"), ("ES", "ESNCC"), ("PT", "PTNCC"),
        ("IE", "IENCC"), ("PL", "PLKNR"), ("RU", "RUCBC"),
    ];

    // ---------------------------------------------------------------- MT103 → pacs.008

    /// <summary>Translates an MT103 into a pacs.008.001.08. <paramref name="messageId"/> defaults to field 20; <paramref name="createdAt"/> to now.</summary>
    public static Translation ToPacs008(string mt103, string? messageId = null, DateTimeOffset? createdAt = null)
    {
        var read = Mt103.Read(mt103);
        var notes = new List<TranslationNote>();
        if (!read.IsValid || read.Message is not { } m) return new(null, read.Issues, notes, []);

        void Note(string where, string message, bool lost = false) => notes.Add(new(where, message, lost));

        var sender = m.SenderBic is null ? null : Bic(m.SenderBic);
        var receiver = m.ReceiverBic is null ? null : Bic(m.ReceiverBic);
        var debtorAgent = Agent(m.OrderingInstitution, "52", Note) ?? sender;
        if (m.OrderingInstitution is null && sender is not null) Note("52a", "No ordering institution: the sender is the debtor's bank (DbtrAgt).");
        var creditorAgent = Agent(m.AccountWithInstitution, "57", Note) ?? receiver;
        if (m.AccountWithInstitution is null && receiver is not null) Note("57a", "No account with institution: the receiver is the creditor's bank (CdtrAgt).");
        var input = new List<MtIssue>(read.Issues);
        if (debtorAgent is null) input.Add(new("MT-AGENT", "The debtor's bank is unknown: there is no field 52a and no header naming the sender."));
        if (creditorAgent is null) input.Add(new("MT-AGENT", "The creditor's bank is unknown: there is no field 57a and no header naming the receiver."));
        if (debtorAgent is null || creditorAgent is null) return new(null, input, notes, []);

        var tx = new CreditTransferTransaction39
        {
            PmtId = new PaymentIdentification7 { InstrId = m.Reference, EndToEndId = "NOTPROVIDED", TxId = m.Reference, Uetr = m.Uetr },
            IntrBkSttlmAmt = new ActiveCurrencyAndAmount { Ccy = m.Currency, Value = m.Amount },
            IntrBkSttlmDt = m.ValueDate.ToDateTime(TimeOnly.MinValue),
            ChrgBr = m.ChargesCode switch { "OUR" => ChargeBearerType1Code.Debt, "BEN" => ChargeBearerType1Code.Cred, _ => ChargeBearerType1Code.Shar },
            Dbtr = Party(m.OrderingCustomer, "50", Note, out var debtorAccount),
            DbtrAcct = debtorAccount,
            DbtrAgt = debtorAgent,
            CdtrAgt = creditorAgent,
            Cdtr = Party(m.Beneficiary, "59", Note, out var creditorAccount),
            CdtrAcct = creditorAccount,
            IntrmyAgt1 = Agent(m.Intermediary, "56", Note),
        };
        Note("20", "Field 20 becomes the instruction and transaction IDs; an MT103 has no end-to-end ID, so EndToEndId is NOTPROVIDED.");
        if (m.Uetr is null) Note("121", "There is no UETR in the user header; CBPR+ requires one, and none was made up.", lost: true);
        if (m.BankOperationCode != "CRED") Note("23B", $"Bank operation code {m.BankOperationCode} has no equivalent in pacs.008 and wasn't carried.", lost: true);
        if (m.Instructed is { } ins) tx.InstdAmt = new ActiveOrHistoricCurrencyAndAmount { Ccy = ins.Currency, Value = ins.Amount };
        if (m.ExchangeRate is decimal rate) tx.XchgRate = rate;

        foreach (var (ccy, amount) in m.SendersCharges)
            tx.ChrgsInf.Add(new Charges7 { Amt = new ActiveOrHistoricCurrencyAndAmount { Ccy = ccy, Value = amount }, Agt = sender ?? debtorAgent });
        if (m.ReceiversCharges is { } rc)
        {
            tx.ChrgsInf.Add(new Charges7 { Amt = new ActiveOrHistoricCurrencyAndAmount { Ccy = rc.Currency, Value = rc.Amount }, Agt = receiver ?? creditorAgent });
            Note("71G", "The receiver's charges become a charges entry for the receiving bank (ChrgsInf).");
        }

        foreach (var code in m.InstructionCodes)
        {
            var (c, text) = code.Length > 4 ? (code[..4], code[5..]) : (code, null);
            switch (c)
            {
                case "CHQB" or "HOLD" or "PHOB" or "TELB":
                    tx.InstrForCdtrAgt.Add(new InstructionForCreditorAgent1 { Cd = Enum.Parse<Instruction3Code>(c, ignoreCase: true), InstrInf = text });
                    break;
                case "PHON" or "TELE":
                    tx.InstrForNxtAgt.Add(new InstructionForNextAgent1 { Cd = c == "PHON" ? Instruction4Code.Phoa : Instruction4Code.Tela, InstrInf = text });
                    break;
                case "SDVA":
                    (tx.PmtTpInf ??= new PaymentTypeInformation28()).SvcLvl.Add(new ServiceLevel8Choice { Cd = "SDVA" });
                    break;
                case "INTC" or "CORT":
                    (tx.PmtTpInf ??= new PaymentTypeInformation28()).CtgyPurp = new CategoryPurpose1Choice { Cd = c };
                    break;
                default:
                    tx.InstrForNxtAgt.Add(new InstructionForNextAgent1 { InstrInf = Cut("/" + code + "/", 140) });
                    Note("23E", $"Instruction code {code[..Math.Min(4, code.Length)]} has no coded equivalent; it is carried as text for the next bank.");
                    break;
            }
        }

        if (m.RemittanceInformation.Count > 0)
        {
            var rmt = new RemittanceInformation16();
            rmt.Ustrd.Add(Cut(string.Concat(m.RemittanceInformation), 140));
            tx.RmtInf = rmt;
            if (m.RemittanceInformation.Count > 1) Note("70", "The remittance lines are joined into one unstructured line (MT wraps lines at 35 characters).");
        }

        foreach (var line in Coded(m.SenderToReceiverInformation))
        {
            if (line.StartsWith("/ACC/", StringComparison.Ordinal))
                tx.InstrForCdtrAgt.Add(new InstructionForCreditorAgent1 { InstrInf = Cut(line[5..], 140) });
            else
                tx.InstrForNxtAgt.Add(new InstructionForNextAgent1 { InstrInf = Cut(line, 140) });
        }
        if (m.SenderToReceiverInformation.Count > 0)
            Note("72", "Sender-to-receiver information is carried as text instructions (/ACC/ for the creditor's bank, the rest for the next bank); structured codes such as /INS/ aren't interpreted.");

        if (m.RegulatoryReporting.Count > 0)
        {
            var details = new StructuredRegulatoryReporting3();
            foreach (var line in m.RegulatoryReporting) details.Inf.Add(line);
            var reporting = new RegulatoryReporting3();
            reporting.Dtls.Add(details);
            tx.RgltryRptg.Add(reporting);
        }

        var settlement = new SettlementInstruction7 { SttlmMtd = SettlementMethod1Code.Inda };
        if (m.SendersCorrespondent is not null || m.ReceiversCorrespondent is not null || m.ThirdReimbursementInstitution is not null)
        {
            settlement.SttlmMtd = SettlementMethod1Code.Cove;
            settlement.InstgRmbrsmntAgt = Agent(m.SendersCorrespondent, "53", Note);
            settlement.InstdRmbrsmntAgt = Agent(m.ReceiversCorrespondent, "54", Note);
            settlement.ThrdRmbrsmntAgt = Agent(m.ThirdReimbursementInstitution, "55", Note);
            Note("53a/54a", "With correspondent banks named, the settlement method is taken to be COVE (a separate cover payment).");
        }
        else Note("SttlmMtd", "No correspondents are named, so the settlement method is taken to be INDA (the receiver debits the sender's account).");

        foreach (var other in m.Other) Note(other.Tag, $"Field {other.Tag} isn't translated.", lost: true);

        var header = new GroupHeader93
        {
            MsgId = Cut(messageId ?? m.Reference, 35),
            CreDtTm = (createdAt ?? DateTimeOffset.UtcNow).UtcDateTime,
            NbOfTxs = "1",
            SttlmInf = settlement,
            InstgAgt = sender,
            InstdAgt = receiver,
        };
        var body = new FiToFiCustomerCreditTransferV08 { GrpHdr = header };
        body.CdtTrfTxInf.Add(tx);
        string xml;
        try
        {
            xml = Iso20022.Write(new Document { FiToFiCstmrCdtTrf = body }, validate: false);
        }
        catch (ArgumentException e)
        {
            // never throw for bad input: a value that can't be XML is reported like any other problem
            return new(null, read.Issues, notes, [new MtIssue("MX-WRITE", SafeText.Clean(e.Message) ?? "The message couldn't be written as XML.")]);
        }
        var report = Iso20022.Validate(xml);
        var outputIssues = report.Errors.Select(e => new MtIssue("MX-" + e.Code, e.Message)).ToList();
        return new(report.IsValid ? xml : null, read.Issues, notes, outputIssues);
    }

    private static BranchAndFinancialInstitutionIdentification6 Bic(string bic) => new() { FinInstnId = new FinancialInstitutionIdentification18 { Bicfi = bic } };

    private static BranchAndFinancialInstitutionIdentification6? Agent(MtParty? p, string tag, Action<string, string, bool> note)
    {
        if (p is null) return null;
        var fi = new FinancialInstitutionIdentification18();
        if (p.Bic is not null) fi.Bicfi = p.Bic;
        // "//FW021000021": a national clearing code; "/C/12345" or "/12345": an account at the bank, which pacs.008 has no place for here
        if (p.Account is { } acct)
        {
            var clearing = acct.StartsWith('/') ? ClearingCodes.FirstOrDefault(c => acct.Length > 3 && acct[1..3] == c.Mt) : default;
            if (clearing.Mt is not null)
                fi.ClrSysMmbId = new ClearingSystemMemberIdentification2 { ClrSysId = new ClearingSystemIdentification2Choice { Cd = clearing.Mx }, MmbId = acct[3..] };
            else note(tag + p.Option, $"The account or code {SafeText.Quote(acct)} on the bank's line isn't carried.", true);
        }
        if (p.Lines.Count > 0 && p.Bic is null)
        {
            fi.Nm = Cut(p.Lines[0], 140);
            if (p.Lines.Count > 1)
            {
                fi.PstlAdr = new PostalAddress24();
                foreach (var l in p.Lines.Skip(1)) fi.PstlAdr.AdrLine.Add(l);
            }
        }
        if (fi.Bicfi is null && fi.Nm is null && fi.ClrSysMmbId is null) return null;
        return new BranchAndFinancialInstitutionIdentification6 { FinInstnId = fi };
    }

    private static PartyIdentification135 Party(MtParty p, string tag, Action<string, string, bool> note, out CashAccount38? account)
    {
        account = p.Account is { Length: > 0 } a ? Account(a) : null;
        var party = new PartyIdentification135();
        if (p.Bic is not null)
            party.Id = new Party38Choice { OrgId = new OrganisationIdentification29 { AnyBic = p.Bic } };
        if (p.Option == "F")
        {
            // numbered lines: 1/ name, 2/ address, 3/ country/town, 4/ to 8/ identification details
            var name = new StringBuilder();
            var address = new List<string>();
            foreach (var line in p.Lines)
            {
                if (line.Length < 2 || line[1] != '/') continue;
                var text = line[2..];
                switch (line[0])
                {
                    case '1': name.Append(name.Length > 0 ? " " : "").Append(text); break;
                    case '2': address.Add(text); break;
                    case '3':
                        var slash = text.IndexOf('/');
                        party.PstlAdr ??= new PostalAddress24();
                        if (slash == 2) { party.PstlAdr.Ctry = text[..2]; if (text.Length > 3) party.PstlAdr.TwnNm = Cut(text[3..], 35); }
                        else address.Add(text);
                        break;
                    default: note(tag + "F", $"Line {line[0]}/ (identification details) isn't carried.", true); break;
                }
            }
            if (name.Length > 0) party.Nm = Cut(name.ToString(), 140);
            if (address.Count > 0) foreach (var l in address) (party.PstlAdr ??= new PostalAddress24()).AdrLine.Add(l);
        }
        else if (p.Lines.Count > 0)
        {
            party.Nm = Cut(p.Lines[0], 140);
            if (p.Lines.Count > 1)
            {
                party.PstlAdr = new PostalAddress24();
                foreach (var l in p.Lines.Skip(1)) party.PstlAdr.AdrLine.Add(l);
                note(tag + p.Option, "The lines after the name become unstructured address lines (AdrLine); MT doesn't say which is the street, town or country.", false);
            }
        }
        return party;
    }

    private static CashAccount38 Account(string a)
    {
        var compact = a.Replace(" ", "", StringComparison.Ordinal);
        var iban = Regex.IsMatch(compact, @"^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) && Validation.Rules.Iban.HasValidCheckDigits(compact);
        return new CashAccount38 { Id = iban ? new AccountIdentification4Choice { Iban = compact } : new AccountIdentification4Choice { Othr = new GenericAccountIdentification1 { Id = Cut(a, 34) } } };
    }

    /// <summary>Field 72's lines as codes: "/INS/text" lines, with "//continuation" lines joined to the line before.</summary>
    private static List<string> Coded(IReadOnlyList<string> lines)
    {
        var result = new List<string>();
        foreach (var l in lines)
        {
            if (l.StartsWith("//", StringComparison.Ordinal) && result.Count > 0) result[^1] += " " + l[2..];
            else result.Add(l);
        }
        return result;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------------------------------------------------------------- pacs.008 → MT103

    /// <summary>Translates a pacs.008.001.08 holding one payment into an MT103.</summary>
    public static Translation ToMt103(string pacs008Xml)
    {
        var notes = new List<TranslationNote>();
        var read = Iso20022.ReadDocument<Document>(pacs008Xml ?? "");
        if (!read.IsValid || read.Value is not { } doc)
            return new(null, read.Report.Issues.Where(i => i.Severity == Severity.Error).Select(i => new MtIssue("MX-" + i.Code, i.Message, i.Line)).ToList(), notes, []);
        var msg = doc.FiToFiCstmrCdtTrf;
        if (msg.CdtTrfTxInf.Count != 1)
            return new(null, [new MtIssue("MX-TRANSACTIONS", $"The message holds {msg.CdtTrfTxInf.Count} payments; an MT103 holds one.")], notes, []);
        var tx = msg.CdtTrfTxInf[0];
        void Note(string where, string message, bool lost = false) => notes.Add(new(where, message, lost));

        var fields = new List<(string Tag, List<string> Lines)>();
        void Field(string tag, params string[] lines) => fields.Add((tag, lines.ToList()));

        // 20: at most 16 characters, no leading or trailing '/', no '//'
        var reference = tx.PmtId.InstrId ?? tx.PmtId.TxId ?? msg.GrpHdr.MsgId;
        var ref20 = X(reference, "20", Note).Replace("//", "/", StringComparison.Ordinal).Trim('/');
        if (ref20.Length > 16) { Note("20", $"The reference {SafeText.Quote(reference)} is cut to 16 characters.", true); ref20 = ref20[..16].TrimEnd('/'); }
        if (ref20.Length == 0) ref20 = "NONREF";
        Field("20", ref20);
        if (tx.PmtId.EndToEndId is { } e2e && e2e != "NOTPROVIDED" && e2e != reference)
            Note("PmtId/EndToEndId", $"The end-to-end ID {SafeText.Quote(e2e)} has no field in an MT103 and isn't carried.", true);
        Field("23B", "CRED");

        foreach (var i in tx.InstrForCdtrAgt.Where(i => i.Cd is not null)) Field("23E", i.Cd!.Value.ToString().ToUpperInvariant() + (i.InstrInf is { } t ? "/" + Fit(X(t, "InstrForCdtrAgt", Note), 30, "InstrForCdtrAgt", Note) : ""));
        foreach (var i in tx.InstrForNxtAgt.Where(i => i.Cd is not null)) Field("23E", (i.Cd == Instruction4Code.Phoa ? "PHON" : "TELE") + (i.InstrInf is { } t ? "/" + Fit(X(t, "InstrForNxtAgt", Note), 30, "InstrForNxtAgt", Note) : ""));

        var date = tx.IntrBkSttlmDt ?? msg.GrpHdr.IntrBkSttlmDt;
        if (date is null) return new(null, [new MtIssue("MX-DATE", "There is no interbank settlement date (IntrBkSttlmDt); field 32A needs one.")], notes, []);
        Field("32A", date.Value.ToString("yyMMdd", Inv) + tx.IntrBkSttlmAmt.Ccy + Amount(tx.IntrBkSttlmAmt.Value, tx.IntrBkSttlmAmt.Ccy));

        var instructed = tx.InstdAmt;
        if (instructed is not null) Field("33B", instructed.Ccy + Amount(instructed.Value, instructed.Ccy));
        if (tx.XchgRate is decimal rate)
        {
            if (instructed is not null && instructed.Ccy != tx.IntrBkSttlmAmt.Ccy) Field("36", Amount(rate));
            else Note("XchgRate", "The exchange rate isn't carried: MT103 allows field 36 only when the instructed and settled currencies differ.", true);
        }

        fields.AddRange(PartyField("50", tx.Dbtr, tx.DbtrAcct, "Dbtr", Note));
        AgentField("52", tx.DbtrAgt, msg.GrpHdr.InstgAgt, "DbtrAgt", fields, Note);
        if (msg.GrpHdr.SttlmInf.SttlmMtd == SettlementMethod1Code.Cove)
        {
            AgentField("53", msg.GrpHdr.SttlmInf.InstgRmbrsmntAgt, null, "InstgRmbrsmntAgt", fields, Note);
            AgentField("54", msg.GrpHdr.SttlmInf.InstdRmbrsmntAgt, null, "InstdRmbrsmntAgt", fields, Note);
            AgentField("55", msg.GrpHdr.SttlmInf.ThrdRmbrsmntAgt, null, "ThrdRmbrsmntAgt", fields, Note);
        }
        AgentField("56", tx.IntrmyAgt1, null, "IntrmyAgt1", fields, Note);
        if (tx.IntrmyAgt2 is not null || tx.IntrmyAgt3 is not null) Note("IntrmyAgt2", "An MT103 names one intermediary bank; the second and third aren't carried.", true);
        AgentField("57", tx.CdtrAgt, msg.GrpHdr.InstdAgt, "CdtrAgt", fields, Note);
        fields.AddRange(PartyField("59", tx.Cdtr, tx.CdtrAcct, "Cdtr", Note));

        if (tx.RmtInf is { } rmt)
        {
            if (rmt.Ustrd.Count > 0) Field("70", Lines(X(string.Join(" ", rmt.Ustrd), "RmtInf", Note), 4, "70", Note).ToArray());
            if (rmt.Strd.Count > 0) Note("RmtInf/Strd", "Structured remittance information (invoice references, amounts) has no place in an MT103 and isn't carried.", true);
        }

        Field("71A", tx.ChrgBr switch { ChargeBearerType1Code.Debt => "OUR", ChargeBearerType1Code.Cred => "BEN", _ => "SHA" });
        if (tx.ChrgBr == ChargeBearerType1Code.Slev) Note("ChrgBr", "SLEV (charges as agreed in the service level) becomes SHA.");
        if (tx.ChrgsInf.Count > 0)
        {
            if (instructed is null) Note("ChrgsInf", "Charges can't be carried without an instructed amount (field 33B), so they aren't.", true);
            else if (tx.ChrgBr == ChargeBearerType1Code.Debt)
            {
                var ours = tx.ChrgsInf.GroupBy(c => c.Amt.Ccy).ToList();
                if (ours.Count == 1) Field("71G", ours[0].Key + Amount(ours[0].Sum(c => c.Amt.Value), ours[0].Key));
                else Note("ChrgsInf", "Charges in several currencies can't be one field 71G, so they aren't carried.", true);
            }
            else foreach (var c in tx.ChrgsInf) Field("71F", c.Amt.Ccy + Amount(c.Amt.Value, c.Amt.Ccy));
        }

        var info72 = new List<string>();
        foreach (var i in tx.InstrForCdtrAgt.Where(i => i.Cd is null && i.InstrInf is not null)) info72.Add("/ACC/" + X(i.InstrInf!, "InstrForCdtrAgt", Note));
        foreach (var i in tx.InstrForNxtAgt.Where(i => i.Cd is null && i.InstrInf is not null))
            info72.Add(i.InstrInf!.StartsWith('/') ? X(i.InstrInf, "InstrForNxtAgt", Note) : "/REC/" + X(i.InstrInf, "InstrForNxtAgt", Note));
        if (info72.Count > 0)
        {
            var lines = new List<string>();
            foreach (var text in info72)
            {
                var first = true;
                foreach (var chunk in Chunks(text, 35, 33))
                {
                    lines.Add(first ? chunk : "//" + chunk);
                    first = false;
                }
            }
            if (lines.Count > 6) { Note("72", "Instructions longer than six lines of field 72 are cut.", true); lines = lines.Take(6).ToList(); }
            fields.Add(("72", lines.Select(SafeLine).ToList()));
        }

        var regulatory = tx.RgltryRptg.SelectMany(r => r.Dtls).SelectMany(d => d.Inf.Concat(d.Cd is null ? [] : new[] { d.Cd })).ToList();
        if (regulatory.Count > 0) Field("77B", Lines(X(string.Join(" ", regulatory), "RgltryRptg", Note), 3, "77B", Note).ToArray());

        if (tx.UltmtDbtr is not null) Note("UltmtDbtr", "The ultimate debtor has no field in an MT103 and isn't carried.", true);
        if (tx.UltmtCdtr is not null) Note("UltmtCdtr", "The ultimate creditor has no field in an MT103 and isn't carried.", true);
        if (tx.InitgPty is not null) Note("InitgPty", "The initiating party isn't carried.", true);
        if (tx.Purp is not null) Note("Purp", "The purpose code isn't carried.", true);
        if (tx.PmtTpInf is not null) Note("PmtTpInf", "Payment type information (service level, local instrument, category purpose) isn't carried.", true);
        if (tx.Tax is not null || tx.RltdRmtInf.Count > 0) Note("Tax/RltdRmtInf", "Tax and related remittance information aren't carried.", true);

        // the headers: logical terminal addresses are the BIC8, a terminal code and the branch
        var sb = new StringBuilder();
        if (msg.GrpHdr.InstgAgt?.FinInstnId.Bicfi is string from && msg.GrpHdr.InstdAgt?.FinInstnId.Bicfi is string to)
        {
            sb.Append("{1:F01").Append(Lt(from)).Append("0000000000}{2:I103").Append(Lt(to)).Append("N}");
            if (tx.PmtId.Uetr is { } uetr) sb.Append("{3:{121:").Append(uetr).Append("}}");
        }
        else
        {
            Note("InstgAgt", "The sending or receiving bank has no BIC, so the message has only its text block (no headers).", true);
            if (tx.PmtId.Uetr is not null) Note("UETR", "Without headers there is no user header for the UETR (field 121), so it isn't carried.", true);
        }
        sb.Append("{4:\r\n");
        foreach (var (tag, lines) in fields)
        {
            sb.Append(':').Append(tag).Append(':').Append(lines.Count > 0 ? lines[0] : "").Append("\r\n");
            foreach (var l in lines.Skip(1)) sb.Append(l).Append("\r\n");
        }
        sb.Append("-}");
        var mt = sb.ToString();

        // read it back: what we wrote must be a valid MT103
        var check = Mt103.Read(mt);
        var outputIssues = check.Issues.Where(i => i.IsError).ToList();
        return new(outputIssues.Count == 0 ? mt : null, [], notes, outputIssues);
    }

    private static string Lt(string bic) => bic[..8] + "X" + (bic.Length == 11 ? bic[8..] : "XXX");

    /// <summary>An MT amount: a decimal comma, with the currency's decimals when it has any ("1000,00", "5000," for JPY).</summary>
    private static string Amount(decimal d, string? currency = null)
    {
        var decimals = currency is null ? null : Validation.Rules.Iso4217.Decimals(currency);
        var s = (decimals is int n && n > 0 && decimal.Round(d, n) == d ? d.ToString("F" + n, Inv) : d.ToString("0.############", Inv)).Replace('.', ',');
        return s.Contains(',') ? s : s + ",";
    }

    /// <summary>Text in the X character set, on one line: line breaks become spaces, so text can never start a new field.</summary>
    private static string X(string s, string where, Action<string, string, bool> note)
    {
        var x = XCharset.Transliterate(s, out var replaced);
        if (replaced > 0) note(where, $"{replaced} character{(replaced == 1 ? "" : "s")} outside the SWIFT character set {(replaced == 1 ? "was" : "were")} replaced (accents removed, others by substitutes or '.').", true);
        return x.Trim();
    }

    private static string Fit(string s, int max, string where, Action<string, string, bool> note)
    {
        if (s.Length <= max) return s;
        note(where, $"Text is cut to {max} characters.", true);
        return s[..max];
    }

    /// <summary>A line can't start with ':' (a new field) or '-' (the end of the text block).</summary>
    private static string SafeLine(string line) => line.Length > 0 && line[0] is ':' or '-' ? "." + line[..Math.Min(line.Length, 34)] : line;

    private static IEnumerable<string> Chunks(string s, int first, int rest)
    {
        var size = first;
        for (var i = 0; i < s.Length;)
        {
            var take = Math.Min(size, s.Length - i);
            yield return s.Substring(i, take);
            i += take;
            size = rest;
        }
    }

    private static List<string> Lines(string text, int max, string tag, Action<string, string, bool> note)
    {
        var lines = Chunks(text, 35, 35).Select(SafeLine).ToList();
        if (lines.Count > max) { note(tag, $"The text is longer than field {tag}'s {max} lines of 35 characters and is cut.", true); lines = lines.Take(max).ToList(); }
        return lines;
    }

    private static IEnumerable<(string, List<string>)> PartyField(string number, PartyIdentification135? party, CashAccount38? account, string where, Action<string, string, bool> note)
    {
        var lines = new List<string>();
        var acct = account?.Id.Iban ?? account?.Id.Othr?.Id;
        if (acct is not null) lines.Add("/" + Fit(X(acct, where + "Acct", note), 34, where + "Acct", note));
        var bic = party?.Id?.OrgId?.AnyBic;
        if (party?.Nm is null && bic is not null)
        {
            lines.Add(bic);
            yield return (number + "A", lines);
            yield break;
        }
        var nameAndAddress = new List<string>();
        if (party?.Nm is { } nm) nameAndAddress.Add(X(nm, where + "/Nm", note));
        if (party?.PstlAdr is { } a)
        {
            foreach (var l in a.AdrLine) nameAndAddress.Add(X(l, where + "/PstlAdr", note));
            var street = string.Join(" ", new[] { a.StrtNm, a.BldgNb }.OfType<string>());
            if (street.Length > 0) nameAndAddress.Add(X(street, where + "/PstlAdr", note));
            var town = string.Join(" ", new[] { a.PstCd, a.TwnNm }.OfType<string>());
            if (town.Length > 0) nameAndAddress.Add(X(town, where + "/PstlAdr", note));
            if (a.Ctry is not null) nameAndAddress.Add(a.Ctry);
        }
        if (bic is not null) note(where + "/Id", "The party's BIC is dropped because the name is carried instead.", true);
        var wrapped = nameAndAddress.SelectMany(l => Chunks(l, 35, 35)).Select(SafeLine).ToList();
        if (wrapped.Count > 4) { note(where, $"The name and address don't fit 4 lines of 35 characters and are cut.", true); wrapped = wrapped.Take(4).ToList(); }
        if (wrapped.Count == 0) wrapped.Add("NOTPROVIDED");
        lines.AddRange(wrapped);
        yield return (number == "59" ? "59" : number + "K", lines);
    }

    private static void AgentField(string number, BranchAndFinancialInstitutionIdentification6? agent, BranchAndFinancialInstitutionIdentification6? sameAsHeader, string where, List<(string, List<string>)> fields, Action<string, string, bool> note)
    {
        if (agent is null) return;
        var fi = agent.FinInstnId;
        var lines = new List<string>();
        if (fi.ClrSysMmbId is { } member)
        {
            var code = member.ClrSysId?.Cd;
            var prefix = ClearingCodes.FirstOrDefault(c => c.Mx == code).Mt;
            if (prefix is not null) lines.Add("//" + prefix + Fit(X(member.MmbId, where, note), 31, where, note));
            else note(where + "/ClrSysMmbId", $"Clearing system {SafeText.Quote(code ?? "(proprietary)")} has no MT code, so the member ID isn't carried.", true);
        }
        if (fi.Bicfi is not null)
        {
            if (sameAsHeader?.FinInstnId.Bicfi == fi.Bicfi && lines.Count == 0) return; // the header already says it
            lines.Add(fi.Bicfi);
            fields.Add((number + "A", lines));
            return;
        }
        if (fi.Nm is { } nm) lines.AddRange(Chunks(X(nm, where + "/Nm", note), 35, 35).Select(SafeLine).Take(4 - (lines.Count > 0 ? 0 : 0)));
        if (fi.PstlAdr is { } a) foreach (var l in a.AdrLine) lines.Add(SafeLine(Fit(X(l, where + "/PstlAdr", note), 35, where, note)));
        var account = lines.Where(l => l.StartsWith("//", StringComparison.Ordinal)).ToList();
        var text = lines.Except(account).Take(4).ToList();
        if (lines.Count - account.Count > 4) note(where, "The bank's name and address don't fit 4 lines and are cut.", true);
        if (text.Count == 0 && account.Count == 0) return;
        if (text.Count == 0) { note(where, "The bank has no BIC or name; only its clearing code is carried.", false); text.Add("NOTPROVIDED"); }
        fields.Add((number + "D", account.Concat(text).ToList()));
    }
}
