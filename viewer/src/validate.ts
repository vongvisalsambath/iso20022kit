// Validates an ISO 20022 message in the browser: the official schema (via the JSON model generated from the XSD
// by tools/SchemaModel) and the same business rules as the .NET library. Codes match docs/rules.md, and the
// conformance suite checks both give the same verdicts.
import { parseXml, pathOf, child, children, text, attr, XmlError, isXmlSpace, xmlTrim, XMLNS, XML_NS, type XElement, type XAttr } from "./xml";
import pacs008 from "./schemas/pacs.008.001.08.json";
import camt053 from "./schemas/camt.053.001.08.json";

export type Severity = "error" | "warning";
export interface Issue { severity: Severity; code: string; message: string; path?: string; line?: number; col?: number }

interface ChildDef { name?: string; type?: string; min: number; max: number; any?: boolean }
type TypeDef =
  | { kind: "simple"; base: string; patterns?: string[]; enum?: string[]; minLength?: number; maxLength?: number; length?: number; totalDigits?: number; fractionDigits?: number; minInclusive?: string; maxInclusive?: string; minExclusive?: string }
  | { kind: "simpleContent"; value: string; attributes: { name: string; type: string; required: boolean }[] }
  | { kind: "complex"; content: "sequence" | "choice" | "empty"; children: ChildDef[] };
export interface SchemaModel { message: string; namespace: string; root: string; rootType: string; types: Record<string, TypeDef> }

export const MODELS: Record<string, SchemaModel> = {
  "pacs.008.001.08": pacs008 as unknown as SchemaModel,
  "camt.053.001.08": camt053 as unknown as SchemaModel,
};
export const SUPPORTED = Object.keys(MODELS);
const XSI = "http://www.w3.org/2001/XMLSchema-instance";
/** Stop after this many issues, as the .NET library does (ValidationOptions.MaxIssues). */
export const MAX_ISSUES = 1000;
class IssueLimitReached extends Error {}

/** Every check, with a one-line description (the same text as the .NET library's RuleDescriptions). */
export const RULES: Record<string, string> = {
  SCHEMA: "The message must match the official ISO 20022 schema for its type and version.",
  "AMOUNT-DECIMALS": "An amount may not have more decimals than its currency allows in ISO 4217 (USD 2, JPY 0, KWD 3).",
  "CURRENCY-UNKNOWN": "Warning: the currency code is not an active ISO 4217 code with minor units.",
  "HIDDEN-CHARACTERS": "Warning: a value contains an invisible or direction-changing character (e.g. a right-to-left override), which can make it display differently from what it is.",
  "IBAN-CHECKSUM": "An IBAN's two check digits must be correct (ISO 13616 mod 97).",
  "PACS008-NBOFTXS": "pacs.008: GrpHdr/NbOfTxs must equal the number of CdtTrfTxInf transactions.",
  "PACS008-CTRLSUM": "pacs.008: GrpHdr/CtrlSum, when present, must equal the sum of every IntrBkSttlmAmt.",
  "PACS008-TOTAL": "pacs.008: GrpHdr/TtlIntrBkSttlmAmt, when present, must equal the sum of every IntrBkSttlmAmt, all in its currency.",
  "CAMT053-BALANCE": "camt.053: opening booked balance + booked credits − booked debits must equal the closing booked balance.",
  "CAMT053-SUMMARY": "camt.053: TxsSummry counts and sums must match the entries in the statement.",
};
/** Rules that can be switched off (the schema check can't). */
export const OPTIONAL_RULES = Object.keys(RULES).filter((r) => r !== "SCHEMA");
const NS_PREFIX = "urn:iso:std:iso:20022:tech:xsd:";

export interface Result {
  message: string | null;
  issues: Issue[];
  valid: boolean;
  document: XElement | null;
  root: XElement | null;
}

export function detect(xml: string): string | null {
  try { return findDocuments(parseXml(xml))[0]?.ns.slice(NS_PREFIX.length) ?? null; } catch { return null; }
}

/** The ISO Document elements: the root, or up to two found inside an envelope (two is an error). */
function findDocuments(root: XElement): XElement[] {
  const isDoc = (e: XElement) => e.local === "Document" && e.ns.startsWith(NS_PREFIX);
  if (isDoc(root)) return [root];
  const found: XElement[] = [];
  const queue = [...root.children];
  for (let k = 0; k < queue.length && found.length < 2; k++) { // an index, not shift(): shift() is O(n), so a wide file took minutes
    const e = queue[k];
    if (isDoc(e)) found.push(e);
    else for (const c of e.children) queue.push(c);
  }
  return found;
}

