#!/usr/bin/env python3
"""Generates the conformance cases from the four valid base messages in base/.

Each case is the base message with one deliberate change (or none), plus what a validator
should say about it. Run: python3 conformance/generate.py  (writes cases/ and suite.json).
The output is committed, so other languages can use the suite without Python.
"""
import json
import pathlib

HERE = pathlib.Path(__file__).parent
BASE = HERE / "base"
OUT = HERE / "cases"

P_TWO = "pacs008-two-transactions.xml"
P_MIN = "pacs008-minimal.xml"
C_EUR = "camt053-eur-iban.xml"
C_USD = "camt053-usd-overdraft.xml"

PACS = "pacs.008.001.08"
CAMT = "camt.053.001.08"

# (id, base, message, category, valid, codes, description, [(old, new, count)])
# category: valid | schema (an XSD validator catches it) | business (needs rules beyond the schema)
#           | wellformed (not well-formed XML) | security (must be refused safely) | unsupported
CASES = [
    # ---- valid ----
    ("pacs008-valid-two-transactions", P_TWO, PACS, "valid", True, [], "Two USD payments with totals, IBAN and non-IBAN accounts.", []),
    ("pacs008-valid-minimal", P_MIN, PACS, "valid", True, [], "One EUR payment, clearing settlement, a debtor bank identified by US routing number.", []),
    ("camt053-valid-eur-iban", C_EUR, CAMT, "valid", True, [], "EUR statement with balances, a transaction summary and three booked entries.", []),
    ("camt053-valid-usd-overdraft", C_USD, CAMT, "valid", True, [], "USD statement that opens overdrawn (DBIT) and closes in credit; one pending entry is left out of the balance.", []),
    ("pacs008-valid-no-xml-declaration", P_TWO, PACS, "valid", True, [], "Same message without the <?xml?> declaration.",
     [('<?xml version="1.0" encoding="UTF-8"?>\n', "", 1)]),
    ("pacs008-valid-unknown-currency-warning", P_MIN, PACS, "valid", True, ["CURRENCY-UNKNOWN"], "A well-formed but non-ISO-4217 currency code: valid by schema, flagged as a warning.",
     [('Ccy="EUR">250.00', 'Ccy="ABC">250.00', 1)]),
    ("pacs008-valid-in-envelope", P_MIN, PACS, "valid", True, [], "The Document inside a business envelope next to a Business Application Header (head.001).",
     [('<Document xmlns', '<Envelope xmlns="urn:example:envelope">\n<AppHdr xmlns="urn:iso:std:iso:20022:tech:xsd:head.001.001.02"><Fr>x</Fr></AppHdr>\n<Document xmlns', 1),
      ('</Document>', '</Document>\n</Envelope>', 1)]),

    # ---- pacs.008 schema errors ----
    ("pacs008-schema-missing-end-to-end-id", P_MIN, PACS, "schema", False, ["SCHEMA"], "PmtId without its required EndToEndId.",
     [("<EndToEndId>E2E-1</EndToEndId>", "", 1)]),
    ("pacs008-schema-bad-bic", P_TWO, PACS, "schema", False, ["SCHEMA"], "A BIC that isn't 8 or 11 characters.",
     [("<BICFI>DEMOGB2L</BICFI>", "<BICFI>DEMOGB2</BICFI>", 1)]),
    ("pacs008-schema-negative-amount", P_MIN, PACS, "schema", False, ["SCHEMA"], "A negative settlement amount.",
     [('Ccy="EUR">250.00', 'Ccy="EUR">-250.00', 1)]),
    ("pacs008-schema-too-many-fraction-digits", P_MIN, PACS, "schema", False, ["SCHEMA"], "An amount with 6 decimals (the schema allows at most 5).",
     [('Ccy="EUR">250.00', 'Ccy="EUR">250.123456', 1)]),
    ("pacs008-schema-lowercase-currency", P_MIN, PACS, "schema", False, ["SCHEMA"], "Currency code in lower case.",
     [('Ccy="EUR">250.00', 'Ccy="eur">250.00', 1)]),
    ("pacs008-schema-missing-charge-bearer", P_MIN, PACS, "schema", False, ["SCHEMA"], "No ChrgBr.",
     [("<ChrgBr>SLEV</ChrgBr>", "", 1)]),
    ("pacs008-schema-bad-charge-bearer", P_MIN, PACS, "schema", False, ["SCHEMA"], "ChrgBr that isn't DEBT, CRED, SHAR or SLEV.",
     [("<ChrgBr>SLEV</ChrgBr>", "<ChrgBr>OURS</ChrgBr>", 1)]),
    ("pacs008-schema-wrong-order", P_MIN, PACS, "schema", False, ["SCHEMA"], "Cdtr before CdtrAgt (the schema fixes the order).",
     [("""      <CdtrAgt>
        <FinInstnId>
          <BICFI>DEMODEFF</BICFI>
        </FinInstnId>
      </CdtrAgt>
      <Cdtr>
        <Nm>Berlin Bikes GmbH</Nm>
      </Cdtr>""", """      <Cdtr>
        <Nm>Berlin Bikes GmbH</Nm>
      </Cdtr>
      <CdtrAgt>
        <FinInstnId>
          <BICFI>DEMODEFF</BICFI>
        </FinInstnId>
      </CdtrAgt>""", 1)]),
    ("pacs008-schema-unknown-element", P_MIN, PACS, "schema", False, ["SCHEMA"], "An element the schema doesn't define.",
     [("<ChrgBr>SLEV</ChrgBr>", "<ChrgBr>SLEV</ChrgBr>\n      <Fee>1.00</Fee>", 1)]),
    ("pacs008-schema-message-id-too-long", P_MIN, PACS, "schema", False, ["SCHEMA"], "MsgId of 36 characters (max 35).",
     [("<MsgId>DEMO-MIN-1</MsgId>", "<MsgId>" + "M" * 36 + "</MsgId>", 1)]),
    ("pacs008-schema-empty-name", P_MIN, PACS, "schema", False, ["SCHEMA"], "An empty debtor name (min length 1).",
     [("<Nm>Jane Doe</Nm>", "<Nm></Nm>", 1)]),
    ("pacs008-schema-bad-date", P_MIN, PACS, "schema", False, ["SCHEMA"], "A settlement date that doesn't exist.",
     [("<IntrBkSttlmDt>2026-11-05</IntrBkSttlmDt>", "<IntrBkSttlmDt>2026-13-40</IntrBkSttlmDt>", 1)]),
    ("pacs008-schema-bad-uetr", P_TWO, PACS, "schema", False, ["SCHEMA"], "A UETR that isn't a version 4 UUID.",
     [("<UETR>4f2b1c9e-7a3d-4e5f-9b8a-1c2d3e4f5a6b</UETR>", "<UETR>4f2b1c9e-7a3d-1e5f-9b8a-1c2d3e4f5a6b</UETR>", 1)]),
    ("pacs008-schema-bad-settlement-method", P_TWO, PACS, "schema", False, ["SCHEMA"], "SttlmMtd that isn't INDA, INGA, COVE or CLRG.",
     [("<SttlmMtd>INDA</SttlmMtd>", "<SttlmMtd>WIRE</SttlmMtd>", 1)]),
    ("pacs008-schema-iban-format", P_TWO, PACS, "schema", False, ["SCHEMA"], "An IBAN that doesn't start with a country code and two digits.",
     [("<IBAN>GB82WEST12345698765432</IBAN>", "<IBAN>8GBWEST12345698765432</IBAN>", 1)]),

    # ---- pacs.008 business rules ----
    ("pacs008-business-number-of-transactions", P_TWO, PACS, "business", False, ["PACS008-NBOFTXS"], "NbOfTxs says 3 but there are 2 transactions.",
     [("<NbOfTxs>2</NbOfTxs>", "<NbOfTxs>3</NbOfTxs>", 1)]),
    ("pacs008-business-control-sum", P_TWO, PACS, "business", False, ["PACS008-CTRLSUM"], "CtrlSum doesn't match the transaction amounts.",
     [("<CtrlSum>1500.25</CtrlSum>", "<CtrlSum>1500.00</CtrlSum>", 1)]),
    ("pacs008-business-total-amount", P_TWO, PACS, "business", False, ["PACS008-TOTAL"], "TtlIntrBkSttlmAmt doesn't match the transaction amounts.",
     [('<TtlIntrBkSttlmAmt Ccy="USD">1500.25</TtlIntrBkSttlmAmt>', '<TtlIntrBkSttlmAmt Ccy="USD">1400.25</TtlIntrBkSttlmAmt>', 1)]),
    ("pacs008-business-total-currency", P_TWO, PACS, "business", False, ["PACS008-TOTAL"], "One transaction in another currency than the total.",
     [('Ccy="USD">500.25', 'Ccy="EUR">500.25', 1)]),
    ("pacs008-business-usd-three-decimals", P_TWO, PACS, "business", False, ["AMOUNT-DECIMALS", "PACS008-CTRLSUM", "PACS008-TOTAL"], "500.255 USD: USD has 2 decimals.",
     [('Ccy="USD">500.25', 'Ccy="USD">500.255', 1)]),
    ("pacs008-business-jpy-decimals", P_MIN, PACS, "business", False, ["AMOUNT-DECIMALS"], "250.50 JPY: the yen has no decimals.",
     [('Ccy="EUR">250.00', 'Ccy="JPY">250.50', 1)]),
    ("pacs008-business-iban-check-digits", P_TWO, PACS, "business", False, ["IBAN-CHECKSUM"], "An IBAN with wrong check digits (well-formed, so the schema accepts it).",
     [("<IBAN>GB82WEST12345698765432</IBAN>", "<IBAN>GB83WEST12345698765432</IBAN>", 1)]),

    # ---- camt.053 schema errors ----
    ("camt053-schema-missing-bank-transaction-code", C_USD, CAMT, "schema", False, ["SCHEMA"], "An entry without BkTxCd (required).",
     [("""        <BkTxCd>
          <Prtry>
            <Cd>SALARY</Cd>
          </Prtry>
        </BkTxCd>
""", "", 1)]),
    ("camt053-schema-bad-credit-debit", C_EUR, CAMT, "schema", False, ["SCHEMA"], "CdtDbtInd that isn't CRDT or DBIT.",
     [("<CdtDbtInd>CRDT</CdtDbtInd>\n        <Sts>", "<CdtDbtInd>CR</CdtDbtInd>\n        <Sts>", 1)]),
    ("camt053-schema-missing-balance", C_USD, CAMT, "schema", False, ["SCHEMA"], "A statement with no Bal at all (at least one is required).",
     [("""      <Bal>
        <Tp>
          <CdOrPrtry>
            <Cd>OPBD</Cd>
          </CdOrPrtry>
        </Tp>
        <Amt Ccy="USD">200.00</Amt>
        <CdtDbtInd>DBIT</CdtDbtInd>
        <Dt>
          <Dt>2026-11-04</Dt>
        </Dt>
      </Bal>
      <Bal>
        <Tp>
          <CdOrPrtry>
            <Cd>CLBD</Cd>
          </CdOrPrtry>
        </Tp>
        <Amt Ccy="USD">800.00</Amt>
        <CdtDbtInd>CRDT</CdtDbtInd>
        <Dt>
          <Dt>2026-11-04</Dt>
        </Dt>
      </Bal>
""", "", 1)]),

    # ---- camt.053 business rules ----
    ("camt053-business-closing-balance", C_EUR, CAMT, "business", False, ["CAMT053-BALANCE"], "Closing balance 10.00 more than opening + credits − debits.",
     [('<Amt Ccy="EUR">11200.00</Amt>', '<Amt Ccy="EUR">11210.00</Amt>', 1)]),
    ("camt053-business-overdraft-sign", C_USD, CAMT, "business", False, ["CAMT053-BALANCE"], "Opening balance marked CRDT instead of DBIT: the statement no longer adds up.",
     [("""<Amt Ccy="USD">200.00</Amt>
        <CdtDbtInd>DBIT</CdtDbtInd>""", """<Amt Ccy="USD">200.00</Amt>
        <CdtDbtInd>CRDT</CdtDbtInd>""", 1)]),
    ("camt053-business-summary-count", C_EUR, CAMT, "business", False, ["CAMT053-SUMMARY"], "TtlNtries/NbOfNtries says 4 but there are 3 entries.",
     [("<NbOfNtries>3</NbOfNtries>", "<NbOfNtries>4</NbOfNtries>", 1)]),
    ("camt053-business-summary-debit-sum", C_EUR, CAMT, "business", False, ["CAMT053-SUMMARY"], "TtlDbtNtries/Sum doesn't match the debit entries.",
     [("<Sum>300.00</Sum>", "<Sum>250.50</Sum>", 1)]),
    ("camt053-business-summary-net", C_EUR, CAMT, "business", False, ["CAMT053-SUMMARY"], "TtlNetNtry has the wrong direction.",
     [("""<Amt>1200.00</Amt>
            <CdtDbtInd>CRDT</CdtDbtInd>""", """<Amt>1200.00</Amt>
            <CdtDbtInd>DBIT</CdtDbtInd>""", 1)]),
    ("camt053-business-eur-three-decimals", C_EUR, CAMT, "business", False, ["AMOUNT-DECIMALS", "CAMT053-BALANCE", "CAMT053-SUMMARY"], "49.505 EUR: the euro has 2 decimals.",
     [('<Amt Ccy="EUR">49.50</Amt>', '<Amt Ccy="EUR">49.505</Amt>', 1)]),
    ("camt053-business-iban-check-digits", C_EUR, CAMT, "business", False, ["IBAN-CHECKSUM"], "The statement account's IBAN has wrong check digits.",
     [("<IBAN>DE89370400440532013000</IBAN>", "<IBAN>DE88370400440532013000</IBAN>", 1)]),

    # ---- not well-formed, unsupported, security ----
    ("wellformed-unclosed-tag", P_MIN, PACS, "wellformed", False, ["XML-MALFORMED"], "An element that is never closed.",
     [("</Cdtr>", "", 1)]),
    ("wellformed-not-xml", None, None, "wellformed", False, ["XML-MALFORMED"], "Not XML at all.", "MsgId=1;Amount=100"),
    ("unsupported-other-version", P_MIN, PACS, "unsupported", False, ["MSG-UNSUPPORTED"], "pacs.008.001.99, a version that doesn't exist (namespace recognised, version not supported).",
     [("pacs.008.001.08", "pacs.008.001.99", 1)]),
    ("unsupported-not-iso", None, None, "unsupported", False, ["MSG-NO-DOCUMENT"], "Well-formed XML that isn't an ISO 20022 message.",
     '<?xml version="1.0"?>\n<Invoice><Number>445</Number></Invoice>\n'),
    ("security-xxe-entity", P_MIN, PACS, "security", False, ["XML-DTD"], "An external entity (XXE) trying to read a local file into the debtor name. Must be refused, not expanded.",
     [('<?xml version="1.0" encoding="UTF-8"?>\n', '<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE Document [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>\n', 1),
      ("<Nm>Jane Doe</Nm>", "<Nm>&xxe;</Nm>", 1)]),
    ("security-billion-laughs", P_MIN, PACS, "security", False, ["XML-DTD"], "Nested entities that expand to gigabytes (\"billion laughs\"). Must be refused quickly.",
     [('<?xml version="1.0" encoding="UTF-8"?>\n', '<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE Document [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;"><!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;"><!ENTITY e "&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;"><!ENTITY f "&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;"><!ENTITY g "&f;&f;&f;&f;&f;&f;&f;&f;&f;&f;"><!ENTITY h "&g;&g;&g;&g;&g;&g;&g;&g;&g;&g;">]>\n', 1),
      ("<Nm>Jane Doe</Nm>", "<Nm>&h;</Nm>", 1)]),
]


