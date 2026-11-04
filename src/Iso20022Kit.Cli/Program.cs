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

try
{
    return Run();
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Couldn't read the file: {e.Message}");
    return 2;
}

int Run()
{
    switch (rest[0])
    {
        case "rules":
            foreach (var (code, text) in Iso20022.RuleDescriptions) Console.WriteLine($"{code,-22} {text}");
            return 0;
        case "conformance" when rest.Length > 1:
        {
            // one JSON line per case, the same format as the runners for other libraries (conformance/runners)
            var suitePath = Path.GetFullPath(rest[1]);
            if (!File.Exists(suitePath)) { Console.Error.WriteLine($"No such file: {rest[1]}"); return 2; }
            using var suite = JsonDocument.Parse(File.ReadAllText(suitePath));
            var dir = Path.GetDirectoryName(suitePath)! + Path.DirectorySeparatorChar;
            var line = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            foreach (var c in suite.RootElement.GetProperty("cases").EnumerateArray())
            {
                // a suite can only point at files in its own folder, so a downloaded suite.json can't read anything else
                var file = Path.GetFullPath(Path.Combine(dir, c.GetProperty("file").GetString() ?? ""));
                if (!file.StartsWith(dir, StringComparison.Ordinal)) { Console.Error.WriteLine($"Case {c.GetProperty("id")}: its file is outside the suite's folder."); return 2; }
                var report = Check(file, out _);
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
                var report = Check(file, out var tooBig);
                if (tooBig) return 2;
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
            var file = rest[1];
            if (!File.Exists(file)) { Console.Error.WriteLine($"No such file: {file}"); return 2; }
            if (TooBig(file)) return 2;
            MessageId? id;
            using (var s = File.OpenRead(file)) id = Iso20022.Detect(s);
            object? value; ValidationReport report;
            using (var s = File.OpenRead(file))
            {
                if (id?.Family == "pacs.008") { var r = Iso20022.ReadCreditTransfer(s); value = r.Value; report = r.Report; }
                else if (id?.Family == "camt.053") { var r = Iso20022.ReadBankStatement(s); value = r.Value; report = r.Report; }
                else { report = Iso20022.Validate(s); value = null; }
            }
            // System.Text.Json escapes control, bidi and other non-ASCII characters, so values print as \u202E, not as themselves
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
}

// Files are read as bytes, so the XML reader decodes them (BOM or XML declaration) and rejects invalid bytes.
static ValidationReport Check(string file, out bool tooBig)
{
    tooBig = TooBig(file);
    if (tooBig) return Iso20022.Validate("");
    using var s = File.OpenRead(file);
    return Iso20022.Validate(s);
}

// The library stops at 50 million characters anyway; refusing huge files up front avoids reading them at all.
static bool TooBig(string file)
{
    const long limit = 200_000_000;
    if (new FileInfo(file).Length <= limit) return false;
    Console.Error.WriteLine($"{file} is larger than {limit / 1_000_000} MB; ISO 20022 messages this size aren't supported.");
    return true;
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