export function validate(xml: string, disabled: string[] = []): Result {
  let root: XElement;
  try {
    root = parseXml(xml);
  } catch (e) {
    if (e instanceof XmlError) return done(null, [{ severity: "error", code: e.code, message: e.message, line: e.line, col: e.col }], null, null);
    throw e;
  }
  const docs = findDocuments(root);
  if (docs.length > 1) {
    return done(null, [{ severity: "error", code: "MSG-MULTIPLE-DOCUMENTS", path: pathOf(docs[1]), line: docs[1].line, col: docs[1].col,
      message: "The input holds more than one ISO 20022 Document element. Check and send them one at a time." }], null, root);
  }
  const doc = docs[0];
  if (!doc) {
    return done(null, [{ severity: "error", code: "MSG-NO-DOCUMENT", path: "/" + root.local, line: root.line, col: root.col,
      message: `No ISO 20022 Document element found. The root element is <${root.local}> in namespace "${root.ns}"; expected <Document xmlns="${NS_PREFIX}…">.` }], null, root);
  }
  const id = doc.ns.slice(NS_PREFIX.length);
  if (!/^[a-z]{4}\.\d{3}\.\d{3}\.\d{2}$/.test(id)) {
    return done(null, [{ severity: "error", code: "MSG-UNKNOWN", path: "/Document", line: doc.line, col: doc.col, message: `"${doc.ns}" is not a recognisable ISO 20022 message namespace.` }], doc, root);
  }
  const model = MODELS[id];
  if (!model) {
    const family = id.slice(0, 8);
    const same = SUPPORTED.filter((s) => s.startsWith(family));
    return done(id, [{ severity: "error", code: "MSG-UNSUPPORTED", path: "/Document", line: doc.line, col: doc.col,
      message: `${id} is not supported yet. Supported messages: ${SUPPORTED.join(", ")}.${same.length ? ` For ${family}, only ${same.join(" and ")} ${same.length === 1 ? "is" : "are"} supported.` : ""}` }], doc, root);
  }
  const issues: Issue[] = [];
  try {
    new SchemaChecker(model, issues).element(doc, model.rootType);
    if (!issues.length) runRules(id, doc, issues, disabled);
  } catch (e) {
    if (!(e instanceof IssueLimitReached)) throw e;
    issues.push({ severity: "warning", code: "TOO-MANY-ISSUES", message: `Stopped after ${MAX_ISSUES} issues; the message may have more.` });
  }
  issues.sort((a, b) => (a.severity === b.severity ? 0 : a.severity === "error" ? -1 : 1) || (a.line ?? 1e9) - (b.line ?? 1e9) || (a.col ?? 1e9) - (b.col ?? 1e9));
  return done(id, issues, doc, root);
}

function done(message: string | null, issues: Issue[], document: XElement | null, root: XElement | null): Result {
  // Messages and paths quote the input: make hidden characters visible and keep the length sane.
  const cleaned = issues.map((i) => ({ ...i, message: cleanText(i.message), path: i.path === undefined ? undefined : cleanText(i.path) }));
  return { message, issues: cleaned, valid: !issues.some((i) => i.severity === "error"), document, root };
}

