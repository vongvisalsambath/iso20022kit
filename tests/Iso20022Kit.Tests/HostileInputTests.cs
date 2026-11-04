using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Iso20022Kit.Builders;
using Iso20022Kit.Validation;

namespace Iso20022Kit.Tests;

/// <summary>
/// Inputs that try to break, mislead or slow down the validator. The stored cases (tests/hostile/cases.json) are also
/// run by the browser viewer's tests with the same expectations, so the two implementations can't drift apart.
/// </summary>
public class HostileInputTests
{
    private static readonly string Hostile = Path.Combine(AppContext.BaseDirectory, "hostile");
    private static readonly string Base = Path.Combine(AppContext.BaseDirectory, "conformance", "base");

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Hostile, "cases.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray()) yield return [c.GetProperty("id").GetString()!];
    }

    private static (string File, bool Valid, string[] Codes) Case(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Hostile, "cases.json")));
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var e = c.GetProperty("expect");
        return (Path.Combine(Hostile, c.GetProperty("file").GetString()!), e.GetProperty("valid").GetBoolean(),
            e.GetProperty("codes").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static string[] Codes(ValidationReport r) => r.Issues.Select(i => i.Code).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Theory]
    [MemberData(nameof(Cases))]
    public void StoredCaseFromBytes(string id)
    {
        var (file, valid, codes) = Case(id);
        using var s = File.OpenRead(file);
        var report = Iso20022.Validate(s);
        Assert.True(valid == report.IsValid, report.ToString());
        Assert.Equal(codes, Codes(report));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void StoredCaseFromText(string id)
    {
        // the string overload sees text, so only cases that are plain UTF-8 (no BOM, no other encoding) apply
        var (file, valid, codes) = Case(id);
        var bytes = File.ReadAllBytes(file);
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); } catch (DecoderFallbackException) { return; }
        if (bytes.Length > 1 && (bytes[0] == 0xFF || bytes[0] == 0xFE) || text.Contains("ISO-8859-1")) return;
        var report = Iso20022.Validate(text);
        Assert.True(valid == report.IsValid, report.ToString());
        Assert.Equal(codes, Codes(report));
    }

    [Fact]
    public void IssueMessagesNeverContainHiddenCharacters()
    {
        foreach (var file in Directory.GetFiles(Hostile, "*.xml"))
        {
            using var s = File.OpenRead(file);
            foreach (var i in Iso20022.Validate(s, new ValidationOptions { AlwaysRunRules = true }).Issues)
            {
                Assert.DoesNotContain(i.Message, c => Internal.SafeText.IsHidden(c) || char.IsSurrogate(c));
                Assert.DoesNotContain(i.Path ?? "", c => Internal.SafeText.IsHidden(c));
            }
        }
        var bidi = Iso20022.Validate(File.ReadAllText(Path.Combine(Hostile, "bidi-override-in-name.xml"))).Issues.Single();
        Assert.Contains("\\u202E (RIGHT-TO-LEFT OVERRIDE)", bidi.Message);
    }

    // ---- size and shape: each must be refused or finished quickly, never hang or crash the process

    private static string Pacs => File.ReadAllText(Path.Combine(Base, "pacs008-minimal.xml"));
    private static string Camt => File.ReadAllText(Path.Combine(Base, "camt053-eur-iban.xml"));

    private static ValidationReport Timed(string xml, double maxSeconds, ValidationOptions? options = null)
    {
        var sw = Stopwatch.StartNew();
        var report = Iso20022.Validate(xml, options);
        Assert.True(sw.Elapsed.TotalSeconds < maxSeconds, $"took {sw.Elapsed.TotalSeconds:0.0} s");
        return report;
    }

    [Fact]
    public void DeepNestingIsRefusedQuickly()
    {
        // .NET's tree builder is quadratic in depth: 200,000 levels took minutes before the limit
        const int d = 200_000;
        var deep = "<E>" + string.Concat(Enumerable.Repeat("<X>", d)) + string.Concat(Enumerable.Repeat("</X>", d)) + "</E>";
        Assert.Equal(["XML-MALFORMED"], Codes(Timed(deep, 5)));
        var inDocument = Pacs.Replace("<Nm>Jane Doe</Nm>", "<Nm>Jane Doe</Nm>" + string.Concat(Enumerable.Repeat("<X>", d)) + string.Concat(Enumerable.Repeat("</X>", d)));
        var r = Timed(inDocument, 5);
        Assert.Equal(["XML-MALFORMED"], Codes(r));
        Assert.Contains("nested more than 256 levels", r.Issues[0].Message);
    }

    [Theory]
    [InlineData(256, "MSG-NO-DOCUMENT")]   // 256 levels: fine (not ISO, but read)
    [InlineData(257, "XML-MALFORMED")]     // one more: refused
    public void DepthLimitBoundary(int levels, string code)
    {
        var xml = string.Concat(Enumerable.Repeat("<E>", levels)) + string.Concat(Enumerable.Repeat("</E>", levels));
        Assert.Equal([code], Codes(Iso20022.Validate(xml)));
    }

    [Fact]
    public void AMillionSiblingsFinish()
    {
        var wide = "<E>" + string.Concat(Enumerable.Repeat("<a/>", 1_000_000)) + "</E>";
        Assert.Equal(["MSG-NO-DOCUMENT"], Codes(Timed(wide, 10)));
    }

    [Fact]
    public void ThousandsOfErrorsStopAtTheLimit()
    {
        var attrs = Pacs.Replace("<Nm>Jane Doe</Nm>", "<Nm " + string.Join(" ", Enumerable.Range(0, 100_000).Select(i => $"a{i}=\"1\"")) + ">Jane Doe</Nm>");
        var r = Timed(attrs, 10);
        Assert.Equal(1001, r.Issues.Count);
        Assert.Equal("TOO-MANY-ISSUES", r.Issues[^1].Code);
        Assert.False(r.IsValid);

        // 20,000 entries each with a rule error: paths used to be quadratic (60 s)
        var entry = System.Text.RegularExpressions.Regex.Match(Camt, @"<Ntry>[\s\S]*?</Ntry>").Value.Replace(">1500.00<", ">1.001<");
        var many = System.Text.RegularExpressions.Regex.Replace(Camt, @"<TxsSummry>[\s\S]*?</TxsSummry>\s*", "");
        many = System.Text.RegularExpressions.Regex.Replace(many, @"<Ntry>[\s\S]*</Ntry>", _ => string.Join("\n", Enumerable.Repeat(entry, 20_000)));
        var rules = Timed(many, 30);
        Assert.Equal(1001, rules.Issues.Count);
        Assert.Contains(rules.Issues, i => i.Path == "/Document/BkToCstmrStmt/Stmt/Ntry[1000]/Amt");

        Assert.Equal(6, Iso20022.Validate(attrs, new ValidationOptions { MaxIssues = 5 }).Issues.Count);
    }

    [Fact]
    public void LongValuesAreNotRepeatedInFull()
    {
        var huge = System.Text.RegularExpressions.Regex.Replace(Pacs, "<BICFI>[^<]*</BICFI>", "<BICFI>" + new string('A', 2_000_000) + "</BICFI>", System.Text.RegularExpressions.RegexOptions.None);
        var issue = Iso20022.Validate(huge).Issues.Single();
        Assert.True(issue.Message.Length < 700, $"{issue.Message.Length} characters");
        Assert.Contains("characters omitted", issue.Message);
        Assert.EndsWith("The Pattern constraint failed.", issue.Message);
    }

    [Fact]
    public void InputsOverTheSizeLimitAreRefused()
    {
        var big = Pacs.Replace("<Nm>Jane Doe</Nm>", "<Nm>Jane Doe</Nm><!--" + new string('x', 50_000_001) + "-->");
        Assert.Equal(["XML-MALFORMED"], Codes(Iso20022.Validate(big)));
    }

    // ---- the API keeps its promises on bad input

    public static IEnumerable<object[]> AllInputs() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "conformance", "cases"), "*.xml")
            .Concat(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "hostile"), "*.xml"))
            .Select(f => new object[] { Path.GetFileName(Path.GetDirectoryName(f)!) + "/" + Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(AllInputs))]
    public void NeverThrowsEvenWithRulesForcedOnBrokenMessages(string relative)
    {
        var file = Path.Combine(AppContext.BaseDirectory, relative.StartsWith("cases/") ? "conformance" : "", relative);
        using var s = File.OpenRead(file);
        var report = Iso20022.Validate(s, new ValidationOptions { AlwaysRunRules = true });
        Assert.NotNull(report);
        using var s2 = File.OpenRead(file);
        _ = Iso20022.ReadCreditTransfer(s2);
        using var s3 = File.OpenRead(file);
        _ = Iso20022.ReadBankStatement(s3);
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]   // decimal.MaxValue: two of them overflowed the sum
    [InlineData("1E+5")]
    [InlineData("NaN")]
    [InlineData("")]
    public void GarbageAmountsWithRulesForcedDontThrow(string amount)
    {
        var xml = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(Base, "pacs008-two-transactions.xml")),
            @"(<IntrBkSttlmAmt Ccy=""USD"">)[^<]+", m => m.Groups[1].Value + amount);
        var report = Iso20022.Validate(xml, new ValidationOptions { AlwaysRunRules = true });
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "SCHEMA");
    }

    [Theory]
    [InlineData("G")]
    [InlineData("")]
    [InlineData("GB8")]
    public void ShortIbansWithRulesForcedDontThrow(string iban)
    {
        var xml = File.ReadAllText(Path.Combine(Base, "pacs008-two-transactions.xml")).Replace("GB82WEST12345698765432", iban);
        var report = Iso20022.Validate(xml, new ValidationOptions { AlwaysRunRules = true });
        Assert.Contains(report.Issues, i => i.Code == "IBAN-CHECKSUM");
    }

    [Fact]
    public void SchemaLocationsAreNeverFetched()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var xml = File.ReadAllText(Path.Combine(Hostile, "xsi-schemalocation-remote.xml")).Replace("47811", port.ToString(CultureInfo.InvariantCulture));
            Assert.True(Iso20022.Validate(xml).IsValid);
            Assert.False(listener.Pending(), "the validator connected to the schemaLocation URL");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void StreamsAreDecodedByTheirDeclaredEncodingAndLeftOpen()
    {
        using var s = File.OpenRead(Path.Combine(Hostile, "latin1-declared.xml"));
        var ct = Iso20022.ReadCreditTransfer(s);
        Assert.True(ct.IsValid, ct.Report.ToString());
        Assert.Equal("Renée Doe", ct.Value!.Transactions[0].Debtor.Name);
        Assert.True(s.CanRead, "Validate closed the caller's stream");

        using var u = File.OpenRead(Path.Combine(Hostile, "utf16le-bom.xml"));
        Assert.Equal(MessageId.Pacs008V08, Iso20022.Detect(u));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void AmountsDontDependOnTheUsersCulture(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Assert.True(Iso20022.Validate(File.ReadAllText(Path.Combine(Base, "pacs008-two-transactions.xml"))).IsValid);
            var bad = Iso20022.Validate(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "cases", "pacs008-business-control-sum.xml")));
            Assert.Contains("1500.25", bad.Issues.Single().Message);
        }
        finally { CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = saved; }
    }

    [Fact]
    public void HiddenCharacterWarningsCanBeSwitchedOff()
    {
        var xml = File.ReadAllText(Path.Combine(Hostile, "bidi-override-in-name.xml"));
        Assert.Empty(Iso20022.Validate(xml, new ValidationOptions { DisabledRules = { "HIDDEN-CHARACTERS" } }).Issues);
    }

    [Fact]
    public void TheBuilderExplainsCharactersXmlCantHold()
    {
        var ex = Assert.Throws<ArgumentException>(() => CreditTransferBuilder.Create("M1").AddTransaction(t => t
            .EndToEndId("E1").Amount(10m, "USD").Debtor("Bad\u0001Name", agentBic: "DEMOUS33").Creditor("B", agentBic: "DEMOGB2L")).ToXml());
        Assert.Contains("can't be written as XML", ex.Message);
        Assert.Contains("0x01", ex.Message);
    }

    [Fact]
    public void ConcurrentValidationGivesTheSameAnswers()
    {
        var files = Directory.GetFiles(Hostile, "*.xml").Concat(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "conformance", "cases"), "*.xml")).ToArray();
        var expected = files.ToDictionary(f => f, f => { using var s = File.OpenRead(f); return string.Join(",", Codes(Iso20022.Validate(s))); });
        Parallel.For(0, files.Length * 8, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var f = files[i % files.Length];
            using var s = File.OpenRead(f);
            Assert.Equal(expected[f], string.Join(",", Codes(Iso20022.Validate(s))));
        });
    }
}
