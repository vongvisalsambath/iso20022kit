// Inputs that try to break, mislead or slow down the validator. tests/hostile/cases.json is also run by the .NET
// tests with the same expectations, so the browser and .NET implementations can't drift apart.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { validate, cleanText, MAX_ISSUES } from "../src/validate";
import { parseXml, decodeXml, pathOf, MAX_DEPTH, xmlTrim } from "../src/xml";
import { explain } from "../src/explain";
import { label, expand, FIELDS, CODES } from "../src/names";

const hostile = join(process.cwd(), "..", "tests", "hostile");
const base = join(process.cwd(), "..", "conformance", "base");
interface Case { id: string; file: string; expect: { valid: boolean; codes: string[] } }
const cases: Case[] = JSON.parse(readFileSync(join(hostile, "cases.json"), "utf8")).cases;
const codes = (r: { issues: { code: string }[] }) => [...new Set(r.issues.map((i) => i.code))].sort();

/** What the page does with a file: decode the bytes strictly, then validate. */
function check(bytes: Uint8Array) {
  const { text, error } = decodeXml(bytes);
  const r = validate(text);
  return error ? { ...r, valid: false, issues: [{ severity: "error" as const, code: error.code, message: error.message }] } : r;
}

for (const c of cases) {
  test(`hostile: ${c.id}`, () => {
    const r = check(new Uint8Array(readFileSync(join(hostile, c.file))));
    const detail = r.issues.map((i) => `${i.code}: ${i.message}`).join("\n");
    assert.equal(r.valid, c.expect.valid, detail);
    assert.deepEqual(codes(r), [...c.expect.codes].sort(), detail);
    // the UI must be able to explain and label whatever came through
    if (r.document) {
      explain(r.message, r.document);
      const walk = (e: NonNullable<typeof r.document>) => { assert.equal(typeof label(e.local).name, "string"); e.children.forEach(walk); };
      walk(r.document);
    }
  });
}

test("issue messages never contain hidden characters", () => {
  const hidden = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u00AD\u061C\u200B-\u200F\u2028-\u202E\u2060-\u2064\u2066-\u2069\uFEFF]/;
  for (const f of readdirSync(hostile).filter((f) => f.endsWith(".xml"))) {
    for (const i of check(new Uint8Array(readFileSync(join(hostile, f)))).issues) {
      assert.ok(!hidden.test(i.message), `${f}: ${JSON.stringify(i.message)}`);
    }
  }
  const bidi = validate(readFileSync(join(hostile, "bidi-override-in-name.xml"), "utf8")).issues[0];
  assert.match(bidi.message, /\\u202E \(RIGHT-TO-LEFT OVERRIDE\)/);
});

// ---- size and shape: refused or finished quickly, never a hang or a stack overflow

const pacs = readFileSync(join(base, "pacs008-minimal.xml"), "utf8");
const camt = readFileSync(join(base, "camt053-eur-iban.xml"), "utf8");
function timed(xml: string, maxMs: number) {
  const t = performance.now();
  const r = validate(xml);
  const ms = performance.now() - t;
  assert.ok(ms < maxMs, `took ${Math.round(ms)} ms`);
  return r;
}

test("deep nesting is refused quickly", () => {
  const d = 200_000;
  const r = timed(pacs.replace("<Nm>Jane Doe</Nm>", "<Nm>Jane Doe</Nm>" + "<X>".repeat(d) + "</X>".repeat(d)), 2000);
  assert.deepEqual(codes(r), ["XML-MALFORMED"]);
  assert.match(r.issues[0].message, /nested more than 256 levels/);
});

test("the depth limit sits exactly where .NET's does", () => {
  const nest = (n: number) => "<E>".repeat(n) + "</E>".repeat(n);
  assert.deepEqual(codes(validate(nest(MAX_DEPTH))), ["MSG-NO-DOCUMENT"]);
  assert.deepEqual(codes(validate(nest(MAX_DEPTH + 1))), ["XML-MALFORMED"]);
});

test("a million siblings on one line finish", () => {
  // the newline search once scanned to the end of the input for every element of a one-line file
  assert.deepEqual(codes(timed("<E>" + "<a/>".repeat(1_000_000) + "</E>", 8000)), ["MSG-NO-DOCUMENT"]);
});

test("one-line (minified) messages are as fast as indented ones", () => {
  const entry = /<Ntry>[\s\S]*?<\/Ntry>/.exec(camt)![0];
  const big = camt.replace(/<TxsSummry>[\s\S]*?<\/TxsSummry>\s*/, "").replace(/<Ntry>[\s\S]*<\/Ntry>/, Array(5000).fill(entry).join("\n"))
    .replace('<Amt Ccy="EUR">11200.00</Amt>', `<Amt Ccy="EUR">${(10000 + 1500 * 5000).toFixed(2)}</Amt>`);
  const flat = big.replace(/>\s+</g, "><");
  assert.equal(timed(flat, 8000).valid, true);
});