/** Characters that can't be seen, or that change how the text around them is displayed (same list as the .NET library). */
export function isHidden(c: number): boolean {
  return (c < 0x20 && c !== 9 && c !== 10 && c !== 13) || (c >= 0x7f && c <= 0x9f) || c === 0xad || c === 0x061c || c === 0x180e ||
    (c >= 0x200b && c <= 0x200f) || (c >= 0x2028 && c <= 0x202e) || (c >= 0x2060 && c <= 0x2064) || (c >= 0x2066 && c <= 0x2069) ||
    c === 0xfeff || (c >= 0xfff9 && c <= 0xfffb);
}
export const HIDDEN = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u00AD\u061C\u180E\u200B-\u200F\u2028-\u202E\u2060-\u2064\u2066-\u2069\uFEFF\uFFF9-\uFFFB]/;
const HIDDEN_ALL = new RegExp(HIDDEN.source + "|[\\uD800-\\uDBFF](?![\\uDC00-\\uDFFF])|(?<![\\uD800-\\uDBFF])[\\uDC00-\\uDFFF]", "g");
export const escapeChar = (c: number) => "\\u" + c.toString(16).toUpperCase().padStart(4, "0");
export function describeChar(c: number): string {
  const names: Record<number, string> = {
    0x202e: "RIGHT-TO-LEFT OVERRIDE", 0x202d: "LEFT-TO-RIGHT OVERRIDE", 0x202b: "RIGHT-TO-LEFT EMBEDDING", 0x202a: "LEFT-TO-RIGHT EMBEDDING",
    0x202c: "POP DIRECTIONAL FORMATTING", 0x2066: "LEFT-TO-RIGHT ISOLATE", 0x2067: "RIGHT-TO-LEFT ISOLATE", 0x2068: "FIRST STRONG ISOLATE",
    0x2069: "POP DIRECTIONAL ISOLATE", 0x200e: "LEFT-TO-RIGHT MARK", 0x200f: "RIGHT-TO-LEFT MARK", 0x200b: "ZERO WIDTH SPACE",
    0x200c: "ZERO WIDTH NON-JOINER", 0x200d: "ZERO WIDTH JOINER", 0x2060: "WORD JOINER", 0xfeff: "ZERO WIDTH NO-BREAK SPACE", 0xad: "SOFT HYPHEN",
  };
  return names[c] ?? (c >= 0x80 && c <= 0x9f ? "C1 CONTROL" : "INVISIBLE CHARACTER");
}
const MAX_TEXT = 600;
/** Escapes hidden characters (\u202E) and shortens very long text in the middle, keeping its start and end. */
export function cleanText(t: string): string {
  if (t.length <= MAX_TEXT && !HIDDEN_ALL.test(t)) { HIDDEN_ALL.lastIndex = 0; return t; }
  HIDDEN_ALL.lastIndex = 0;
  const s = t.replace(HIDDEN_ALL, (ch) => escapeChar(ch.charCodeAt(0)));
  if (s.length <= MAX_TEXT) return s;
  const cut = (from: number, to: number) => {
    if (from > 0 && /[\uDC00-\uDFFF]/.test(s[from])) from++;
    if (to < s.length && /[\uD800-\uDBFF]/.test(s[to - 1])) to--;
    return s.slice(from, to);
  };
  const head = cut(0, Math.floor(MAX_TEXT * 2 / 3));
  const tail = cut(s.length - Math.floor(MAX_TEXT / 4), s.length);
  return `${head}…(${(s.length - head.length - tail.length).toLocaleString("en")} characters omitted)…${tail}`;
}

// ------------------------------------------------------------------ schema

class SchemaChecker {
  constructor(private model: SchemaModel, private issues: Issue[]) {}

  private err(el: XElement, message: string, a?: XAttr) {
    if (this.issues.length >= MAX_ISSUES) throw new IssueLimitReached();
    this.issues.push({ severity: "error", code: "SCHEMA", message, path: pathOf(el, a?.local), line: a?.line ?? el.line, col: a?.col ?? el.col });
  }

  /**
   * Attributes outside the no-namespace set, checked as .NET's validator does: namespace declarations, xml:* and
   * xsi:schemaLocation are fine (the locations are never fetched); xsi:nil isn't (no ISO element is nillable); xsi:type
   * only when it names the element's own type; anything in another namespace isn't declared, so it's an error.
   */
  private foreignAttributes(el: XElement, typeName: string) {
    for (const a of el.attrs) {
      if (a.ns === "" || a.ns === XMLNS || a.ns === XML_NS) continue;
      if (a.ns === XSI) {
        if (a.local === "schemaLocation" || a.local === "noNamespaceSchemaLocation") continue;
        if (a.local === "nil") { this.err(el, `xsi:nil is not allowed: <${el.local}> can't be nil in the schema.`, a); continue; }
        if (a.local === "type") {
          const v = xmlTrim(a.value);
          const idx = v.indexOf(":");
          const prefix = idx < 0 ? "" : v.slice(0, idx), local = idx < 0 ? v : v.slice(idx + 1);
          const ns = prefix in el.scope ? el.scope[prefix] : undefined;
          if (ns !== this.model.namespace || local !== typeName) this.err(el, `The xsi:type "${v}" is not valid for <${el.local}>, whose type is ${typeName}.`, a);
          continue;
        }
      }
      this.err(el, `The attribute ${a.ns}:${a.local} is not declared.`, a);
    }
  }

