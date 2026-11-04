# MT103 ↔ pacs.008

Iso20022Kit translates between the SWIFT MT103 (single customer credit transfer) and pacs.008.001.08, one payment at a time, in both directions. It also says what each translation changed, inferred or lost.

```csharp
using Iso20022Kit.Mt;

var t = MtTranslator.ToPacs008(File.ReadAllText("payment.fin"));
if (t.Ok) File.WriteAllText("payment.xml", t.Output);
foreach (var n in t.Notes) Console.WriteLine($"{(n.Lost ? "lost" : "note")} {n.Where}: {n.Message}");

var back = MtTranslator.ToMt103(File.ReadAllText("payment.xml"));
```

```
iso20022 mt2mx payment.fin > payment.xml      # notes on stderr, the translation on stdout
iso20022 mx2mt payment.xml --json
```

**This isn't SWIFT's translation.** SWIFT publishes official MT ↔ MX translation rules for CBPR+, but they're licensed. This mapping is built from the publicly documented MT field formats and the ISO 20022 schema. Where it has to choose, it says so in a note. Use SWIFT's rules (or its translator) where a scheme requires them.

Both directions check their output before returning it:
- the pacs.008 against the official schema and Iso20022Kit's business rules;
- the MT103 by reading it back with the MT reader.

## Reading an MT103

`Mt103.Read(text)` accepts a full FIN message (`{1:…}{2:…}{3:…}{4:…-}`) or just the text block. It checks:

| Code | Check |
|---|---|
| `MT-MISSING`, `MT-REPEATED` | 20, 23B, 32A, 50a, 59a and 71A are required; most fields appear once. |
| `MT-32A`, `MT-AMOUNT`, `MT-CURRENCY` | Dates are YYMMDD and real. Amounts have a decimal comma, at most 15 characters, and no more decimals than the currency has. |
| `MT-20`, `MT-23B`, `MT-23E`, `MT-71A`, `MT-36` | Field formats and codes. |
| `MT-RULE-36`, `MT-RULE-33B`, `MT-RULE-71` | Rules across fields: 36 only when 33B's currency differs from 32A's; 33B when there are charges; 71F and 71G according to 71A (OUR, SHA or BEN). |
| `MT-OPTION`, `MT-BIC`, `MT-LINES`, `MT-LINE-LENGTH` | Option letters per field; BICs; at most 4 lines of 35 characters for names and addresses (6 for field 72, 3 for 77B). |
| `MT-CHARSET` | Only the SWIFT X character set: letters, digits, space and `/ - ? : ( ) . , ' +`. |
| `MT-UETR`, `MT-TYPE`, `MT-VARIANT`, `MT-HEADER` | The UETR in field 121 is a lowercase UUID version 4. The message is an MT103, and not the REMIT or RFDD variant. |
| `MT-BLOCKS`, `MT-FIELD`, `MT-TOO-LONG`, `MT-EMPTY` | The structure itself. |

## MT103 → pacs.008

| MT103 | pacs.008 | Note |
|---|---|---|
| 121 (header) | `PmtId/UETR` | If it's missing, none is made up. |
| 20 | `PmtId/InstrId`, `TxId`, and `GrpHdr/MsgId` unless given | An MT103 has no end-to-end ID: `EndToEndId` is `NOTPROVIDED`. |
| 32A | `IntrBkSttlmAmt`, `IntrBkSttlmDt` | |
| 33B, 36 | `InstdAmt`, `XchgRate` | |
| 50A/F/K | `Dbtr`, `DbtrAcct` | K: the first line is the name, the rest become `AdrLine`. F: 1/ name, 2/ address, 3/ country and town; lines 4/ to 8/ (identification) aren't carried. |
| 52a, 57a | `DbtrAgt`, `CdtrAgt` | When absent, the sender and receiver. A `//FW…`-style clearing code becomes `ClrSysMmbId`. |
| 53a, 54a, 55a | reimbursement agents; `SttlmMtd` COVE | Otherwise `SttlmMtd` is INDA. This is inferred, and a note says so. |
| 56a | `IntrmyAgt1` | |
| 59/59A/59F | `Cdtr`, `CdtrAcct` | IBANs are recognised by their check digits. |
| 70 | `RmtInf/Ustrd` | The lines are joined; MT wraps at 35 characters. |
| 71A | `ChrgBr` | OUR → DEBT, SHA → SHAR, BEN → CRED. |
| 71F, 71G | `ChrgsInf` | For the sending bank or the receiving bank. |
| 23E | `InstrForCdtrAgt`, `InstrForNxtAgt`, `PmtTpInf` | CHQB, HOLD, PHOB and TELB are coded; PHON and TELE too; SDVA becomes a service level; anything else is carried as text. |
| 72 | instructions as text | `/ACC/` for the creditor's bank, the rest for the next bank. |
| 77B | `RgltryRptg/Dtls/Inf` | |
| 13C, 26T, 51A | not carried | Listed as lost. |

## pacs.008 → MT103

The reverse mapping, plus:
- **Character set.** Text is converted to the X character set: accents are removed (é becomes e, ß becomes ss), `&` becomes `+`, and other characters become `.`. Each conversion is noted.
- **Lengths.** Text is cut to MT's lengths: 16 characters for field 20, 4 lines of 35 for names, addresses and field 70. Each cut is noted.
- **What MT has no place for** is listed as lost:
  - the end-to-end ID;
  - structured remittance information;
  - the ultimate debtor and creditor, and the initiating party;
  - the purpose code;
  - the payment type;
  - tax information;
  - a second or third intermediary;
  - charges without an instructed amount.
- **More than one payment** is refused: an MT103 holds one.
- **Line breaks.** Every value is put on one line before it is wrapped. A line break inside a pacs.008 name (legal in XML) therefore can't start a new MT field, and a line that would start with `:` or `-` gets a leading `.`. Without this, a name like `Alice⏎:59:/ACCOUNT` would add a second beneficiary to the MT103.
