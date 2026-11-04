// Runs every case of the conformance suite through the browser validator and checks it reaches the same
// verdict and the same set of codes as the .NET library (the expectations in suite.json).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { validate, compareDecimal, ibanValid } from "../src/validate";

const root = join(process.cwd(), "..", "conformance");
interface Case { id: string; file: string; category: string; expect: { valid: boolean; codes: string[] } }
const suite: { cases: Case[] } = JSON.parse(readFileSync(join(root, "suite.json"), "utf8"));

for (const c of suite.cases) {
  test(`${c.category}: ${c.id}`, () => {
    const xml = readFileSync(join(root, c.file), "utf8");
    const started = performance.now();
    const r = validate(xml);
    assert.ok(performance.now() - started < 2000, "took more than 2 s");
    const codes = [...new Set(r.issues.map((i) => i.code))].sort();
    const detail = r.issues.map((i) => `${i.code} ${i.line}:${i.col} ${i.message}`).join("\n");
    assert.equal(r.valid, c.expect.valid, detail);
    assert.deepEqual(codes, [...c.expect.codes].sort(), detail);
    for (const i of r.issues) assert.ok(i.line && i.line > 0, `${i.code} has no line`);
  });
}

test("decimals compare exactly", () => {
  assert.equal(compareDecimal("0.1", "0.10"), 0);
  assert.equal(compareDecimal("-0.01", "0"), -1);
  assert.equal(compareDecimal("99999999999999999.99", "99999999999999999.98"), 1);
});

test("IBAN check digits", () => {
  assert.ok(ibanValid("GB82WEST12345698765432"));
  assert.ok(ibanValid("DE89 3704 0044 0532 0130 00"));
  assert.ok(!ibanValid("GB83WEST12345698765432"));
});

test("a CtrlSum with an extra decimal is not rounded into agreement", () => {
  const xml = readFileSync(join(root, "base", "pacs008-two-transactions.xml"), "utf8");
  const m = /<CtrlSum>([^<]+)<\/CtrlSum>/.exec(xml);
  assert.ok(m, "base has a CtrlSum");
  const r = validate(xml.replace(m![0], `<CtrlSum>${m![1]}1</CtrlSum>`));
  assert.ok(r.issues.some((i) => i.code === "PACS008-CTRLSUM" || i.code === "SCHEMA"));
});

test("issues point at the exact line and column", () => {
  const at = (id: string) => validate(readFileSync(join(root, "cases", `${id}.xml`), "utf8")).issues.map((i) => `${i.code}@${i.line}:${i.col}`);
  assert.deepEqual(at("pacs008-schema-bad-bic"), ["SCHEMA@103:11"]);
  assert.deepEqual(at("pacs008-schema-lowercase-currency"), ["SCHEMA@19:23"]);
  assert.deepEqual(at("wellformed-unclosed-tag"), ["XML-MALFORMED@48:5"]);
  assert.ok(at("camt053-business-eur-three-decimals").includes("AMOUNT-DECIMALS@157:9"));
  assert.deepEqual(at("security-xxe-entity"), ["XML-DTD@2:1"]);
});

test("CRLF line endings and a byte-order mark don't shift positions", () => {
  const xml = readFileSync(join(root, "cases", "pacs008-schema-bad-bic.xml"), "utf8");
  const r = validate("\uFEFF" + xml.replace(/\n/g, "\r\n"));
  assert.deepEqual(r.issues.map((i) => `${i.line}:${i.col}`), ["103:11"]);
});
