using Iso20022Kit.Messages.Pacs008V08;
using Iso20022Kit.Mt;
using Xunit;

namespace Iso20022Kit.Tests;

public sealed class MtTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Sample = File.ReadAllText(Path.Combine(Root, "samples", "mt", "mt103.fin"));

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "samples", "mt"))) dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("samples/mt");
        return dir;
    }

    private static string Body(string fields) => "{1:F01DEMOKHPPAXXX0000000000}{2:I103DEMOGB2LXXXXN}{4:\r\n" + fields.Replace("\n", "\r\n") + "\r\n-}";

    private const string Minimal = ":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA";

    private static IEnumerable<string> Codes(Mt103.ReadResult r) => r.Issues.Where(i => i.IsError).Select(i => i.Code);

    [Fact]
    public void TheSampleIsAValidMt103()
    {
        var r = Mt103.Read(Sample);
        Assert.True(r.IsValid, string.Join("; ", r.Issues));
        Assert.Empty(r.Issues); // not even a warning
        var m = r.Message!;
        Assert.Equal(("DEMOKHPP", "DEMOGB2L", "e3b6f2a4-5c1d-4e8f-9a7b-1c2d3e4f5a6b"), (m.SenderBic, m.ReceiverBic, m.Uetr));
        Assert.Equal(("INV445-20261104", new DateOnly(2026, 11, 4), "USD", 1000m), (m.Reference, m.ValueDate, m.Currency, m.Amount));
        Assert.Equal(("USD", 1010m), m.Instructed);
        Assert.Equal(("K", "001234567"), (m.OrderingCustomer.Option, m.OrderingCustomer.Account));
        Assert.Equal(["SOKHA CHAN", "NO 12 STREET 51", "PHNOM PENH KH"], m.OrderingCustomer.Lines);
        Assert.Equal("GB82WEST12345698765432", m.Beneficiary.Account);
        Assert.Equal([("USD", 10m)], m.SendersCharges);
    }

    [Fact]
    public void Mt103ToPacs008()
    {
        var t = MtTranslator.ToPacs008(Sample, createdAt: new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
        Assert.True(t.Ok, string.Join("; ", t.InputIssues.Concat(t.OutputIssues)));
        var read = Iso20022.ReadDocument<Document>(t.Output!);
        Assert.True(read.IsValid);
        var g = read.Value!.FiToFiCstmrCdtTrf.GrpHdr;
        var tx = read.Value.FiToFiCstmrCdtTrf.CdtTrfTxInf.Single();
        Assert.Equal(("INV445-20261104", "DEMOKHPP", "DEMOGB2L", SettlementMethod1Code.Inda), (g.MsgId, g.InstgAgt!.FinInstnId.Bicfi, g.InstdAgt!.FinInstnId.Bicfi, g.SttlmInf.SttlmMtd));
        Assert.Equal(("INV445-20261104", "NOTPROVIDED", "e3b6f2a4-5c1d-4e8f-9a7b-1c2d3e4f5a6b"), (tx.PmtId.InstrId, tx.PmtId.EndToEndId, tx.PmtId.Uetr));
        Assert.Equal((1000m, "USD", new DateTime(2026, 11, 4)), (tx.IntrBkSttlmAmt.Value, tx.IntrBkSttlmAmt.Ccy, tx.IntrBkSttlmDt!.Value));
        Assert.Equal(1010m, tx.InstdAmt!.Value);
        Assert.Equal(ChargeBearerType1Code.Shar, tx.ChrgBr);
        Assert.Equal((10m, "DEMOKHPP"), (tx.ChrgsInf.Single().Amt.Value, tx.ChrgsInf.Single().Agt.FinInstnId.Bicfi));
        Assert.Equal(("SOKHA CHAN", "001234567"), (tx.Dbtr.Nm, tx.DbtrAcct!.Id.Othr!.Id));
        Assert.Equal(["NO 12 STREET 51", "PHNOM PENH KH"], tx.Dbtr.PstlAdr!.AdrLine);
        Assert.Equal(("OXFORD BOOKS LTD", "GB82WEST12345698765432"), (tx.Cdtr.Nm, tx.CdtrAcct!.Id.Iban));
        Assert.Equal(("DEMOKHPP", "DEMOGB2L"), (tx.DbtrAgt.FinInstnId.Bicfi, tx.CdtrAgt.FinInstnId.Bicfi));
        Assert.Equal("INVOICE 445", tx.RmtInf!.Ustrd.Single());
        Assert.Equal("PLEASE ADVISE BENEFICIARY", tx.InstrForCdtrAgt.Single().InstrInf);
        Assert.Contains(t.Notes, n => n.Where == "20" && n.Message.Contains("NOTPROVIDED", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoundTripKeepsWhatBothFormatsHold()
    {
        var mx = MtTranslator.ToPacs008(Sample);
        var back = MtTranslator.ToMt103(mx.Output!);
        Assert.True(back.Ok, string.Join("; ", back.InputIssues.Concat(back.OutputIssues)));
        Assert.Contains(":32A:261104USD1000,00\r\n", back.Output, StringComparison.Ordinal);
        Assert.Empty(Mt103.Read(back.Output!).Issues);
        var a = Mt103.Read(Sample).Message!;
        var b = Mt103.Read(back.Output!).Message!;
        Assert.Equal((a.Reference, a.ValueDate, a.Currency, a.Amount, a.Instructed, a.ChargesCode, a.Uetr), (b.Reference, b.ValueDate, b.Currency, b.Amount, b.Instructed, b.ChargesCode, b.Uetr));
        Assert.Equal((a.SenderBic, a.ReceiverBic), (b.SenderBic, b.ReceiverBic));
        Assert.Equal(a.OrderingCustomer.Lines, b.OrderingCustomer.Lines);
        Assert.Equal((a.OrderingCustomer.Account, a.Beneficiary.Account), (b.OrderingCustomer.Account, b.Beneficiary.Account));
        Assert.Equal(a.Beneficiary.Lines, b.Beneficiary.Lines);
        Assert.Equal(a.RemittanceInformation, b.RemittanceInformation);
        Assert.Equal(a.SendersCharges, b.SendersCharges);
        Assert.Equal(a.SenderToReceiverInformation, b.SenderToReceiverInformation);
    }

    [Fact]
    public void Pacs008ToMt103_FromTheConformanceSample()
    {
        var xml = File.ReadAllText(Path.Combine(Root, "conformance", "base", "pacs008-minimal.xml"));
        var t = MtTranslator.ToMt103(xml);
        Assert.True(t.Ok, string.Join("; ", t.InputIssues.Concat(t.OutputIssues)));
        var m = Mt103.Read(t.Output!).Message!;
        var source = Iso20022.ReadCreditTransfer(xml).Value!.Transactions.Single();
        Assert.Equal((source.Amount.Amount, source.Amount.Currency), (m.Amount, m.Currency));
        var two = MtTranslator.ToMt103(File.ReadAllText(Path.Combine(Root, "conformance", "base", "pacs008-two-transactions.xml")));
        Assert.Null(two.Output);
        Assert.Contains(two.InputIssues, i => i.Code == "MX-TRANSACTIONS");
    }

    /// <summary>A pacs.008 built in code, with what MT can't hold as it is.</summary>
    private static string Mx(string creditorName, string? remittance = null, string reference = "REF-1", Action<CreditTransferTransaction39>? change = null)
    {
        var doc = Builders.CreditTransferBuilder.Create("MSG-1")
            .CreatedAt(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero))
            .InstructingAgent("DEMOKHPP").InstructedAgent("DEMOGB2L")
            .AddTransaction(t =>
            {
                t.EndToEndId("E2E-1").Ids(instructionId: reference).Amount(250m, "EUR", new DateOnly(2026, 11, 4))
                 .Debtor("Société Générale d'Équipement", account: "001234567", agentBic: "DEMOKHPP")
                 .Creditor(creditorName, iban: "GB82WEST12345698765432", agentBic: "DEMOGB2L");
                if (remittance is not null) t.Remittance(remittance);
            })
            .Build();
        change?.Invoke(doc.FiToFiCstmrCdtTrf.CdtTrfTxInf[0]);
        return Iso20022.Write(doc);
    }

    [Fact]
    public void TextIsTransliteratedAndTheLossesAreListed()
    {
        var t = MtTranslator.ToMt103(Mx("Zoë & Søn Ltd", remittance: new string('x', 140), reference: "A-VERY-LONG-INSTRUCTION-ID-2026"));
        Assert.True(t.Ok, string.Join("; ", t.OutputIssues));
        var m = Mt103.Read(t.Output!).Message!;
        Assert.Equal("Societe Generale d'Equipement", m.OrderingCustomer.Lines[0]);
        Assert.Equal("Zoe + Son Ltd", m.Beneficiary.Lines[0]);
        Assert.Equal("A-VERY-LONG-INST", m.Reference);
        Assert.Equal(4, m.RemittanceInformation.Count);
        Assert.Contains(t.Notes, n => n.Where == "20" && n.Lost);
        Assert.Contains(t.Notes, n => n.Where == "PmtId/EndToEndId" && n.Lost);
        Assert.Contains(t.Notes, n => n.Where.StartsWith("Cdtr", StringComparison.Ordinal) && n.Message.Contains("replaced", StringComparison.Ordinal));
    }

    [Fact]
    public void AnMxNameCantInjectAnotherMtField()
    {
        // a newline in XML text is legal; written to MT as is, it would start a second beneficiary field
        var t = MtTranslator.ToMt103(Mx("Alice\n:59:/DE89370400440532013000\nMALLORY\n-}{5:"));
        Assert.True(t.Ok, string.Join("; ", t.OutputIssues));
        var text = t.Output!;
        Assert.Equal(1, text.Split("\r\n:59", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, text.Split("-}", StringSplitOptions.None).Length - 1);
        var m = Mt103.Read(text).Message!;
        Assert.Equal("GB82WEST12345698765432", m.Beneficiary.Account);
        Assert.StartsWith("Alice :59:/DE89", m.Beneficiary.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ALineStartingWithAColonOrDashIsDefused()
    {
        var t = MtTranslator.ToMt103(Mx(new string('A', 35) + ":71A:OUR" + new string(' ', 27) + "-}"));
        Assert.True(t.Ok, string.Join("; ", t.OutputIssues));
        Assert.DoesNotContain("\r\n:71A:OUR\r\n:71A", t.Output!, StringComparison.Ordinal);
        Assert.Equal("SHA", Mt103.Read(t.Output!).Message!.ChargesCode);
        Assert.All(t.Output!.Split("\r\n").Where(l => !l.StartsWith(':') && l != "-}" && !l.StartsWith('{')), l => Assert.False(l.StartsWith(':') || l.StartsWith('-'), l));
    }

    [Theory]
    [InlineData(":23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA", "MT-MISSING")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261304USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA", "MT-32A")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100.00\n:50K:PAYER\n:59:PAYEE\n:71A:SHA", "MT-AMOUNT")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104JPY100,5\n:50K:PAYER\n:59:PAYEE\n:71A:SHA", "MT-AMOUNT")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:XYZ", "MT-71A")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:OUR\n:33B:USD100,\n:71F:USD1,", "MT-RULE-71")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:BEN", "MT-RULE-71")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA\n:71F:USD1,", "MT-RULE-33B")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:33B:EUR92,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA", "MT-RULE-36")]
    [InlineData(":20:REF//1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA", "MT-20")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYÉR\n:59:PAYEE\n:71A:SHA", "MT-CHARSET")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50B:PAYER\n:59:PAYEE\n:71A:SHA", "MT-OPTION")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA\n:71A:OUR", "MT-REPEATED")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:52A:NOT A BIC\n:59:PAYEE\n:71A:SHA", "MT-BIC")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:PAYEE\n:71A:SHA\n:99Z:X", "MT-FIELD")]
    [InlineData(":20:REF1\n:23B:CRED\n:32A:261104USD100,\n:50K:PAYER\n:59:" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\n:71A:SHA", "MT-LINE-LENGTH")]
    public void BadMt103sAreRefusedWithAReason(string fields, string code)
    {
        var r = Mt103.Read(Body(fields));
        Assert.False(r.IsValid);
        Assert.Contains(code, Codes(r));
    }

    [Fact]
    public void TheMinimalMt103IsValid_AndHeadersAreChecked()
    {
        Assert.True(Mt103.Read(Body(Minimal)).IsValid);
        Assert.Contains("MT-UETR", Codes(Mt103.Read(Body(Minimal).Replace("{4:", "{3:{121:NOT-A-UUID}}{4:"))));
        Assert.Contains("MT-TYPE", Codes(Mt103.Read(Body(Minimal).Replace("I103", "I202"))));
        Assert.Contains("MT-VARIANT", Codes(Mt103.Read(Body(Minimal).Replace("{4:", "{3:{119:REMIT}}{4:"))));
        // a bare text block is accepted, with a note that the banks are unknown
        var bare = Mt103.Read(Minimal.Replace("\n", "\r\n"));
        Assert.True(bare.IsValid);
        Assert.Contains(bare.Issues, i => i.Code == "MT-HEADER" && !i.IsError);
        Assert.Null(MtTranslator.ToPacs008(Minimal).Output); // no banks: nothing to put in DbtrAgt
    }

    [Theory]
    [InlineData("{1:F01DEMOKHPPAXXX0000000000}{4:\r\n:20:X")]
    [InlineData("{1:F01DEMO{{{{{{{{}")]
    [InlineData("{9:x}{4:\r\n:20:X\r\n-}")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("{4:\r\n-}")]
    public void BrokenMessagesAreReportedNotThrown(string text)
    {
        var r = Mt103.Read(text);
        Assert.False(r.IsValid);
        Assert.NotEmpty(r.Issues);
    }

    [Fact]
    public void HugeInputIsRefusedQuickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("MT-TOO-LONG", Codes(Mt103.Read(new string('{', 5_000_000))));
        Assert.Contains("MT-TOO-LONG", Codes(Mt103.Read(":20:" + new string('A', 30_000))));
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void TenThousandMutatedMessagesNeverThrowAndStayFast()
    {
        var random = new Random(103);
        var nasty = new[] { "{", "}", ":", "\r\n", "\r\n:20:", "\r\n-}", "//", "\u202E", "\u0000", "é", ",", "9999", "{4:" };
        var slowest = 0L;
        for (var i = 0; i < 10_000; i++)
        {
            var s = Sample;
            for (var e = 0; e < 1 + random.Next(4); e++)
            {
                var at = random.Next(s.Length + 1);
                s = random.Next(3) switch
                {
                    0 when s.Length > 0 => s.Remove(Math.Min(at, s.Length - 1), Math.Min(1 + random.Next(5), s.Length - Math.Min(at, s.Length - 1))),
                    0 => s,
                    1 => s.Insert(at, nasty[random.Next(nasty.Length)]),
                    _ => s[..at],
                };
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = Mt103.Read(s);
            if (r.IsValid) _ = MtTranslator.ToPacs008(s);
            slowest = Math.Max(slowest, sw.ElapsedMilliseconds);
            foreach (var issue in r.Issues)
                Assert.False(issue.Message.Any(c => char.IsControl(c) || c == '\u202E'), $"{issue.Code}: {string.Concat(issue.Message.Select(c => char.IsControl(c) || c == '\u202E' ? $"<U+{(int)c:X4}>" : c.ToString()))}");
        }
        Assert.True(slowest < 500, $"slowest {slowest} ms");
    }
}