  element(el: XElement, typeName: string) {
    if (el.ns !== this.model.namespace) { this.err(el, `<${el.local}> is in namespace "${el.ns}", not the message's namespace.`); return; }
    this.foreignAttributes(el, typeName);
    if (typeName.startsWith("xs:")) { this.simpleValue(el, el.text, { kind: "simple", base: typeName.slice(3) }); this.noChildren(el); return; }
    const t = Object.hasOwn(this.model.types, typeName) ? this.model.types[typeName] : undefined;
    if (!t) { this.err(el, `Unknown type ${typeName}.`); return; }
    if (t.kind === "simple") {
      this.noAttributes(el);
      this.noChildren(el);
      this.simpleValue(el, el.text, t);
    } else if (t.kind === "simpleContent") {
      this.noChildren(el);
      for (const a of t.attributes) {
        const v = attr(el, a.name);
        if (!v) { if (a.required) this.err(el, `<${el.local}> needs the attribute ${a.name}.`); continue; }
        const at = this.model.types[a.type];
        if (at?.kind === "simple") this.simpleValue(el, v.value, at, v);
      }
      for (const a of el.attrs) {
        if (a.ns === "" && !t.attributes.some((d) => d.name === a.local)) this.err(el, `The attribute ${a.local} is not allowed on <${el.local}>.`, a);
      }
      const vt = this.model.types[t.value];
      if (vt?.kind === "simple") this.simpleValue(el, el.text, vt);
    } else {
      this.noAttributes(el);
      if (!isXmlSpace(el.text)) { this.err(el, `<${el.local}> may only contain elements, not text.`); return; }
      this.content(el, t);
    }
  }

  private noAttributes(el: XElement) {
    for (const a of el.attrs) if (a.ns === "") this.err(el, `The attribute ${a.local} is not allowed on <${el.local}>.`, a);
  }
  private noChildren(el: XElement) {
    if (el.children.length) this.err(el.children[0], `<${el.parent?.local ?? el.local}> holds a value; it can't contain <${el.children[0].local}>.`);
  }

  private content(el: XElement, t: Extract<TypeDef, { kind: "complex" }>) {
    const kids = el.children;
    const list = (defs: ChildDef[]) => defs.map((d) => d.name ?? "any element").join(", ");
    if (t.content === "empty") { if (kids.length) this.err(kids[0], `<${el.local}> must be empty.`); return; }
    if (t.content === "choice") {
      if (!kids.length) { this.err(el, `<${el.local}> must contain one of: ${list(t.children)}.`); return; }
      const def = t.children.find((d) => d.any || d.name === kids[0].local);
      if (!def) { this.err(kids[0], `<${kids[0].local}> is not allowed in <${el.local}>. Expected one of: ${list(t.children)}.`); return; }
      let n = 0;
      while (n < kids.length && (def.any || kids[n].local === def.name) && (def.max < 0 || n < def.max)) { if (!def.any) this.element(kids[n], def.type!); n++; }
      if (n < def.min) { this.err(el, `<${el.local}> needs at least ${def.min} <${def.name}>.`); return; }
      if (n < kids.length) this.err(kids[n], `<${kids[n].local}> is not allowed here: <${el.local}> holds only one choice (${def.name ?? "any"} was already given).`);
      return;
    }
    // sequence
    let k = 0;
    for (let d = 0; d < t.children.length; d++) {
      const def = t.children[d];
      let n = 0;
      while (k < kids.length && (def.any ? !t.children.slice(d + 1).some((x) => x.name === kids[k].local) : kids[k].local === def.name) && (def.max < 0 || n < def.max)) {
        if (!def.any) this.element(kids[k], def.type!);
        k++; n++;
      }
      if (n < def.min) {
        const next = kids[k];
        const expected = t.children.slice(d, d + 6);
        if (next) this.err(next, `<${next.local}> is not allowed here in <${el.local}>. Expected <${def.name}>${expected.length > 1 ? ` (possible elements: ${list(expected)})` : ""}.`);
        else this.err(el, `<${el.local}> is missing the required element <${def.name}>.`);
        return;
      }
    }
    if (k < kids.length) {
      const extra = kids[k];
      const known = t.children.some((d) => d.name === extra.local);
      this.err(extra, known
        ? `<${extra.local}> is in the wrong place or repeated too often in <${el.local}>. The order is: ${list(t.children)}.`
        : `<${extra.local}> is not allowed in <${el.local}>.`);
    }
  }

