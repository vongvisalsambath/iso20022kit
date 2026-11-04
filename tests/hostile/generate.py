#!/usr/bin/env python3
"""Builds tests/hostile/: inputs that try to break, mislead or slow down a validator, each with the verdict and codes
both Iso20022Kit implementations (.NET and the browser viewer) must give. Run: python3 tests/hostile/generate.py

Each case is a valid base message from conformance/base changed in one hostile way. Very large inputs (deep nesting,
a million elements, thousands of errors) aren't stored: the test suites generate them.
"""
import json, pathlib, re

HERE = pathlib.Path(__file__).parent
BASE = HERE.parent.parent / "conformance" / "base"
pacs = (BASE / "pacs008-minimal.xml").read_text()
pacs2 = (BASE / "pacs008-two-transactions.xml").read_text()
NS = 'xmlns="urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08"'
NM = "<Nm>Jane Doe</Nm>"
XSI = 'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"'


def sub(s, a, b):
    assert a in s, a
    return s.replace(a, b, 1)


MALFORMED, VALID = (False, ["XML-MALFORMED"]), (True, [])
CASES = [
    # id, expect (valid, codes), why, content (str written as UTF-8, or bytes)
    # --- characters XML doesn't allow, and references to them
    ("char-control-raw", MALFORMED, "A raw U+0001 control character.", sub(pacs, NM, "<Nm>Jane\x01Doe</Nm>")),
    ("char-control-ref", MALFORMED, "&#1; refers to a character XML 1.0 forbids.", sub(pacs, NM, "<Nm>Jane&#1;Doe</Nm>")),
    ("char-nul-ref", MALFORMED, "&#0; (NUL).", sub(pacs, NM, "<Nm>Jane&#0;Doe</Nm>")),
    ("char-surrogate-ref", MALFORMED, "&#xD800; is half a surrogate pair, not a character.", sub(pacs, NM, "<Nm>Jane&#xD800;Doe</Nm>")),
    ("char-beyond-unicode-ref", MALFORMED, "&#x110000; is past the last Unicode code point (crashed the viewer).", sub(pacs, NM, "<Nm>Jane&#x110000;Doe</Nm>")),
    ("char-huge-ref", MALFORMED, "A 20-digit character reference (crashed the viewer).", sub(pacs, NM, "<Nm>Jane&#99999999999999999999;Doe</Nm>")),
    ("char-fffe-raw", MALFORMED, "U+FFFE is a noncharacter.", sub(pacs, NM, "<Nm>Jane￾Doe</Nm>")),
    ("char-fffe-ref", MALFORMED, "&#xFFFE;.", sub(pacs, NM, "<Nm>Jane&#xFFFE;Doe</Nm>")),
    # --- names a JavaScript object already has
    ("entity-constructor", MALFORMED, "&constructor; is not a predefined entity (the viewer once read it from Object.prototype).", sub(pacs, NM, "<Nm>Jane&constructor;Doe</Nm>")),
    ("entity-proto", MALFORMED, "&__proto__;.", sub(pacs, NM, "<Nm>Jane&__proto__;Doe</Nm>")),
    ("prefix-tostring", MALFORMED, "Undeclared prefix toString: (resolved to a function in the viewer).",
     sub(pacs, "<FIToFICstmrCdtTrf>", "<toString:FIToFICstmrCdtTrf>").replace("</FIToFICstmrCdtTrf>", "</toString:FIToFICstmrCdtTrf>")),
    ("prefix-proto", MALFORMED, "Undeclared prefix __proto__:.",
     sub(pacs, "<FIToFICstmrCdtTrf>", "<__proto__:FIToFICstmrCdtTrf>").replace("</FIToFICstmrCdtTrf>", "</__proto__:FIToFICstmrCdtTrf>")),
    ("xmlns-proto-declared", VALID, "Declaring a prefix named __proto__ is legal XML and changes nothing.", sub(pacs, "<FIToFICstmrCdtTrf>", '<FIToFICstmrCdtTrf xmlns:__proto__="urn:x">')),
    ("element-proto", (False, ["SCHEMA"]), "Elements named __proto__ and constructor (crashed the viewer's field tree).",
     sub(pacs, "<FIToFICstmrCdtTrf>", "<FIToFICstmrCdtTrf><__proto__/><constructor/>")),
    # --- well-formedness rules
    ("endtag-other-prefix", MALFORMED, "</b:Document> closes <a:Document>: same namespace, different name as written.",
     pacs.replace("<Document " + NS + ">", '<a:Document xmlns:a="urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08" xmlns:b="urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08" ' + NS + ">").replace("</Document>", "</b:Document>")),
    ("comment-double-hyphen", MALFORMED, "'--' inside a comment.", sub(pacs, NM, "<!-- a -- b -->" + NM)),
    ("xml-declaration-late", MALFORMED, "Whitespace before the XML declaration.", "\n" + pacs),
    ("xml-declaration-inside", MALFORMED, "An XML declaration inside the document.", sub(pacs, NM, "<?xml version='1.0'?>" + NM)),
    ("xml-version-1-1", MALFORMED, "XML 1.1 (different character rules) isn't supported.", pacs.replace('version="1.0"', 'version="1.1"', 1)),
    ("name-two-colons", MALFORMED, "A name with two colons.", sub(pacs, NM, '<Nm a:b:c="1">Jane Doe</Nm>')),
    ("attribute-duplicate-expanded", MALFORMED, "p:a and q:a with p and q bound to the same namespace are the same attribute.",
     sub(pacs, NM, '<Nm xmlns:p="urn:x" xmlns:q="urn:x" p:a="1" q:a="2">Jane Doe</Nm>')),
    ("xmlns-prefix-empty", MALFORMED, 'xmlns:p="" (undeclaring a prefix is XML 1.1 only).', sub(pacs, NM, '<Nm xmlns:p="">Jane Doe</Nm>')),
    ("nbsp-before-root", MALFORMED, "A no-break space is not XML whitespace.", pacs.replace("<Document", " <Document", 1)),
    # --- what the schema must catch, however it's spelled
    ("nbsp-in-element-only", (False, ["SCHEMA"]), "A no-break space is text, and GrpHdr may only hold elements.", sub(pacs, "<GrpHdr>", "<GrpHdr> ")),
    ("nbsp-after-amount", (False, ["SCHEMA"]), "A no-break space after a decimal isn't collapsed away.",
     re.sub(r'(<IntrBkSttlmAmt Ccy="EUR">)([^<]+)', lambda m: m.group(1) + m.group(2) + " ", pacs, count=1)),
    ("attribute-foreign-namespace", (False, ["SCHEMA"]), "An attribute in another namespace isn't declared by the schema.", sub(pacs, NM, '<Nm xmlns:x="urn:evil" x:id="1">Jane Doe</Nm>')),
    ("element-foreign-namespace", (False, ["SCHEMA"]), "An element in another namespace.", sub(pacs, NM, NM + '<x:Extra xmlns:x="urn:evil"/>')),
    ("xsi-nil", (False, ["SCHEMA"]), "xsi:nil on an element that isn't nillable.", sub(pacs, NM, f'<Nm {XSI} xsi:nil="true"></Nm>')),
    ("xsi-type", (False, ["SCHEMA"]), "xsi:type replacing GrpHdr's type with xs:string.",
     sub(pacs, "<GrpHdr>", f'<GrpHdr {XSI} xsi:type="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema">')),
    ("xml-lang", VALID, "xml:lang is allowed anywhere.", sub(pacs, NM, '<Nm xml:lang="en">Jane Doe</Nm>')),
    ("xsi-schemalocation-remote", VALID, "schemaLocation pointing at a server: ignored, never fetched.",
     sub(pacs, "<Document " + NS + ">", f'<Document {NS} {XSI} xsi:schemaLocation="urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08 http://127.0.0.1:47811/evil.xsd" xsi:noNamespaceSchemaLocation="http://127.0.0.1:47811/evil2.xsd">')),
    ("xsi-schemalocation-file", VALID, "schemaLocation pointing at a local file: ignored, never read.",
     sub(pacs, "<Document " + NS + ">", f'<Document {NS} {XSI} xsi:schemaLocation="urn:other file:///etc/passwd">')),
    # --- which message is it, really
    ("namespace-trailing-space", (False, ["MSG-UNKNOWN"]), "A trailing space made .NET check the message against nothing and call it valid.",
     pacs.replace('pacs.008.001.08"', 'pacs.008.001.08 "', 1)),
    ("namespace-arabic-digits", (False, ["MSG-UNKNOWN"]), "Arabic-Indic digits in the version (.NET's \\d matched them).",
     pacs.replace('pacs.008.001.08"', 'pacs.٠٠٨.001.08"', 1)),
    ("two-documents", (False, ["MSG-MULTIPLE-DOCUMENTS"]), "An envelope with two Documents: only the first used to be checked.",
     '<?xml version="1.0"?>\n<Envelope>' + pacs.split("?>", 1)[1] + pacs2.split("?>", 1)[1].replace("1500.25", "9999.99") + "</Envelope>"),
    # --- encodings (stored as bytes)
    ("utf8-invalid-bytes", MALFORMED, "Bytes that aren't UTF-8 (once silently replaced with U+FFFD).", sub(pacs, NM, "<Nm>Jane XXDoe</Nm>").encode().replace(b"XX", b"\xc3\x28")),
    ("utf16le-bom", VALID, "UTF-16 with a byte-order mark.", b"\xff\xfe" + pacs.replace('encoding="UTF-8"', 'encoding="UTF-16"').encode("utf-16-le")),
    ("latin1-declared", VALID, "Declared ISO-8859-1, with é as one byte.", sub(pacs, NM, "<Nm>Renée Doe</Nm>").replace('encoding="UTF-8"', 'encoding="ISO-8859-1"').encode("latin-1")),
    # --- line endings: XML reads CRLF and CR as LF
    ("crlf-at-max-length", VALID, "140 characters once CRLF reads as LF (175 if it didn't).", sub(pacs, NM, "<Nm>" + ("ab\r\n" * 35) + "</Nm>")),
    ("cr-only", VALID, "Old Mac line endings.", pacs.replace("\n", "\r")),
    # --- display spoofing
    ("bidi-override-in-name", (True, ["HIDDEN-CHARACTERS"]), "A right-to-left override: 'Doe 0001 DSU' displays reversed.", sub(pacs, NM, "<Nm>Jane \u202EDoe 0001 DSU</Nm>")),
    ("zero-width-in-bic-name", (True, ["HIDDEN-CHARACTERS"]), "A zero-width space hides inside a name.", sub(pacs, NM, "<Nm>Jane\u200BDoe</Nm>")),
    ("c1-control", (True, ["HIDDEN-CHARACTERS"]), "U+009B, a terminal escape in some terminals.", sub(pacs, NM, "<Nm>Jane\u009b31mDoe</Nm>")),
    ("markup-in-text", VALID, "Escaped HTML in a name stays text: it must never become an element in the viewer.",
     sub(pacs, NM, "<Nm>&lt;img src=x onerror=alert(1)&gt;&lt;script&gt;alert(2)&lt;/script&gt;</Nm>")),
]


def main():
    for f in HERE.glob("*.xml"):
        f.unlink()
    out = []
    for id_, (valid, codes), why, content in CASES:
        name = f"{id_}.xml"
        data = content if isinstance(content, bytes) else content.encode("utf-8")
        (HERE / name).write_bytes(data)
        out.append({"id": id_, "file": name, "description": why, "expect": {"valid": valid, "codes": sorted(codes)}})
    (HERE / "cases.json").write_text(json.dumps({"cases": out}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(out)} cases written to tests/hostile/")


if __name__ == "__main__":
    main()
