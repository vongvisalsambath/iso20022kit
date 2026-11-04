using System.Diagnostics;
using System.Text.Json;
using Iso20022Kit.Validation;

namespace Iso20022Kit.Tests;

/// <summary>Runs every case in conformance/suite.json: the verdict and the exact set of codes must match.</summary>
public class ConformanceTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "suite.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return [c.GetProperty("id").GetString()!];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "suite.json")));
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var expectValid = c.GetProperty("expect").GetProperty("valid").GetBoolean();
        var expectCodes = c.GetProperty("expect").GetProperty("codes").EnumerateArray().Select(x => x.GetString()!).ToHashSet();

        var sw = Stopwatch.StartNew();
        var report = Iso20022.Validate(File.ReadAllText(Path.Combine(Root, c.GetProperty("file").GetString()!)));
        sw.Stop();

        var codes = report.Issues.Select(i => i.Code).ToHashSet();
        Assert.True(expectValid == report.IsValid, $"{id}: expected valid={expectValid}, got {report}");
        Assert.True(expectCodes.SetEquals(codes), $"{id}: expected codes [{string.Join(", ", expectCodes)}], got [{string.Join(", ", codes)}]: {report}");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{id} took {sw.ElapsedMilliseconds} ms");
        // every issue points somewhere useful
        foreach (var issue in report.Issues.Where(i => i.Code is not ("XML-MALFORMED" or "XML-DTD")))
            Assert.False(string.IsNullOrEmpty(issue.Path), $"{id}: {issue} has no path");
    }

    [Fact]
    public void SuiteHasEveryCategory()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "suite.json")));
        var categories = doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("category").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "valid", "schema", "business", "wellformed", "unsupported", "security" }, categories);
    }
}
