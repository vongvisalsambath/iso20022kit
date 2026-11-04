# Iso20022Kit

Read, write and check ISO 20022 payment messages: `pacs.008` (a bank-to-bank customer credit transfer) and `camt.053` (a bank-to-customer account statement), version `.001.08` of each.

It comes in three parts that give the same answers:

- **A .NET library** (`Iso20022Kit`, .NET 8 and 10): read a message into simple objects, build one with a fluent builder, and validate against the official schema plus the business rules a bank checks (totals, statement balances, currency decimals, IBAN check digits).
- **A browser viewer** (`viewer/`): paste or drop a message and see what it says in plain English, every field with its name spelled out, and every problem with the line it's on. It runs entirely in the page; nothing is uploaded.
- **A conformance suite** (`conformance/`): 45 messages, valid and broken one way each, with the verdict a validator should reach. Any library in any language can run it. [Results for four libraries](docs/comparison.md), including this one, Prowide (Java), Moov (Go) and mx20022 (Rust).

All sample data is made up.

## Why

ISO 20022 has replaced the old SWIFT MT format for cross-border payments, and Fedwire, CHAPS, the Eurosystem's T2 and many domestic systems now use it too. The schemas tell you whether a message is *shaped* right, but not whether it *adds up*: a pacs.008 can match the XSD perfectly while its total disagrees with its transactions, and a camt.053 can be valid while its closing balance doesn't follow from its entries. Error messages from schema validators are also hard to read (`The element 'CdtTrfTxInf' has invalid child element 'Cdtr'…`).

Iso20022Kit checks both, and says what's wrong in plain words with the exact place:

```
INVALID pacs008-business-control-sum.xml (pacs.008.001.08)
  error PACS008-CTRLSUM at /Document/FIToFICstmrCdtTrf/GrpHdr/CtrlSum (line 8, column 7):
  CtrlSum is 1500.00, but the transaction amounts (IntrBkSttlmAmt) add up to 1500.25.
```

## .NET

> Not on NuGet yet. Build from source: `dotnet build`, or reference `src/Iso20022Kit/Iso20022Kit.csproj`.

### Validate

```csharp
using Iso20022Kit;

var report = Iso20022.Validate(File.ReadAllText("payment.xml"));
if (!report.IsValid)
    foreach (var e in report.Errors)
        Console.WriteLine($"{e.Code} line {e.Line}: {e.Message}");   // e.Path has the element's path
```

`Validate` takes a string or a stream; for files, pass the stream, so the bytes are decoded by their declared encoding and invalid ones are reported rather than replaced. It detects the message type from the namespace (also inside a business envelope with an `AppHdr`), refuses DTDs and external entities, and never throws for bad input: everything is in the report. The codes are listed in [docs/rules.md](docs/rules.md).

### Read

```csharp
var result = Iso20022.ReadBankStatement(xml);
if (!result.IsValid) { Console.WriteLine(result.Report); return; }

foreach (var s in result.Value!.Statements)
{
    Console.WriteLine($"{s.Account.Iban}: {s.Opening?.Amount} → {s.Closing?.Amount}");
    foreach (var e in s.Entries)
        Console.WriteLine($"  {e.BookingDate} {e.Amount} {e.Counterparty} {string.Join(" ", e.RemittanceInformation)}");
}
```

`ReadCreditTransfer` works the same way for pacs.008. Amounts are `decimal` with their currency; debits and overdrawn balances are negative. For full access to every field, `Iso20022.ReadDocument<T>` returns the types generated from the official schema (in `Iso20022Kit.Messages.Pacs008V08` and `…Camt053V08`).

### Build

```csharp
string xml = CreditTransferBuilder.Create("MSG-20261104-001")
    .Settlement(SettlementMethod.ClearingSystem, "FDW")
    .InstructingAgent("TSTAKHPP")
    .AddTransaction(t => t
        .EndToEndId("INV-445")
        .Amount(1000.00m, "USD", new DateOnly(2026, 11, 4))
        .Charges(ChargeBearer.Shared)
        .Debtor("Sokha Chan", account: "001234567", agentBic: "TSTAKHPP", country: "KH")
        .Creditor("Oxford Books Ltd", iban: "GB82WEST12345698765432", agentBic: "DEMOGB2L")
        .Remittance("Invoice 445"))
    .ToXml();   // validated before it's returned: throws Iso20022ValidationException if anything is wrong
```