  private simpleValue(el: XElement, raw: string, t: Extract<TypeDef, { kind: "simple" }>, a?: XAttr) {
    const where = a ? `The ${a.local} attribute of <${el.local}>` : `<${el.local}>`;
    // XSD whitespace: strings keep theirs; other types collapse XML whitespace (only space, tab, CR, LF)
    const v = t.base === "string" ? raw : raw.replace(/[\t\n\r]/g, " ").replace(/ {2,}/g, " ").replace(/^ | $/g, "");
    const bad = (why: string) => this.err(el, `${where} ${why}`, a);
    switch (t.base) {
      case "decimal": if (!/^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(v)) return bad(`must be a number, got "${v}".`); break;
      case "boolean": if (!/^(true|false|1|0)$/.test(v)) return bad(`must be true or false, got "${v}".`); break;
      case "date": if (!isDate(v)) return bad(`must be a real date (YYYY-MM-DD), got "${v}".`); break;
      case "dateTime": if (!isDateTime(v)) return bad(`must be a date and time (YYYY-MM-DDThh:mm:ss, optionally with fractions and an offset), got "${v}".`); break;
      case "time": if (!/^([01]\d|2[0-3]):[0-5]\d:[0-5]\d(\.\d+)?(Z|[+-]\d{2}:\d{2})?$/.test(v)) return bad(`must be a time (hh:mm:ss), got "${v}".`); break;
      case "gYear": if (!/^-?\d{4,}(Z|[+-]\d{2}:\d{2})?$/.test(v)) return bad(`must be a year, got "${v}".`); break;
      case "gYearMonth": if (!/^-?\d{4,}-(0[1-9]|1[0-2])(Z|[+-]\d{2}:\d{2})?$/.test(v)) return bad(`must be a year and month (YYYY-MM), got "${v}".`); break;
      case "base64Binary": if (!/^[A-Za-z0-9+/\s]*={0,2}\s*$/.test(v)) return bad(`must be Base64, got "${v.slice(0, 20)}…".`); break;
    }
    const len = [...v].length;
    if (t.length !== undefined && len !== t.length) return bad(`must be exactly ${t.length} characters, got ${len}.`);
    if (t.minLength !== undefined && len < t.minLength) return bad(len === 0 ? "must not be empty." : `must be at least ${t.minLength} characters, got ${len}.`);
    if (t.maxLength !== undefined && len > t.maxLength) return bad(`must be at most ${t.maxLength} characters, got ${len}.`);
    if (t.enum && !t.enum.includes(v)) return bad(`must be one of ${t.enum.slice(0, 12).join(", ")}${t.enum.length > 12 ? "…" : ""}, got "${v}".`);
    if (t.patterns && !t.patterns.some((p) => xsdPattern(p).test(v))) return bad(`doesn't have the required format (${t.patterns[0]}), got "${v}".`);
    if (t.base === "decimal") {
      const { digits, fraction } = decimalDigits(v);
      if (t.totalDigits !== undefined && digits > t.totalDigits) return bad(`may have at most ${t.totalDigits} digits, got ${digits}.`);
      if (t.fractionDigits !== undefined && fraction > t.fractionDigits) return bad(`may have at most ${t.fractionDigits} decimals, got ${fraction}.`);
      if (t.minInclusive !== undefined && compareDecimal(v, t.minInclusive) < 0) return bad(`must be at least ${t.minInclusive}, got ${v}.`);
      if (t.maxInclusive !== undefined && compareDecimal(v, t.maxInclusive) > 0) return bad(`must be at most ${t.maxInclusive}, got ${v}.`);
      if (t.minExclusive !== undefined && compareDecimal(v, t.minExclusive) <= 0) return bad(`must be more than ${t.minExclusive}, got ${v}.`);
    }
  }
}

const patternCache = new Map<string, RegExp>();
/** XSD patterns match the whole value and use a dialect close to JavaScript's for the patterns ISO uses. */
function xsdPattern(p: string): RegExp {
  let r = patternCache.get(p);
  if (!r) { r = new RegExp(`^(?:${p})$`, "u"); patternCache.set(p, r); }
  return r;
}