def main():
    OUT.mkdir(exist_ok=True)
    for old in OUT.glob("*.xml"):
        old.unlink()
    suite = []
    for case_id, base, message, category, valid, codes, description, changes in CASES:
        if base is None:
            text = changes  # literal content
        else:
            text = (BASE / base).read_text(encoding="utf-8")
            for old, new, count in changes:
                found = text.count(old)
                if found < count:
                    raise SystemExit(f"{case_id}: expected {count} of {old!r} in {base}, found {found}")
                text = text.replace(old, new, count)
        file = f"cases/{case_id}.xml"
        (HERE / file).write_text(text, encoding="utf-8")
        suite.append({
            "id": case_id,
            "file": file,
            "message": message,
            "category": category,
            "description": description,
            "expect": {"valid": valid, "codes": codes},
        })
    manifest = {
        "name": "Iso20022Kit conformance suite",
        "version": "0.1.0",
        "description": "Valid and deliberately broken ISO 20022 messages with the expected verdict. "
                       "Any validator in any language can run it: see conformance/README.md.",
        "categories": {
            "valid": "A correct message. Expected: accepted.",
            "schema": "Breaks the official XSD. Any schema-validating library should reject it.",
            "business": "Matches the XSD but breaks a rule the schema can't express (totals, balances, ISO 4217 decimals, IBAN check digits).",
            "wellformed": "Not well-formed XML.",
            "unsupported": "Not a supported ISO 20022 message.",
            "security": "Hostile XML (DTD entities). Must be refused, never expanded.",
        },
        "cases": suite,
    }
    (HERE / "suite.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(suite)} cases written")


if __name__ == "__main__":
    main()