test("thousands of errors stop at the limit", () => {
  const attrs = pacs.replace("<Nm>Jane Doe</Nm>", "<Nm " + Array.from({ length: 100_000 }, (_, i) => `a${i}="1"`).join(" ") + ">Jane Doe</Nm>");
  const r = timed(attrs, 5000);
  assert.equal(r.issues.length, MAX_ISSUES + 1);
  assert.equal(r.issues[r.issues.length - 1].code, "TOO-MANY-ISSUES");
  assert.equal(r.valid, false);

  const entry = /<Ntry>[\s\S]*?<\/Ntry>/.exec(camt)![0].replace(">1500.00<", ">1.001<");
  const many = camt.replace(/<TxsSummry>[\s\S]*?<\/TxsSummry>\s*/, "").replace(/<Ntry>[\s\S]*<\/Ntry>/, Array(20_000).fill(entry).join("\n"));
  const rules = timed(many, 15000);
  assert.equal(rules.issues.length, MAX_ISSUES + 1);
  assert.ok(rules.issues.some((i) => i.path === "/Document/BkToCstmrStmt/Stmt/Ntry[1000]/Amt"));
});

test("long values are not repeated in full", () => {
  const r = validate(pacs.replace(/<BICFI>[^<]*<\/BICFI>/, "<BICFI>" + "A".repeat(2_000_000) + "</BICFI>"));
  const m = r.issues[0].message;
  assert.ok(m.length < 700, `${m.length} characters`);
  assert.match(m, /characters omitted/);
});

test("inputs over the size limit are refused", () => {
  assert.deepEqual(codes(validate("<a>" + "x".repeat(50_000_001) + "</a>")), ["XML-MALFORMED"]);
});

// ---- object keys that JavaScript gives special meaning

test("names like __proto__ and constructor are ordinary missing keys", () => {
  for (const k of ["__proto__", "constructor", "toString", "hasOwnProperty", "valueOf"]) {
    assert.equal(typeof expand(k), "string");
    assert.equal(typeof label(k).name, "string");
    assert.equal(FIELDS[k], undefined);
    assert.equal(CODES[k], undefined);
    assert.equal(CODES.Cd[k], undefined);
  }
  // a currency code like "constructor" isn't a known currency (Object.prototype once said it was)
  const r = validate(pacs.replace('Ccy="EUR"', 'Ccy="constructor"'));
  assert.deepEqual(codes(r), ["SCHEMA"]);
});

test("a namespace scope can declare __proto__ without changing other prefixes", () => {
  const root = parseXml('<a xmlns:__proto__="urn:x" xmlns:p="urn:p"><p:b __proto__:c="1"/></a>');
  assert.equal(root.children[0].ns, "urn:p");
  assert.equal(root.children[0].attrs[0].ns, "urn:x");
});

// ---- decoding bytes like an XML reader

test("bytes are decoded by BOM, then declaration, and never silently replaced", () => {
  const enc = new TextEncoder();
  assert.equal(decodeXml(enc.encode("\uFEFF<a/>")).text, "<a/>");
  assert.equal(decodeXml(new Uint8Array([0xff, 0xfe, 0x3c, 0, 0x61, 0, 0x2f, 0, 0x3e, 0])).text, "<a/>");
  assert.equal(decodeXml(new Uint8Array([0xfe, 0xff, 0, 0x3c, 0, 0x61, 0, 0x2f, 0, 0x3e])).text, "<a/>");
  const latin = new Uint8Array([...enc.encode('<?xml version="1.0" encoding="ISO-8859-1"?><a>'), 0xe9, ...enc.encode("</a>")]);
  assert.equal(decodeXml(latin).text.includes("é"), true);
  const bad = decodeXml(new Uint8Array([...enc.encode("<a>\n"), 0xc3, 0x28, ...enc.encode("</a>")]));
  assert.equal(bad.error?.code, "XML-MALFORMED");
  assert.equal(bad.error?.line, 2);
  assert.equal(decodeXml(enc.encode('<?xml version="1.0" encoding="x-made-up"?><a/>')).error?.code, "XML-MALFORMED");
});

// ---- happy paths that the hardening must not break

test("every conformance base message is still valid, in every line-ending style", () => {
  for (const f of readdirSync(base)) {
    const xml = readFileSync(join(base, f), "utf8");
    for (const v of [xml, xml.replace(/\n/g, "\r\n"), xml.replace(/\n/g, "\r"), "\uFEFF" + xml, xml.replace(/>\s+</g, "><")]) {
      const r = validate(v);
      assert.equal(r.valid, true, `${f}: ${r.issues.map((i) => i.message).join("; ")}`);
      assert.equal(r.issues.length, 0, f);
    }
  }
});

test("predefined entities, character references, CDATA and comments still read correctly", () => {
  const r = parseXml(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<!-- a comment --><?pi data?><a x='&lt;&amp;&#65;&#x42;' y="a&#9;b
c"><![CDATA[<raw>]]>&quot;&apos;&gt;<b/></a>`);
  assert.equal(r.attrs[0].value, "<&AB");
  assert.equal(r.attrs[1].value, "a\tb c"); // a literal newline in an attribute reads as a space; &#9; stays a tab
  assert.equal(r.text, "<raw>\"'>");
  assert.equal(xmlTrim("\u00a0 x \n"), "\u00a0 x");
});

test("paths index repeated elements correctly", () => {
  const root = parseXml("<r><a/><b/><a><c/></a><a/></r>");
  assert.equal(pathOf(root.children[2].children[0]), "/r/a[2]/c");
  assert.equal(pathOf(root.children[1]), "/r/b");
  assert.equal(pathOf(root.children[3], "x"), "/r/a[3]/@x");
});

test("cleanText keeps short text and surrogate pairs intact", () => {
  assert.equal(cleanText("Renée 😀"), "Renée 😀");
  const long = "😀".repeat(1000);
  const c = cleanText(long);
  assert.ok(!/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(c), "cut a surrogate pair in half");
});
