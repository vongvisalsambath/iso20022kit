# ISO 20022 conformance suite

A set of ISO 20022 messages, valid and deliberately broken, with the verdict a validator should reach for each. It doesn't depend on any language or library: any ISO 20022 library in Java, Go, Rust, Python, TypeScript or .NET can run it and see where it agrees.

All data is made up. Bank identifiers such as `TSTAKHPP` and `DEMOUS33` are fictitious; the two IBANs are the standard documentation examples (`GB82WEST12345698765432`, `DE89370400440532013000`).

## What's in it

| Category | Cases | What a validator should do |
|---|---|---|
| `valid` | 7 | Accept. One has a non-ISO-4217 currency and should at most warn. One is wrapped in a business envelope next to an `AppHdr`. |
| `schema` | 18 | Reject: each breaks the official XSD once (missing required element, bad BIC, wrong order, too many decimals for the schema, bad date, bad UETR …). Any library that validates against the schema should catch these. |
| `business` | 14 | Reject: each matches the XSD but breaks a rule the schema can't express: `NbOfTxs`, `CtrlSum` and `TtlIntrBkSttlmAmt` must add up; a camt.053 statement must balance and its summary must match its entries; amounts must respect ISO 4217 decimals; IBAN check digits must be right. |
| `wellformed` | 2 | Reject: not well-formed XML. |
| `unsupported` | 2 | Reject or report as unsupported: a version that doesn't exist, and XML that isn't ISO 20022. |
| `security` | 2 | Refuse safely: an external entity trying to read a local file (XXE) and an entity expansion bomb ("billion laughs"). A library must never expand them. |

Messages covered: `pacs.008.001.08` (FI to FI customer credit transfer) and `camt.053.001.08` (bank to customer statement).

## Files

- `suite.json`: the list of cases. Each has an `id`, a `file`, the `message` it is, a `category`, a `description`, and `expect`:
  - `valid` (true or false): the verdict.
  - `codes`: the Iso20022Kit codes expected (see `../docs/rules.md`). Other libraries won't use the same codes, so for them only `valid` and the category matter.
- `cases/*.xml`: the messages.
- `base/*.xml`: the four valid messages every case is made from.
- `generate.py`: rebuilds `cases/` and `suite.json` from `base/` (`python3 conformance/generate.py`). Each case is one base message with one change, so you can see exactly what was broken.

## Running it against your library

1. Read `suite.json`.
2. For each case, run your validator on `file`. Treat any exception, parse error or refusal as "invalid".
3. Compare with `expect.valid`, and group the results by `category`.

Results for other libraries are in `../docs/comparison.md`, with the adapters used to produce them in `runners/`.

## Adding cases

Add a tuple to `CASES` in `generate.py`: an id, the base file, the message, a category, the expected verdict and codes, a description, and the change as `(old text, new text, count)`. Run the generator and the .NET tests (`dotnet test`). Suggestions and new cases are welcome as issues or pull requests.
