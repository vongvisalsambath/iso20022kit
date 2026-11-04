// A small, strict XML reader that keeps line and column numbers (the browser's DOMParser doesn't).
// It refuses DTDs and unknown entities, so hostile input (XXE, entity bombs) is never expanded.

export interface XAttr { name: string; local: string; ns: string; value: string; line: number; col: number }
export interface XElement {
  local: string;
  ns: string;
  attrs: XAttr[];
  children: XElement[];
  text: string;          // concatenated character data directly inside this element
  parent: XElement | null;
  line: number;
  col: number;
}

export class XmlError extends Error {
  constructor(message: string, public line: number, public col: number, public code: "XML-MALFORMED" | "XML-DTD" = "XML-MALFORMED") {
    super(message);
  }
}

const NAME_START = /[A-Za-z_:À-ÖØ-öø-˿Ͱ-ͽͿ-῿‌-‍⁰-↏Ⰰ-⿯、-퟿豈-﷏ﷰ-�]/;
const NAME_CHAR = /[A-Za-z_:\-.0-9·À-ÖØ-öø-ͽͿ-῿‌-‍‿-⁀⁰-↏Ⰰ-⿯、-퟿豈-﷏ﷰ-�]/;
const NAME = new RegExp(`${NAME_START.source}${NAME_CHAR.source}*`, "uy");
const PREDEFINED: Record<string, string> = { lt: "<", gt: ">", amp: "&", apos: "'", quot: '"' };
const XMLNS = "http://www.w3.org/2000/xmlns/";
const XML_NS = "http://www.w3.org/XML/1998/namespace";

