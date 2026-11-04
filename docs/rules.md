# Validation codes

Every problem Iso20022Kit reports has a code. The .NET library, the `iso20022` command and the browser viewer use the same codes, the same rules and the same line and column numbers (the [conformance suite](../conformance/README.md) checks that they agree).

A check happens in three stages, and each stage runs only if the one before it found nothing wrong:

1. **Reading**: is it well-formed XML, safe to read, and an ISO 20022 message this version supports?
2. **Schema** (`SCHEMA`): does it match the official ISO 20022 XSD for its message and version?
3. **Business rules**: the things a bank checks that the schema can't express, such as totals adding up.

Business rules wait for the schema to pass because they depend on the message's structure: with a required element missing, a total can't be checked meaningfully. In .NET, `new ValidationOptions { AlwaysRunRules = true }` runs them anyway.

Errors make a message invalid. Warnings (only `CURRENCY-UNKNOWN` today) don't.

## Reading

| Code | Meaning |
|---|---|
| `XML-MALFORMED` | Not well-formed XML: an unclosed tag, a stray `&`, text outside the root element… The message names the problem and its position. |
| `XML-DTD` | The input has a DTD (`<!DOCTYPE …>`). DTDs are never used in ISO 20022 messages and are how XML attacks work (reading local files through external entities, "billion laughs" memory exhaustion), so the input is refused without being read. The .NET reader doesn't report a position for this one; the browser viewer does. |
| `MSG-NO-DOCUMENT` | The XML has no ISO 20022 `Document` element, i.e. no `<Document xmlns="urn:iso:std:iso:20022:tech:xsd:…">`, either as the root or inside an envelope. |
| `MSG-UNKNOWN` | There is a `Document`, but its namespace isn't a recognisable message identifier such as `pacs.008.001.08`. |
| `MSG-UNSUPPORTED` | A real ISO 20022 message that this version doesn't support. Supported today: `pacs.008.001.08` and `camt.053.001.08`. |

A message inside a business envelope (for example next to an `AppHdr`) is found and checked; line numbers refer to the whole input.

## Schema

| Code | Meaning |
|---|---|
| `SCHEMA` | The message doesn't match the official XSD: a missing or unexpected element, elements in the wrong order, a value in the wrong format (BIC, IBAN, UETR, date…), a code that isn't allowed, a value too long, too many decimals for the schema, a negative amount… |

The XSDs in [`schemas/`](../schemas) are the official ISO 20022 schemas; see [NOTICE](../NOTICE) for where they come from. The .NET library uses them directly with `System.Xml.Schema`. The browser viewer uses a JSON model generated from the same files (`tools/SchemaModel`), so both check the same constraints.

## Business rules

| Code | Messages | Rule |
|---|---|---|
| `AMOUNT-DECIMALS` | all | An amount may not have more decimals than its currency has in ISO 4217: 2 for USD and EUR, 0 for JPY and KRW, 3 for KWD and BHD, 4 for CLF. (The schema allows up to 5 decimals for any currency.) |
| `CURRENCY-UNKNOWN` | all | **Warning.** The `Ccy` isn't an active ISO 4217 currency with minor units, so its decimals can't be checked. Codes such as `XAU` (gold) or `XTS` (testing) have no minor units and get this warning too. |
| `IBAN-CHECKSUM` | all | An IBAN's two check digits must match the rest of the number (ISO 13616, mod 97). This catches most typing mistakes. The schema only checks the shape (two letters, two digits, up to 30 letters or digits). |
| `PACS008-NBOFTXS` | pacs.008 | `GrpHdr/NbOfTxs` must equal the number of `CdtTrfTxInf` transactions. |
| `PACS008-CTRLSUM` | pacs.008 | `GrpHdr/CtrlSum`, when present, must equal the sum of every `IntrBkSttlmAmt`. As in ISO 20022, the control sum ignores currencies. |
| `PACS008-TOTAL` | pacs.008 | `GrpHdr/TtlIntrBkSttlmAmt`, when present, must equal the sum of every `IntrBkSttlmAmt`, and every transaction must settle in its currency. |
| `CAMT053-BALANCE` | camt.053 | For each statement: opening booked balance (`OPBD`, or `PRCD` previous closing) + booked credits − booked debits = closing booked balance (`CLBD`). Only entries with status `BOOK` count. Debit balances (`CdtDbtInd` = `DBIT`, an overdrawn account) are negative. Skipped when the balances and entries aren't all in one currency. |
| `CAMT053-SUMMARY` | camt.053 | For each statement with a `TxsSummry`: the numbers of entries and their sums in `TtlNtries`, `TtlCdtNtries` and `TtlDbtNtries` must match the entries listed, and `TtlNetNtry` must equal credits − debits, with the right direction. |

All sums are exact decimal arithmetic: no rounding, and `1500.25` and `1500.250` are equal.

Any business rule can be switched off: `new ValidationOptions { DisabledRules = { "CURRENCY-UNKNOWN" } }` in .NET, or the checkboxes under "Checks" in the viewer. `SCHEMA` can't be switched off.

## Positions

Each issue has a path (`/Document/FIToFICstmrCdtTrf/CdtTrfTxInf[2]/IntrBkSttlmAmt`, with an index only where an element repeats) and, when known, a line and column. For an element the column is that of its `<`; for an attribute it's the attribute's name.

## What isn't checked

- Rules of a specific scheme or market infrastructure, such as CBPR+, SEPA, FedNow, CHAPS or a national RTGS's usage guidelines. These restrict the base message further (which fields are mandatory, which characters are allowed, maximum amounts), and each publishes its own.
- Whether BICs, IBANs or clearing codes exist and are reachable: that needs a directory such as the SWIFT BIC directory.
- Dates in context: for example whether a settlement date is a business day or is in the past.
- Duplicate message IDs or end-to-end IDs across messages.

Run `iso20022 rules` to list the codes, or see `Iso20022.RuleDescriptions` in .NET.
