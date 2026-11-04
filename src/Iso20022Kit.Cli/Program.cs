// iso20022: validate and read ISO 20022 messages from the command line.
//   iso20022 validate <file>... [--json]     exit code 0 valid, 1 invalid, 2 usage or file error
//   iso20022 read <file> [--json]            the message content in plain form
//   iso20022 rules                           every validation code
using System.Text.Json;
using Iso20022Kit;
using Iso20022Kit.Validation;

var json = args.Contains("--json");
var rest = args.Where(a => a != "--json").ToArray();
if (rest.Length == 0 || rest[0] is "-h" or "--help") return Usage();
var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

switch (rest[0])
{
    case "rules":
        foreach (var (code, text) in Iso20022.RuleDescriptions) Console.WriteLine($"{code,-18} {text}");
        return 0;
    case "conformance" when rest.Length > 1:
    {
        // one JSON line per case, the same format as the runners for other libraries (conformance/runners)
        var suitePath = rest[1];
        using var suite = JsonDocument.Parse(File.ReadAllText(suitePath));
        var dir = Path.GetDirectoryName(Path.GetFullPath(suitePath))!;
        var line = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var c in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            var report = Iso20022.Validate(File.ReadAllText(Path.Combine(dir, c.GetProperty("file").GetString()!)));
            var unsupported = report.Issues.Any(i => i.Code is "MSG-UNSUPPORTED");
            var result = unsupported ? "unsupported" : report.IsValid ? "valid" : "invalid";
            var detail = string.Join("; ", report.Issues.Select(i => i.Code).Distinct());
            Console.WriteLine(JsonSerializer.Serialize(new { id = c.GetProperty("id").GetString(), result, detail }, line));
        }
        return 0;
    }
    case "validate" when rest.Length > 1:
    {
        var allValid = true;
        var results = new List<object>();
        foreach (var file in rest[1..])
        {
            if (!File.Exists(file)) { Console.Error.WriteLine($"No such file: {file}"); return 2; }
            var report = Iso20022.Validate(File.ReadAllText(file));
            allValid &= report.IsValid;
            if (json) results.Add(new { file, message = report.Message?.ToString(), valid = report.IsValid, issues = report.Issues });
            else
            {
                Console.WriteLine($"{(report.IsValid ? "VALID  " : "INVALID")} {file} ({report.Message?.ToString() ?? "unknown"})");
                foreach (var i in report.Issues) Console.WriteLine($"  {i}");
            }
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(results.Count == 1 ? results[0] : results, options));
        return allValid ? 0 : 1;
    }
    case "read" when rest.Length > 1:
    {
        if (!File.Exists(rest[1])) { Console.Error.WriteLine($"No such file: {rest[1]}"); return 2; }
        var xml = File.ReadAllText(rest[1]);
        var id = Iso20022.Detect(xml);
        object? value; ValidationReport report;
        if (id?.Family == "pacs.008") { var r = Iso20022.ReadCreditTransfer(xml); value = r.Value; report = r.Report; }
        else if (id?.Family == "camt.053") { var r = Iso20022.ReadBankStatement(xml); value = r.Value; report = r.Report; }
        else { report = Iso20022.Validate(xml); value = null; }
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { message = id?.ToString(), valid = report.IsValid, issues = report.Issues, content = value }, options));
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(value, options));
            foreach (var i in report.Issues) Console.Error.WriteLine(i);
        }
        return report.IsValid ? 0 : 1;
    }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("""
        iso20022 validate <file>... [--json]   check messages against the official schema and business rules
        iso20022 read <file> [--json]          show what a pacs.008 or camt.053 says
        iso20022 rules                         list every validation code
        iso20022 conformance <suite.json>      run the conformance suite (one JSON line per case)
        Exit codes: 0 valid, 1 invalid, 2 usage or file error.
        """);
    return 2;
}
