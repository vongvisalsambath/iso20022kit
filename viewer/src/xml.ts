// A small, strict XML 1.0 reader that keeps line and column numbers (the browser's DOMParser doesn't).
// It follows the rules .NET's XmlReader enforces, so the viewer and the .NET library accept and reject the same
// inputs: only characters XML allows, no DTDs or unknown entities (so no XXE or entity bombs), namespaces checked,
// end tags matching their start tags exactly, and at most MAX_DEPTH levels of nesting.

export interface XAttr { name: string; local: string; ns: string; value: string; line: number; col: number }
export interface XElement {
  local: string;
  ns: string;
  qname: string;          // the name as written, prefix included
  attrs: XAttr[];
  children: XElement[];
  text: string;           // concatenated character data directly inside this element
  parent: XElement | null;
  scope: NsScope;         // prefixes in scope, to resolve QNames in values (xsi:type)
  line: number;
  col: number;
}
export type NsScope = Record<string, string>; // created with a null prototype: "__proto__" is just a prefix

export class XmlError extends Error {
  constructor(message: string, public line: number, public col: number, public code: "XML-MALFORMED" | "XML-DTD" = "XML-MALFORMED") {
    super(message);
  }
}

/** Deepest nesting accepted, as in the .NET library. ISO 20022 messages are about 20 levels deep. */
export const MAX_DEPTH = 256;
export const MAX_CHARS = 50_000_000;