The builder fills in what can be derived (number of transactions, control sum, total, a UETR for each transaction) and writes clean XML: UTF-8, the message namespace as default, no `xsi` noise.

### Command line

```
dotnet run --project src/Iso20022Kit.Cli -- validate payment.xml statement.xml
dotnet run --project src/Iso20022Kit.Cli -- read statement.xml --json
dotnet run --project src/Iso20022Kit.Cli -- rules
```

`validate` exits with 0 when every file is valid, 1 when one isn't, 2 for a usage or file error, and `--json` prints the reports as JSON. It is packaged as a .NET tool named `iso20022`.

## Browser viewer

```
cd viewer
npm ci
npm run dev      # http://127.0.0.1:8000
npm test         # the conformance suite and the hostile inputs through the browser validator
npm run test:e2e # the built page in Chromium, with the production headers
npm run build    # static site in viewer/dist
```

The viewer reimplements the checks in TypeScript, so it needs no server: the schema rules come from a JSON model generated from the same XSDs (`tools/SchemaModel`), and the business rules are ported line for line. Its tests run all 45 conformance cases and require the same verdict and the same codes as the .NET library, and pin line and column numbers that both must report.

The page has a strict Content-Security-Policy (`connect-src 'none'`): it can't send the message anywhere even by mistake. Large statements (tested up to 21 MB, 840,000 lines) open in a read-only view that only draws the visible lines.

## Security

Messages are treated as hostile input: no DTDs or external entities, nothing fetched, size, depth and issue limits, and warnings for invisible or direction-changing characters that could make a payee's name display differently from what it is. `tests/hostile/` holds 44 such inputs that both the .NET library and the viewer must handle the same way. See [SECURITY.md](SECURITY.md) for the details and how to report a problem.

## Conformance suite

See [conformance/README.md](conformance/README.md). In short: read `conformance/suite.json`, run your validator on each case's file, and compare with `expect.valid`. The `runners/` folder has the adapters used for Prowide, Moov and mx20022, and [docs/comparison.md](docs/comparison.md) the results, with the bugs found in each checked against its source.

New cases are the most useful contribution: the suite was written alongside this library, so it can't show this library's blind spots.

## What it doesn't do (yet)

- Other messages and versions. pacs.008.001.08 and camt.053.001.08 were chosen because they're the versions Fedwire and CBPR+ (cross-border payments over SWIFT) use; pacs.002, pacs.004, pacs.009, pain.001 and camt.054 would be next.
- Scheme rules: CBPR+, SEPA, FedNow and others restrict the base message further. These checks are the ISO 20022 baseline only.
- Lookups: whether a BIC exists, whether a date is a business day.
- MT (SWIFT FIN) conversion.

## Repository

| Path | |
|---|---|
| `src/Iso20022Kit` | The library. `Generated/` holds the types generated from the schemas (`scripts/generate.sh`). |
| `src/Iso20022Kit.Cli` | The `iso20022` command. |
| `tests/` | .NET tests, including every conformance case, and `tests/hostile/`: inputs that try to break or mislead a validator. |
| `viewer/` | The browser viewer (TypeScript, no framework, about 35 KB gzipped). |
| `conformance/` | The language-neutral test suite and the runners for other libraries. |
| `schemas/` | The official ISO 20022 XSDs (see [NOTICE](NOTICE)). |
| `tools/SchemaModel` | Turns an XSD into the JSON model the viewer uses. |
| `docs/` | [Validation codes](docs/rules.md) and the [library comparison](docs/comparison.md). |

## Licence

Apache-2.0 (see [LICENSE](LICENSE)). The ISO 20022 schemas in `schemas/` are published by the ISO 20022 Registration Authority and remain under its terms; [NOTICE](NOTICE) says where these copies come from.

Written with the help of Claude (Anthropic).
