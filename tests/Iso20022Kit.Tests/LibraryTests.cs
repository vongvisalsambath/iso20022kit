using System.Xml.Linq;
using Iso20022Kit.Builders;
using Iso20022Kit.Validation;
using Iso20022Kit.Validation.Rules;

namespace Iso20022Kit.Tests;

public class LibraryTests
{
    private static string Base(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "base", name));

    [Fact]
    public void MessageIdParsesIdsAndNamespaces()
    {
        Assert.Equal("pacs.008.001.08", MessageId.Parse("urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08").ToString());
        Assert.Equal("pacs.008", MessageId.Pacs008V08.Family);
        Assert.False(MessageId.TryParse("pacs.8.1.8", out _));
        Assert.Throws<FormatException>(() => MessageId.Parse("nope"));
    }

    [Theory]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("GB82 WEST 1234 5698 7654 32", true)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("gb82west12345698765432", true)]
    [InlineData("GB83WEST12345698765432", false)]
    [InlineData("GB82WEST1234569876543", false)]
    [InlineData("GB8", false)]
    [InlineData("GB82WEST1234569876543$", false)]
    public void IbanCheckDigits(string iban, bool valid) => Assert.Equal(valid, Iban.HasValidCheckDigits(iban));

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("KHR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("CLF", 4)]
    public void CurrencyDecimals(string code, int decimals) => Assert.Equal(decimals, Iso4217.Decimals(code));

    [Fact]
    public void UnknownCurrencyHasNoDecimals() => Assert.Null(Iso4217.Decimals("XAU"));

    [Fact]
    public void DetectsSupportedAndUnsupportedMessages()
    {
        Assert.Equal(MessageId.Camt053V08, Iso20022.Detect(Base("camt053-eur-iban.xml")));
        Assert.Equal("pacs.008.001.99", Iso20022.Detect(Base("pacs008-minimal.xml").Replace("pacs.008.001.08", "pacs.008.001.99"))?.ToString());
        Assert.Null(Iso20022.Detect("<x/>"));
        Assert.Null(Iso20022.Detect("not xml"));
    }

    [Fact]
    public void ReadsACreditTransfer()
    {
        var result = Iso20022.ReadCreditTransfer(Base("pacs008-two-transactions.xml"));
        Assert.True(result.IsValid, result.Report.ToString());
        var ct = result.Value!;
        Assert.Equal("TSTA20261104-0001", ct.MessageId);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(7)), ct.CreatedAt);
        Assert.Equal(2, ct.NumberOfTransactions);
        Assert.Equal("INDA", ct.SettlementMethod);
        Assert.Equal(1500.25m, ct.ControlSum);
        Assert.Equal("TSTAKHPPXXX", ct.InstructingAgent?.Bic);

        var first = ct.Transactions[0];
        Assert.Equal("INV-445", first.EndToEndId);
        Assert.Equal(1000.00m, first.Amount.Amount);
        Assert.Equal("USD", first.Amount.Currency);
        Assert.Equal(new DateOnly(2026, 11, 4), first.SettlementDate); // from the group header
        Assert.Equal("Sokha Chan", first.Debtor.Name);
        Assert.Equal("KH", first.Debtor.Country);
        Assert.Equal("001234567", first.DebtorAccount?.Number);
        Assert.Equal("DEMOUS33", first.CreditorAgent.Bic);
        Assert.Equal(["Main Street 100", "10001 New York"], first.Creditor.AddressLines);
        Assert.Equal(["Invoice 445"], first.RemittanceInformation);

        var second = ct.Transactions[1];
        Assert.Equal("GB82WEST12345698765432", second.CreditorAccount?.Iban);
        Assert.Equal("GDDS", second.Purpose);
        Assert.Equal("DEBT", second.ChargeBearer);
    }

    [Fact]
    public void ReadsAClearingMemberAgent()
    {
        var tx = Iso20022.ReadCreditTransfer(Base("pacs008-minimal.xml")).Value!.Transactions[0];
        Assert.Null(tx.DebtorAgent.Bic);
        Assert.Equal("USABA", tx.DebtorAgent.ClearingSystem);
        Assert.Equal("000000001", tx.DebtorAgent.ClearingMemberId);
        Assert.Equal("USABA:000000001", tx.DebtorAgent.Display);
        Assert.Equal(new DateOnly(2026, 11, 5), tx.SettlementDate);
    }

    [Fact]
    public void ReadsAStatement()
    {
        var result = Iso20022.ReadBankStatement(Base("camt053-eur-iban.xml"));
        Assert.True(result.IsValid, result.Report.ToString());
        var s = Assert.Single(result.Value!.Statements);
        Assert.Equal("DE89370400440532013000", s.Account.Iban);
        Assert.Equal("Berlin Bikes GmbH", s.Owner?.Name);
        Assert.Equal("DEMODEFF", s.Servicer?.Bic);
        Assert.Equal(10000.00m, s.Opening?.Amount.Amount);
        Assert.Equal(11200.00m, s.Closing?.Amount.Amount);
        Assert.Equal(3, s.Entries.Count);
        Assert.True(s.Entries[0].IsCredit);
        Assert.Equal("Max Mustermann", s.Entries[0].Counterparty);
        Assert.Equal("ORDER-7781", s.Entries[0].EndToEndId);
        Assert.Equal(-250.50m, s.Entries[1].Amount.Amount);
        Assert.Equal("Spree Properties KG", s.Entries[1].Counterparty);
        Assert.Equal(["Rent November"], s.Entries[1].RemittanceInformation);
        Assert.Equal("Monthly account fee", s.Entries[2].Description);
    }

    [Fact]
    public void OverdraftBalancesAreNegative()
    {
        var s = Iso20022.ReadBankStatement(Base("camt053-usd-overdraft.xml")).Value!.Statements[0];
        Assert.Equal(-200.00m, s.Opening?.Amount.Amount);
        Assert.Equal("PDNG", s.Entries[1].Status);
    }

    [Fact]
    public void ReadingTheWrongTypeSaysSo()
    {
        var result = Iso20022.ReadBankStatement(Base("pacs008-minimal.xml"));
        Assert.Null(result.Value);
        Assert.Equal("MSG-WRONG-TYPE", Assert.Single(result.Report.Issues).Code);
        Assert.Throws<Iso20022ValidationException>(() => result.ValueOrThrow());
    }

    [Fact]
    public void ABusinessErrorStillGivesTheValue()
    {
        var result = Iso20022.ReadCreditTransfer(Base("pacs008-two-transactions.xml").Replace("<NbOfTxs>2</NbOfTxs>", "<NbOfTxs>3</NbOfTxs>"));
        Assert.NotNull(result.Value);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ReadsTheGeneratedType()
    {
        var result = Iso20022.ReadDocument<Messages.Pacs008V08.Document>(Base("pacs008-two-transactions.xml"));
        Assert.True(result.IsValid, result.Report.ToString());
        var tx = result.Value!.FiToFiCstmrCdtTrf.CdtTrfTxInf[1];
        Assert.Equal(500.25m, tx.IntrBkSttlmAmt.Value);
        Assert.Equal("GB82WEST12345698765432", tx.CdtrAcct.Id.Iban);
    }

    [Fact]
    public void BuildsAValidMessageAndReadsItBack()
    {
        var xml = CreditTransferBuilder.Create("MSG-20261104-001")
            .CreatedAt(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(7)))
            .Settlement(SettlementMethod.ClearingSystem, "FDW")
            .InstructingAgent("TSTAKHPP")
            .AddTransaction(t => t
                .EndToEndId("INV-445")
                .Amount(1000.00m, "USD", new DateOnly(2026, 11, 4))
                .Charges(ChargeBearer.Debtor)
                .Debtor("Sokha Chan", account: "001234567", agentBic: "TSTAKHPP", country: "KH")
                .Creditor("Acme Supplies Inc.", account: "987654321", clearingSystem: "USABA", clearingMemberId: "000000001")
                .Remittance("Invoice 445"))
            .AddTransaction(t => t
                .EndToEndId("INV-446")
                .Uetr(null)
                .Amount(0.5m, "USD")
                .Debtor("Sokha Chan", agentBic: "TSTAKHPP")
                .Creditor("Oxford Books Ltd", iban: "GB82WEST12345698765432", agentBic: "DEMOGB2L"))
            .ToXml();

        Assert.Contains("<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08\">", xml);
        Assert.Contains("<CreDtTm>2026-11-04T02:00:00Z</CreDtTm>", xml);
        Assert.Contains("<IntrBkSttlmDt>2026-11-04</IntrBkSttlmDt>", xml);
        Assert.DoesNotContain("xmlns:xsi", xml);

        var back = Iso20022.ReadCreditTransfer(xml);
        Assert.True(back.IsValid, back.Report.ToString());
        var ct = back.Value!;
        Assert.Equal(2, ct.NumberOfTransactions);
        Assert.Equal(1000.5m, ct.ControlSum);
        Assert.Equal(new Model.Money(1000.5m, "USD"), ct.TotalSettlementAmount);
        Assert.Equal("CLRG", ct.SettlementMethod);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", ct.Transactions[0].Uetr!);
        Assert.Null(ct.Transactions[1].Uetr);
        Assert.Equal("USABA:000000001", ct.Transactions[0].CreditorAgent.Display);
        Assert.Equal("DEBT", ct.Transactions[0].ChargeBearer);
    }

    [Fact]
    public void TheBuilderRefusesAnInvalidMessage()
    {
        var builder = CreditTransferBuilder.Create("M1").AddTransaction(t => t
            .EndToEndId("E1").Amount(10m, "USD")
            .Debtor("A", agentBic: "NOTABIC").Creditor("B", agentBic: "DEMOUS33"));
        var ex = Assert.Throws<Iso20022ValidationException>(() => builder.ToXml());
        Assert.Contains(ex.Report.Errors, e => e.Code == "SCHEMA" && e.Path!.EndsWith("DbtrAgt/FinInstnId/BICFI"));
    }

    [Fact]
    public void TheBuilderCatchesBusinessErrorsToo()
    {
        var builder = CreditTransferBuilder.Create("M1").AddTransaction(t => t
            .EndToEndId("E1").Amount(10.123m, "USD")
            .Debtor("A", agentBic: "DEMOUS33").Creditor("B", iban: "GB83WEST12345698765432", agentBic: "DEMOGB2L"));
        var ex = Assert.Throws<Iso20022ValidationException>(() => builder.ToXml());
        Assert.Equal(["AMOUNT-DECIMALS", "AMOUNT-DECIMALS", "IBAN-CHECKSUM"], ex.Report.Errors.Select(e => e.Code).Order());
    }

    [Fact]
    public void TheBuilderNeedsTheRequiredParts()
    {
        Assert.Throws<InvalidOperationException>(() => CreditTransferBuilder.Create("M").Build());
        Assert.Throws<InvalidOperationException>(() => CreditTransferBuilder.Create("M").AddTransaction(t => t.EndToEndId("E")).Build());
    }

    [Fact]
    public void ErrorsInsideAnEnvelopePointAtTheOriginalLines()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "cases", "pacs008-valid-in-envelope.xml"))
            .Replace("<ChrgBr>SLEV</ChrgBr>", "<ChrgBr>NOPE</ChrgBr>");
        var report = Iso20022.Validate(xml);
        var error = Assert.Single(report.Errors);
        var expectedLine = xml.Split('\n').ToList().FindIndex(l => l.Contains("<ChrgBr>NOPE")) + 1;
        Assert.Equal(expectedLine, error.Line);
        Assert.Equal("/Document/FIToFICstmrCdtTrf/CdtTrfTxInf/ChrgBr", error.Path);
    }

    [Fact]
    public void RulesCanBeTurnedOff()
    {
        var xml = Base("pacs008-two-transactions.xml").Replace("<NbOfTxs>2</NbOfTxs>", "<NbOfTxs>3</NbOfTxs>");
        Assert.False(Iso20022.Validate(xml).IsValid);
        var options = new ValidationOptions { DisabledRules = new HashSet<string> { "PACS008-NBOFTXS" } };
        Assert.True(Iso20022.Validate(xml, options).IsValid);
    }

    [Fact]
    public void ValidatesFromAStream()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Base("camt053-eur-iban.xml")));
        Assert.True(Iso20022.Validate(stream).IsValid);
    }

    [Fact]
    public void IsSafeToUseFromManyThreads()
    {
        var messages = new[] { "pacs008-two-transactions.xml", "pacs008-minimal.xml", "camt053-eur-iban.xml", "camt053-usd-overdraft.xml" }.Select(Base).ToArray();
        var results = Enumerable.Range(0, 200).AsParallel().Select(i => Iso20022.Validate(messages[i % 4]).IsValid).ToList();
        Assert.All(results, Assert.True);
    }

    [Fact]
    public void GeneratedTypesRoundTripEveryBaseMessage()
    {
        foreach (var (file, type) in new[] { ("pacs008-two-transactions.xml", typeof(Messages.Pacs008V08.Document)), ("camt053-eur-iban.xml", typeof(Messages.Camt053V08.Document)) })
        {
            var doc = typeof(Iso20022).GetMethod(nameof(Iso20022.ReadDocument))!.MakeGenericMethod(type).Invoke(null, [Base(file), null]);
            var value = doc!.GetType().GetProperty("Value")!.GetValue(doc)!;
            var written = Iso20022.Write(value);
            // same content: compare the parsed trees, ignoring whitespace and the date-time offset representation of CreDtTm
            var a = XDocument.Parse(Base(file));
            var b = XDocument.Parse(written);
            static IEnumerable<string> Leaves(XDocument d) => d.Descendants().Where(e => !e.HasElements && e.Name.LocalName != "CreDtTm" && e.Name.LocalName != "FrDtTm" && e.Name.LocalName != "ToDtTm")
                .Select(e => $"{e.Name.LocalName}={e.Value.Trim()}|{string.Join(",", e.Attributes().Select(x => x.Value))}");
            Assert.Equal(Leaves(a), Leaves(b));
        }
    }
}