const NAME_START = /[A-Za-z_:\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u02FF\u0370-\u037D\u037F-\u1FFF\u200C-\u200D\u2070-\u218F\u2C00-\u2FEF\u3001-\uD7FF\uF900-\uFDCF\uFDF0-\uFFFD]/;
const NAME_CHAR = /[A-Za-z_:\-.0-9\u00B7\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u037D\u037F-\u1FFF\u200C-\u200D\u203F-\u2040\u2070-\u218F\u2C00-\u2FEF\u3001-\uD7FF\uF900-\uFDCF\uFDF0-\uFFFD]/;
const NAME = new RegExp(`${NAME_START.source}${NAME_CHAR.source}*`, "uy");
const QNAME_OK = /^[^:]+(:[^:]+)?$/;
const PREDEFINED: Record<string, string> = Object.assign(Object.create(null), { lt: "<", gt: ">", amp: "&", apos: "'", quot: '"' });
export const XMLNS = "http://www.w3.org/2000/xmlns/";
export const XML_NS = "http://www.w3.org/XML/1998/namespace";
/** Any character XML 1.0 doesn't allow (after line endings are normalised, so \r is gone). Lone surrogates match too. */
const NOT_XML_CHAR = /[^\t\n\x20-\uD7FF\uE000-\uFFFD\u{10000}-\u{10FFFF}]/u;
const XML_DECL = /^<\?xml[ \t\n]+version[ \t\n]*=[ \t\n]*(["'])([^"']*)\1(?:[ \t\n]+encoding[ \t\n]*=[ \t\n]*(["'])[A-Za-z][A-Za-z0-9._-]*\3)?(?:[ \t\n]+standalone[ \t\n]*=[ \t\n]*(["'])(?:yes|no)\4)?[ \t\n]*\?>/;

const isXmlChar = (c: number) => c === 9 || c === 10 || c === 13 || (c >= 0x20 && c <= 0xd7ff) || (c >= 0xe000 && c <= 0xfffd) || (c >= 0x10000 && c <= 0x10ffff);
/** XML whitespace is only space, tab, CR and LF (JavaScript's trim() also removes no-break spaces and more). */
export const isXmlSpace = (s: string) => /^[ \t\n\r]*$/.test(s);
export const xmlTrim = (s: string) => s.replace(/^[ \t\n\r]+|[ \t\n\r]+$/g, "");
const hex = (c: number) => "U+" + c.toString(16).toUpperCase().padStart(4, "0");

export function parseXml(input: string, maxChars = MAX_CHARS): XElement {
  if (input.length > maxChars) throw new XmlError(`The document is larger than ${maxChars.toLocaleString("en")} characters.`, 1, 1);
  // XML reads CRLF and a lone CR as LF. Doing it first keeps line numbers right and values the same length as in .NET.
  let src = input.charCodeAt(0) === 0xfeff ? input.slice(1) : input;
  if (src.includes("\r")) src = src.replace(/\r\n?/g, "\n");
  let i = 0, line = 1, col = 1;
  const at = (index: number) => {
    let l = 1, last = -1;
    for (let k = src.indexOf("\n"); k >= 0 && k < index; k = src.indexOf("\n", k + 1)) { l++; last = k; }
    return { line: l, col: index - last };
  };
  const fail = (msg: string, l = line, c = col): never => { throw new XmlError(msg, l, c); };

  const badChar = NOT_XML_CHAR.exec(src);
  if (badChar) {
    const p = at(badChar.index);
    fail(`The character ${hex(badChar[0].codePointAt(0)!)} is not allowed in XML.`, p.line, p.col);
  }

  const peek = (s: string) => src.startsWith(s, i);
  // Moves forward n characters, keeping line and column right. The next newline's position is remembered: searching
  // for it on every step scanned to the end of the input each time when there was none left, which made a message
  // written on one line (as many are) take minutes.
  let nextNl = src.indexOf("\n");
  const advance = (n: number) => {
    const end = i + n;
    let last = -1;
    while (nextNl >= 0 && nextNl < end) { line++; last = nextNl; nextNl = src.indexOf("\n", nextNl + 1); }
    col = last >= 0 ? end - last : col + n;
    i = end;
  };
  const skipSpace = () => {
    let k = i;
    for (let ch = src.charCodeAt(k); ch === 32 || ch === 9 || ch === 10; ch = src.charCodeAt(++k));
    if (k > i) advance(k - i);
  };
  const readName = () => {
    NAME.lastIndex = i;
    const m = NAME.exec(src);
    if (!m) return fail(`Expected a name, found "${src[i] ?? "end of input"}".`);
    const l = line, c = col;
    advance(m[0].length);
    if (!QNAME_OK.test(m[0])) fail(`"${m[0].slice(0, 80)}" is not a valid name: a name has at most one ":", between a prefix and a local name.`, l, c);
    return m[0];
  };
  const decode = (raw: string, l: number, c: number) => !raw.includes("&") ? raw :
    raw.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|[A-Za-z_][\w.-]*);|&/g, (_m, ref: string | undefined) => {
      if (!ref) return fail("A bare & must be written as &amp;.", l, c);
      if (ref[0] === "#") {
        const digits = ref[1] === "x" ? ref.slice(2) : ref.slice(1);
        const cp = digits.length > 8 ? -1 : parseInt(digits, ref[1] === "x" ? 16 : 10);
        if (!isXmlChar(cp)) return fail(`The character reference &${ref.slice(0, 20)}; is not a character XML allows.`, l, c);
        return String.fromCodePoint(cp);
      }
      if (Object.hasOwn(PREDEFINED, ref)) return PREDEFINED[ref];
      return fail(`The entity &${ref.slice(0, 40)}; is not defined (and DTD entities are not allowed).`, l, c);
    });

  let root: XElement | null = null;
  const stack: XElement[] = [];
  const top: NsScope = Object.assign(Object.create(null), { xml: XML_NS, xmlns: XMLNS });
  const nsStack: NsScope[] = [top];

  if (src.startsWith("<?xml") && /[ \t\n?]/.test(src[5] ?? "")) {
    const m = XML_DECL.exec(src);
    if (!m) fail("The XML declaration is malformed.");
    if (m![2] !== "1.0") fail(`Version number '${m![2].slice(0, 20)}' is invalid: only XML 1.0 is supported.`, 1, 16);
    advance(m![0].length);
  }

  while (i < src.length) {
    if (peek("<?")) {
      const l = line, c = col;
      advance(2);
      const target = readName();
      if (target.toLowerCase() === "xml") fail("Unexpected XML declaration: it must come first in the document, with nothing before it.", l, c);
      const end = src.indexOf("?>", i);
      if (end < 0) fail("Unclosed processing instruction.", l, c);
      if (end > i && !/[ \t\n]/.test(src[i])) fail("Expected a space after the processing instruction's name.");
      advance(end + 2 - i);
    } else if (peek("<!--")) {
      const l = line, c = col;
      const end = src.indexOf("-->", i + 4);
      if (end < 0) fail("Unclosed comment.", l, c);
      const body = src.slice(i + 4, end);
      if (body.includes("--") || body.endsWith("-")) fail("A comment cannot contain '--', and '-' cannot be its last character.", l, c);
      advance(end + 3 - i);
    } else if (peek("<!DOCTYPE") || peek("<!ENTITY")) {
      throw new XmlError("DTDs are not allowed in ISO 20022 messages (and are a security risk), so the message was not read.", line, col, "XML-DTD");
    } else if (peek("<![CDATA[")) {
      if (!stack.length) fail("Character data outside the root element.");
      const end = src.indexOf("]]>", i);
      if (end < 0) fail("Unclosed CDATA section.");
      stack[stack.length - 1].text += src.slice(i + 9, end);
      advance(end + 3 - i);
    } else if (peek("<!")) {
      fail("Unexpected markup: only elements, comments, CDATA sections and processing instructions are allowed.");
    } else if (peek("</")) {
      const l = line, c = col;
      advance(2);
      const name = readName();
      skipSpace();
      if (src[i] !== ">") fail(`Expected ">" to close </${name}.`);
      advance(1);
      const open = stack.pop();
      if (!open) fail(`Unexpected closing tag </${name}>.`, l, c);
      if (name !== open!.qname) fail(`The <${open!.qname}> start tag on line ${open!.line} doesn't match the end tag </${name}>.`, l, c);
      nsStack.pop();
    } else if (src[i] === "<") {
      const l = line, c = col;
      if (!stack.length && root) fail("Only one root element is allowed.", l, c);
      if (stack.length >= MAX_DEPTH) fail(`Elements are nested more than ${MAX_DEPTH} levels deep; ISO 20022 messages are far shallower, so the input was not read.`, l, c);
      advance(1);
      const name = readName();
      const rawAttrs: { name: string; value: string; line: number; col: number }[] = [];
      const seen = new Set<string>();
      for (;;) {
        const before = i;
        skipSpace();
        if (peek("/>") || src[i] === ">") break;
        if (i >= src.length) fail(`<${name}> is never closed.`, l, c);
        if (i === before) fail(`Expected a space before the next attribute in <${name}>.`);
        const al = line, ac = col;
        const an = readName();
        skipSpace();
        if (src[i] !== "=") fail(`Expected "=" after attribute ${an}.`);
        advance(1);
        skipSpace();
        const q = src[i];
        if (q !== '"' && q !== "'") fail(`Attribute ${an} needs a quoted value.`);
        const end = src.indexOf(q, i + 1);
        if (end < 0) fail(`Unclosed value for attribute ${an}.`);
        const raw = src.slice(i + 1, end);
        if (raw.includes("<")) fail(`"<" is not allowed in attribute values.`, al, ac);
        if (seen.has(an)) fail(`'${an}' is a duplicate attribute name.`, al, ac);
        seen.add(an);
        // attribute-value normalisation: literal tabs and newlines read as spaces (character references don't)
        rawAttrs.push({ name: an, value: decode(raw.replace(/[\t\n]/g, " "), al, ac), line: al, col: ac });
        advance(end + 1 - i);
      }
      const selfClosing = peek("/>");
      advance(selfClosing ? 2 : 1);

      // namespaces: an element without declarations shares its parent's scope instead of copying it
      const parentScope = nsStack[nsStack.length - 1];
      let scope = parentScope;
      for (const a of rawAttrs) {
        const isDefault = a.name === "xmlns";
        if (!isDefault && !a.name.startsWith("xmlns:")) continue;
        const prefix = isDefault ? "" : a.name.slice(6);
        const bad =
          (prefix === "xmlns") ||
          (prefix === "xml" && a.value !== XML_NS) ||
          (prefix !== "xml" && (a.value === XML_NS || a.value === XMLNS)) ||
          (!isDefault && a.value === "");
        if (bad) fail("Invalid namespace declaration.", a.line, a.col);
        if (scope === parentScope) scope = Object.assign(Object.create(null), parentScope) as NsScope;
        scope[prefix] = a.value;
      }
      const resolve = (q: string, isAttr: boolean, ql: number, qc: number) => {
        const idx = q.indexOf(":");
        if (idx < 0) return { local: q, ns: isAttr ? "" : (scope[""] ?? "") };
        const prefix = q.slice(0, idx);
        if (!(prefix in scope)) fail(`The prefix "${prefix.slice(0, 40)}" is not declared.`, ql, qc);
        return { local: q.slice(idx + 1), ns: scope[prefix] };
      };
      const { local, ns } = resolve(name, false, l, c);
      const expanded = new Set<string>();
      const attrs: XAttr[] = !rawAttrs.length ? [] : rawAttrs.map((a) => {
        if (a.name === "xmlns" || a.name.startsWith("xmlns:")) return { name: a.name, local: a.name, ns: XMLNS, value: a.value, line: a.line, col: a.col };
        const r = resolve(a.name, true, a.line, a.col);
        const key = r.ns + "\u0000" + r.local;
        if (expanded.has(key)) fail(`'${a.name}' is a duplicate attribute name (another attribute has the same namespace and name).`, a.line, a.col);
        expanded.add(key);
        return { name: a.name, local: r.local, ns: r.ns, value: a.value, line: a.line, col: a.col };
      });
      const el: XElement = { local, ns, qname: name, attrs, children: [], text: "", parent: stack[stack.length - 1] ?? null, scope, line: l, col: c };
      if (el.parent) el.parent.children.push(el);
      else root = el;
      if (!selfClosing) { stack.push(el); nsStack.push(scope); }
    } else {
      const start = i;
      const l = line, c = col;
      const next = src.indexOf("<", i);
      const end = next < 0 ? src.length : next;
      const raw = src.slice(start, end);
      advance(end - i);
      if (!stack.length) {
        if (!isXmlSpace(raw)) fail(root ? "Text after the root element." : "Text before the root element: this doesn't look like XML.", l, c);
        continue;
      }
      if (raw.includes("]]>")) fail('"]]>" is not allowed in text.', l, c);
      stack[stack.length - 1].text += decode(raw, l, c);
    }
  }
  if (stack.length) fail(`<${stack[stack.length - 1].qname}> opened on line ${stack[stack.length - 1].line} is never closed.`);
  if (!root) fail("The input is empty or has no root element.", 1, 1);
  return root!;
}

// Position of each child among its same-named siblings, worked out once per parent: a statement can hold tens of
// thousands of entries, and scanning them again for every path made reports quadratic.
const siblingIndex = new WeakMap<XElement, Map<XElement, [number, number]>>();
function siblings(el: XElement): [number, number] {
  const parent = el.parent!;
  let map = siblingIndex.get(parent);
  if (!map) {
    map = new Map();
    const groups = new Map<string, XElement[]>();
    for (const c of parent.children) {
      const key = c.ns + "\u0000" + c.local;
      let g = groups.get(key);
      if (!g) groups.set(key, (g = []));
      g.push(c);
    }
    for (const g of groups.values()) g.forEach((c, n) => map!.set(c, [n + 1, g.length]));
    siblingIndex.set(parent, map);
  }
  return map.get(el) ?? [1, 1];
}

/** XPath-like path from the ISO Document element: /Document/A/B[2]/C, with an index only where a name repeats. */
export function pathOf(el: XElement, attr?: string): string {
  const parts: string[] = [];
  for (let cur: XElement | null = el; cur; cur = cur.parent) {
    let name = cur.local;
    if (cur.parent) {
      const [index, count] = siblings(cur);
      if (count > 1) name += `[${index}]`;
    }
    parts.push(name);
    if (cur.local === "Document" && cur.ns.startsWith("urn:iso:std:iso:20022:tech:xsd:")) break;
  }
  return "/" + parts.reverse().join("/") + (attr ? "/@" + attr : "");
}

export const child = (el: XElement | null | undefined, ...names: string[]): XElement | null => {
  let cur: XElement | null | undefined = el;
  for (const n of names) cur = cur?.children.find((c) => c.local === n) ?? null;
  return cur ?? null;
};
export const children = (el: XElement | null | undefined, name: string) => el?.children.filter((c) => c.local === name) ?? [];
export const text = (el: XElement | null | undefined, ...names: string[]): string | null => {
  const e = names.length ? child(el, ...names) : el;
  const t = e ? xmlTrim(e.text) : "";
  return t ? t : null;
};
export const attr = (el: XElement | null | undefined, name: string) => el?.attrs.find((a) => a.local === name && a.ns === "") ?? null;

/**
 * Turns the bytes of a file into text the way an XML reader does: a byte-order mark (UTF-8 or UTF-16) wins, then the
 * encoding in the XML declaration, else UTF-8. Bytes that aren't valid in that encoding are an error, never silently
 * replaced. (Browsers decode "ISO-8859-1" as windows-1252, which differs only for bytes 0x80-0x9F.)
 */
export function decodeXml(bytes: Uint8Array): { text: string; error?: XmlError } {
  let label = "utf-8", offset = 0;
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) offset = 3;
  else if (bytes[0] === 0xff && bytes[1] === 0xfe) { label = "utf-16le"; offset = 2; }
  else if (bytes[0] === 0xfe && bytes[1] === 0xff) { label = "utf-16be"; offset = 2; }
  else {
    const head = String.fromCharCode(...bytes.subarray(0, 200));
    const m = /^<\?xml[^>]*?encoding[ \t\r\n]*=[ \t\r\n]*["']([A-Za-z][A-Za-z0-9._-]*)["']/.exec(head);
    if (m) label = m[1].toLowerCase();
  }
  let decoder: TextDecoder;
  try { decoder = new TextDecoder(label, { fatal: true }); }
  catch { return { text: new TextDecoder().decode(bytes), error: new XmlError(`The file says it is in the "${label.slice(0, 40)}" encoding, which this viewer can't read.`, 1, 1) }; }
  try {
    return { text: decoder.decode(bytes.subarray(offset)) };
  } catch {
    // find where it goes wrong, for the message
    const lossy = new TextDecoder(label).decode(bytes.subarray(offset));
    const bad = lossy.indexOf("\uFFFD");
    const before = lossy.slice(0, Math.max(0, bad));
    const lineNo = before.split("\n").length;
    return { text: lossy, error: new XmlError(`The file isn't valid ${label.toUpperCase()}: some bytes don't form characters in that encoding.`, lineNo, bad - before.lastIndexOf("\n")) };
  }
}