function isDate(v: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})(Z|[+-]\d{2}:\d{2})?$/.exec(v);
  if (!m) return false;
  const y = +m[1], mo = +m[2], d = +m[3];
  if (mo < 1 || mo > 12 || d < 1) return false;
  const days = [31, y % 4 === 0 && (y % 100 !== 0 || y % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][mo - 1];
  return d <= days;
}
function isDateTime(v: string): boolean {
  const m = /^(\d{4}-\d{2}-\d{2})T([01]\d|2[0-3]):([0-5]\d):([0-5]\d)(\.\d+)?(Z|[+-](\d{2}):(\d{2}))?$/.exec(v);
  return !!m && isDate(m[1]);
}
function decimalDigits(v: string) {
  let s = v.replace(/^[+-]/, "");
  let [int, frac = ""] = s.split(".");
  int = int.replace(/^0+/, "");
  frac = frac.replace(/0+$/, "");
  s = int + frac;
  return { digits: Math.max(1, s.replace(/^0+/, "").length || 1), fraction: frac.length };
}
/** Exact comparison of two decimal strings. */
export function compareDecimal(a: string, b: string): number {
  const scale = (x: string) => (x.split(".")[1] ?? "").length;
  const n = Math.max(scale(a), scale(b));
  const big = (x: string) => {
    const neg = x.trim().startsWith("-");
    const [i, f = ""] = x.trim().replace(/^[+-]/, "").split(".");
    const v = BigInt((i || "0") + f.padEnd(n, "0"));
    return neg ? -v : v;
  };
  const d = big(a) - big(b);
  return d < 0n ? -1 : d > 0n ? 1 : 0;
}

// ------------------------------------------------------------------ business rules (same as the .NET library)

const ZERO = "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX UYI VND VUV XAF XOF XPF".split(" ");
const THREE = "BHD IQD JOD KWD LYD OMR TND".split(" ");
const FOUR = "CLF UYW".split(" ");
const TWO = ("AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BMD BND BOB BOV BRL BSD BTN BWP BYN BZD CAD CDF CHE CHF CHW CNY COP COU CRC CUP CVE CZK DKK DOP DZD EGP " +
  "ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GTQ GYD HKD HNL HTG HUF IDR ILS INR IRR JMD KES KGS KHR KPW KYD KZT LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU " +
  "MUR MVR MWK MXN MXV MYR MZN NAD NGN NIO NOK NPR NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS " +
  "TMT TOP TRY TTD TWD TZS UAH USD USN UYU UZS VED VES WST XCD XCG YER ZAR ZMW ZWG").split(" ");
export const CURRENCY_DECIMALS: Record<string, number> = Object.fromEntries([
  ...TWO.map((c) => [c, 2]), ...ZERO.map((c) => [c, 0]), ...THREE.map((c) => [c, 3]), ...FOUR.map((c) => [c, 4]),
]);

export function ibanValid(iban: string): boolean {
  const s = iban.replace(/ /g, "").toUpperCase();
  if (s.length < 5 || s.length > 34) return false;
  let rem = 0;
  for (const ch of s.slice(4) + s.slice(0, 4)) {
    const code = ch.charCodeAt(0);
    let v: number;
    if (code >= 48 && code <= 57) v = code - 48;
    else if (code >= 65 && code <= 90) v = code - 55;
    else return false;
    rem = v >= 10 ? (rem * 100 + v) % 97 : (rem * 10 + v) % 97;
  }
  return rem === 1;
}

const fmt = (n: bigint, scale: number) => {
  const neg = n < 0n;
  const s = (neg ? -n : n).toString().padStart(scale + 1, "0");
  const out = scale ? `${s.slice(0, -scale)}.${s.slice(-scale)}` : s;
  // at least two decimals, like the .NET messages
  const [i, f = ""] = out.split(".");
  const trimmed = f.replace(/0+$/, "");
  return (neg ? "-" : "") + i + "." + trimmed.padEnd(2, "0");
};
/** Sums decimal strings exactly. */
function sum(values: (string | null)[]) {
  const scale = Math.max(2, ...values.map((v) => (v?.split(".")[1] ?? "").length));
  const total = values.reduce((acc, v) => acc + toScaled(v ?? "0", scale), 0n);
  return { total, scale, text: fmt(total, scale) };
}
function toScaled(v: string, scale: number) {
  const neg = v.trim().startsWith("-");
  const [i, f = ""] = v.trim().replace(/^[+-]/, "").split(".");
  const x = BigInt((i || "0") + f.padEnd(scale, "0").slice(0, scale));
  return neg ? -x : x;
}
/** True when the decimal string a equals b (an integer scaled by 10^scale), with no rounding. */
const eq = (a: string, b: bigint, scale: number) => {
  const s = Math.max(scale, (a.trim().split(".")[1] ?? "").length);
  return toScaled(a, s) === b * 10n ** BigInt(s - scale);
};