export function parseXml(src: string, maxChars = 50_000_000): XElement {
  if (src.length > maxChars) throw new XmlError(`The document is larger than ${maxChars} characters.`, 1, 1);
  let i = 0, line = 1, col = 1;
  const peek = (s: string) => src.startsWith(s, i);
  // Moves forward n characters, keeping line and column right; newlines are found with indexOf, not one by one.
  const advance = (n: number) => {
    const end = i + n;
    let last = -1;
    for (let nl = src.indexOf("\n", i); nl >= 0 && nl < end; nl = src.indexOf("\n", nl + 1)) { line++; last = nl; }
    col = last >= 0 ? end - last : col + n;
    i = end;
  };
  const fail = (msg: string, l = line, c = col): never => { throw new XmlError(msg, l, c); };
  const skipSpace = () => {
    let k = i;
    for (let ch = src.charCodeAt(k); ch === 32 || ch === 9 || ch === 13 || ch === 10; ch = src.charCodeAt(++k));
    if (k > i) advance(k - i);
  };
  const readName = () => {
    NAME.lastIndex = i;
    const m = NAME.exec(src);
    if (!m) return fail(`Expected a name, found "${src[i] ?? "end of input"}".`);
    advance(m[0].length); // names never contain newlines, but advance keeps the column right for any width
    return m[0];
  };
  const decode = (raw: string, l: number, c: number) => !raw.includes("&") ? raw :
    raw.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|[A-Za-z_][\w.-]*);|&/g, (_m, ref: string | undefined) => {
      if (!ref) return fail("A bare & must be written as &amp;.", l, c);
      if (ref.startsWith("#x")) return String.fromCodePoint(parseInt(ref.slice(2), 16));
      if (ref.startsWith("#")) return String.fromCodePoint(parseInt(ref.slice(1), 10));
      if (ref in PREDEFINED) return PREDEFINED[ref];
      return fail(`The entity &${ref}; is not defined (and DTD entities are not allowed).`, l, c);
    });

  // prolog
  if (src.charCodeAt(0) === 0xfeff) advance(1);
  let root: XElement | null = null;
  const stack: XElement[] = [];
  const nsStack: Record<string, string>[] = [{ xml: XML_NS, xmlns: XMLNS }];

  while (i < src.length) {
    if (peek("<?")) {
      const end = src.indexOf("?>", i);
      if (end < 0) fail("Unclosed processing instruction.");
      advance(end + 2 - i);
    } else if (peek("<!--")) {
      const end = src.indexOf("-->", i);
      if (end < 0) fail("Unclosed comment.");
      advance(end + 3 - i);
    } else if (peek("<!DOCTYPE") || peek("<!ENTITY")) {
      throw new XmlError("DTDs are not allowed in ISO 20022 messages (and are a security risk), so the message was not read.", line, col, "XML-DTD");
    } else if (peek("<![CDATA[")) {
      if (!stack.length) fail("Character data outside the root element.");
      const end = src.indexOf("]]>", i);
      if (end < 0) fail("Unclosed CDATA section.");
      stack[stack.length - 1].text += src.slice(i + 9, end);
      advance(end + 3 - i);
    } else if (peek("</")) {
      const l = line, c = col;
      advance(2);
      const name = readName();
      skipSpace();
      if (src[i] !== ">") fail(`Expected ">" to close </${name}.`);
      advance(1);
      const open = stack.pop();
      if (!open) fail(`Unexpected closing tag </${name}>.`, l, c);
      const openName = open!.local;
      const qname = name.includes(":") ? name.split(":")[1] : name;
      if (qname !== openName) fail(`Closing tag </${name}> doesn't match <${openName}> opened on line ${open!.line}.`, l, c);
      nsStack.pop();
    } else if (src[i] === "<") {
      const l = line, c = col;
      advance(1);
      const name = readName();
      const rawAttrs: { name: string; value: string; line: number; col: number }[] = [];
      for (;;) {
        const before = i;
        skipSpace();
        if (peek("/>") || src[i] === ">") break;
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
        if (rawAttrs.some((a) => a.name === an)) fail(`Attribute ${an} appears twice.`, al, ac);
        rawAttrs.push({ name: an, value: decode(raw, al, ac), line: al, col: ac });
        advance(end + 1 - i);
      }
      const selfClosing = peek("/>");
      advance(selfClosing ? 2 : 1);
      if (!stack.length && root) fail("Only one root element is allowed.", l, c);
      // namespaces: an element without declarations shares its parent's scope instead of copying it
      let scope = nsStack[nsStack.length - 1];
      for (const a of rawAttrs) {
        if (a.name !== "xmlns" && !a.name.startsWith("xmlns:")) continue;
        if (scope === nsStack[nsStack.length - 1]) scope = { ...scope };
        scope[a.name === "xmlns" ? "" : a.name.slice(6)] = a.value;
      }
      const resolve = (q: string, isAttr: boolean, al: number, ac: number) => {
        const idx = q.indexOf(":");
        if (idx < 0) return { local: q, ns: isAttr ? "" : scope[""] ?? "" };
        const prefix = q.slice(0, idx);
        const ns = scope[prefix];
        if (ns === undefined) fail(`The prefix "${prefix}" is not declared.`, al, ac);
        return { local: q.slice(idx + 1), ns: ns! };
      };
      const { local, ns } = resolve(name, false, l, c);
      const el: XElement = {
        local, ns, children: [], text: "", parent: stack[stack.length - 1] ?? null, line: l, col: c,
        attrs: !rawAttrs.length ? [] : rawAttrs.map((a) => {
          if (a.name === "xmlns" || a.name.startsWith("xmlns:")) return { name: a.name, local: a.name, ns: XMLNS, value: a.value, line: a.line, col: a.col };
          const r = resolve(a.name, true, a.line, a.col);
          return { name: a.name, local: r.local, ns: r.ns, value: a.value, line: a.line, col: a.col };
        }),
      };
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
        if (raw.trim()) fail(root ? "Text after the root element." : "Text before the root element: this doesn't look like XML.", l, c);
        continue;
      }
      if (raw.includes("]]>")) fail('"]]>" is not allowed in text.', l, c);
      stack[stack.length - 1].text += decode(raw, l, c);
    }
  }
  if (stack.length) fail(`<${stack[stack.length - 1].local}> opened on line ${stack[stack.length - 1].line} is never closed.`);
  if (!root) fail("The input is empty or has no root element.", 1, 1);
  return root!;
}

/** XPath-like path from the ISO Document element: /Document/A/B[2]/C, with an index only where a name repeats. */
export function pathOf(el: XElement, attr?: string): string {
  const parts: string[] = [];
  for (let cur: XElement | null = el; cur; cur = cur.parent) {
    let name = cur.local;
    if (cur.parent) {
      const same = cur.parent.children.filter((c) => c.local === cur!.local && c.ns === cur!.ns);
      if (same.length > 1) name += `[${same.indexOf(cur) + 1}]`;
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
  const t = e?.text.trim();
  return t ? t : null;
};
export const attr = (el: XElement | null | undefined, name: string) => el?.attrs.find((a) => a.local === name && a.ns === "") ?? null;