function runRules(id: string, doc: XElement, issues: Issue[], disabled: string[]) {
  const push = (severity: Severity, code: string, message: string, el: XElement, attrName?: string) => {
    if (disabled.includes(code)) return;
    if (issues.length >= MAX_ISSUES) throw new IssueLimitReached();
    const a = attrName ? el.attrs.find((x) => x.local === attrName && x.ns === "") : undefined;
    issues.push({ severity, code, message, path: pathOf(el, attrName), line: a?.line ?? el.line, col: a?.col ?? el.col });
  };
  const all: XElement[] = [];
  (function walk(e: XElement) { all.push(e); e.children.forEach(walk); })(doc);

  // AMOUNT-DECIMALS, CURRENCY-UNKNOWN
  for (const e of all) {
    const ccy = attr(e, "Ccy");
    if (!ccy || e.children.length) continue;
    const code = ccy.value.trim(), value = e.text.trim();
    const allowed = Object.hasOwn(CURRENCY_DECIMALS, code) ? CURRENCY_DECIMALS[code] : undefined;
    if (allowed === undefined) {
      push("warning", "CURRENCY-UNKNOWN", `"${code}" is not an active ISO 4217 currency with minor units, so its decimals can't be checked.`, e, "Ccy");
      continue;
    }
    const dot = value.indexOf(".");
    const decimals = dot < 0 ? 0 : value.length - dot - 1;
    if (decimals > allowed) push("error", "AMOUNT-DECIMALS", `${value} ${code} has ${decimals} decimal${decimals === 1 ? "" : "s"}, but ${code} allows ${allowed === 0 ? "none" : allowed}.`, e);
  }
  // IBAN-CHECKSUM
  for (const e of all.filter((x) => x.local === "IBAN")) {
    const iban = e.text.trim();
    if (!ibanValid(iban)) push("error", "IBAN-CHECKSUM", `${iban} is not a valid IBAN: its check digits (${iban.length >= 4 ? iban.slice(2, 4) : "missing"}) don't match the rest of the number.`, e);
  }

  // HIDDEN-CHARACTERS (warning): "Acme\u202E0001" displays as "Acme1000"
  if (!disabled.includes("HIDDEN-CHARACTERS")) {
    const inspect = (value: string, el: XElement, what: string, attrName?: string) => {
      const m = HIDDEN.exec(value);
      if (!m) return;
      const c = m[0].charCodeAt(0);
      push("warning", "HIDDEN-CHARACTERS", `${what} contains an invisible ${escapeChar(c)} (${describeChar(c)}) character, which can make it display differently from what it is.`, el, attrName);
    };
    for (const e of all) {
      if (!e.children.length) inspect(e.text, e, `<${e.local}>`);
      for (const a of e.attrs) if (a.ns !== XMLNS) inspect(a.value, e, `The ${a.local} attribute of <${e.local}>`, a.ns === "" ? a.local : undefined);
    }
  }

  if (id.startsWith("pacs.008")) {
    const body = child(doc, "FIToFICstmrCdtTrf");
    const txs = children(body, "CdtTrfTxInf");
    const nb = child(body, "GrpHdr", "NbOfTxs");
    if (nb && Number(nb.text.trim()) !== txs.length)
      push("error", "PACS008-NBOFTXS", `NbOfTxs says ${nb.text.trim()}, but the message contains ${txs.length} transaction${txs.length === 1 ? "" : "s"} (CdtTrfTxInf).`, nb);
    const amounts = txs.map((t) => child(t, "IntrBkSttlmAmt")).filter((x): x is XElement => !!x);
    const s = sum(amounts.map((a) => a.text.trim()));
    const ctrl = child(body, "GrpHdr", "CtrlSum");
    if (ctrl && !eq(ctrl.text, s.total, s.scale))
      push("error", "PACS008-CTRLSUM", `CtrlSum is ${ctrl.text.trim()}, but the transaction amounts (IntrBkSttlmAmt) add up to ${s.text}.`, ctrl);
    const total = child(body, "GrpHdr", "TtlIntrBkSttlmAmt");
    if (total) {
      const ccy = attr(total, "Ccy")?.value.trim();
      for (const a of amounts.filter((a) => attr(a, "Ccy")?.value.trim() !== ccy))
        push("error", "PACS008-TOTAL", `TtlIntrBkSttlmAmt is in ${ccy}, but this transaction settles in ${attr(a, "Ccy")?.value.trim()}. A total can only add up amounts in one currency.`, a);
      if (!eq(total.text, s.total, s.scale))
        push("error", "PACS008-TOTAL", `TtlIntrBkSttlmAmt is ${total.text.trim()} ${ccy}, but the transaction amounts add up to ${s.text}.`, total);
    }
  }

  if (id.startsWith("camt.053")) {
    const sign = (holder: XElement | null) => {
      const amt = text(holder, "Amt");
      if (amt === null) return null;
      return text(holder, "CdtDbtInd") === "DBIT" ? "-" + amt : amt;
    };
    for (const stmt of children(child(doc, "BkToCstmrStmt"), "Stmt")) {
      const bals = children(stmt, "Bal");
      const code = (b: XElement) => text(b, "Tp", "CdOrPrtry", "Cd");
      const opening = bals.find((b) => code(b) === "OPBD" || code(b) === "PRCD") ?? null;
      const closing = bals.find((b) => code(b) === "CLBD") ?? null;
      const entries = children(stmt, "Ntry");
      const booked = entries.filter((n) => (text(n, "Sts", "Cd") ?? text(n, "Sts", "Prtry")) === "BOOK");
      if (opening && closing) {
        const ccy = attr(child(opening, "Amt"), "Ccy")?.value;
        const sameCcy = attr(child(closing, "Amt"), "Ccy")?.value === ccy && booked.every((n) => attr(child(n, "Amt"), "Ccy")?.value === ccy);
        const open = sign(opening), close = sign(closing);
        if (sameCcy && open !== null && close !== null) {
          const s = sum([open, ...booked.map(sign)]);
          if (!eq(close, s.total, s.scale)) {
            const credits = sum(booked.filter((n) => text(n, "CdtDbtInd") === "CRDT").map((n) => text(n, "Amt")));
            const debits = sum(booked.filter((n) => text(n, "CdtDbtInd") === "DBIT").map((n) => text(n, "Amt")));
            const diff = toScaled(close, s.scale) - s.total;
            push("error", "CAMT053-BALANCE", `The statement doesn't add up: opening ${fmt(toScaled(open, s.scale), s.scale)} + booked credits ${credits.text} − booked debits ${debits.text} = ${s.text} ${ccy}, but the closing booked balance is ${fmt(toScaled(close, s.scale), s.scale)} ${ccy} (difference ${fmt(diff, s.scale)}).`, closing);
          }
        }
      }
      const summary = child(stmt, "TxsSummry");
      if (summary) {
        const credits = entries.filter((n) => text(n, "CdtDbtInd") === "CRDT");
        const debits = entries.filter((n) => text(n, "CdtDbtInd") === "DBIT");
        const group = (g: XElement | null, label: string, list: XElement[]) => {
          if (!g) return;
          const nb = child(g, "NbOfNtries");
          if (nb && Number(nb.text.trim()) !== list.length) push("error", "CAMT053-SUMMARY", `${label}: NbOfNtries says ${nb.text.trim()}, but the statement has ${list.length}.`, nb);
          const sm = child(g, "Sum");
          const s = sum(list.map((n) => text(n, "Amt")));
          if (sm && !eq(sm.text, s.total, s.scale)) push("error", "CAMT053-SUMMARY", `${label}: Sum says ${sm.text.trim()}, but the entries add up to ${s.text}.`, sm);
        };
        group(child(summary, "TtlNtries"), "TtlNtries (all entries)", entries);
        group(child(summary, "TtlCdtNtries"), "TtlCdtNtries (credit entries)", credits);
        group(child(summary, "TtlDbtNtries"), "TtlDbtNtries (debit entries)", debits);
        const net = child(summary, "TtlNtries", "TtlNetNtry");
        const declared = sign(net);
        if (net && declared !== null) {
          const s = sum([...credits.map((n) => text(n, "Amt")), ...debits.map((n) => "-" + (text(n, "Amt") ?? "0"))]);
          if (!eq(declared, s.total, s.scale)) {
            const side = (v: bigint) => `${v < 0n ? "a debit" : "a credit"} of ${fmt(v < 0n ? -v : v, s.scale)}`;
            push("error", "CAMT053-SUMMARY", `TtlNetNtry is ${side(toScaled(declared, s.scale))}, but the entries net to ${side(s.total)} (credits − debits = ${s.text}).`, net);
          }
        }
      }
    }
  }
}
