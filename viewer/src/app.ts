// The viewer page: paste, open or drop a message; it is checked and explained in the browser. Nothing is sent anywhere.
import { validate, RULES, OPTIONAL_RULES, SUPPORTED, type Issue, type Result } from "./validate";
import { explain, money, friendlyDate, friendlyDateTime, printIban, type Section, type Row } from "./explain";
import { label, CODES } from "./names";
import { pathOf, type XElement } from "./xml";
import { SAMPLES } from "./samples";

// ------------------------------------------------------------------ tiny DOM helper (text only, never innerHTML)

type Child = Node | string | null | undefined | false;
function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Record<string, unknown> = {}, ...kids: (Child | Child[])[]): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v as EventListener);
    else if (k === "class") el.className = String(v);
    else if (k === "dataset") Object.assign(el.dataset, v);
    else if (k === "style") Object.assign(el.style, v); // CSSOM, which the page's CSP allows (style attributes are not)
    else el.setAttribute(k, v === true ? "" : String(v));
  }
  for (const c of kids.flat()) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}
const $ = <T extends HTMLElement>(sel: string) => document.querySelector(sel) as T;

// ------------------------------------------------------------------ state

const input = $<HTMLTextAreaElement>("#xml");
const gutter = $<HTMLDivElement>("#gutter");
const gutterWrap = $<HTMLDivElement>(".gutter-wrap");
const marks = $<HTMLDivElement>("#marks");
const source = $<HTMLDivElement>("#source");
const sourceRows = $<HTMLDivElement>("#source-rows");
const sourceSpacer = $<HTMLDivElement>("#source-spacer");
const bigNote = $<HTMLSpanElement>("#big-note");
const output = $<HTMLDivElement>("#output");
const status = $<HTMLDivElement>("#status");
const disabled = new Set<string>();
let tab: "summary" | "issues" | "fields" = "summary";
let last: Result | null = null;
let lineStarts: number[] = [0];
let timer = 0;
let lastMs = 0;
let flash = { line: 0, until: 0 };
/** A message over BIG characters is shown in a read-only view that draws only the visible lines: browsers take
 *  seconds to lay out a textarea holding a 20 MB statement. */
let big: string | null = null;
const BIG = 2_000_000;
const text = () => big ?? input.value;

const LINE_HEIGHT = 20; // must match --line in styles.css
const PAD = 12;         // textarea top padding
const MAX_SCROLL_PX = 15_000_000; // taller than this, the big view scrolls proportionally instead of pixel by pixel

// ------------------------------------------------------------------ editor: line numbers, issue marks, jump to line

function indexLines(src: string) {
  lineStarts = [0];
  for (let i = src.indexOf("\n"); i >= 0; i = src.indexOf("\n", i + 1)) lineStarts.push(i + 1);
}
function issueLines() {
  const bad = new Map<number, Issue["severity"]>();
  for (const i of last?.issues ?? []) if (i.line && bad.get(i.line) !== "error") bad.set(i.line, i.severity);
  return bad;
}
const flashing = (line: number) => flash.line === line && performance.now() < flash.until;

/** Where a line sits in the visible part of the editor. Rows are placed relative to the view, not the whole
 *  document, so their offsets stay small however long the message is. */
const y = (line: number) => PAD + (line - 1) * LINE_HEIGHT - input.scrollTop;

function drawGutter() {
  if (big !== null) return drawBig();
  const n = lineStarts.length;
  const bad = issueLines();
  const first = Math.max(0, Math.floor((input.scrollTop - PAD) / LINE_HEIGHT) - 5);
  const count = Math.ceil(input.clientHeight / LINE_HEIGHT) + 10;
  const frag = document.createDocumentFragment();
  for (let l = first; l < Math.min(n, first + count); l++) {
    const sev = bad.get(l + 1);
    frag.append(h("div", { class: `ln${sev ? " " + sev : ""}`, style: { top: `${y(l + 1)}px` } }, String(l + 1)));
  }
  gutter.replaceChildren(frag);
  gutter.style.width = `${Math.max(3, String(n).length) + 2}ch`;

  const mf = document.createDocumentFragment();
  for (const [line, sev] of bad) {
    if (line - 1 < first || line - 1 > first + count) continue;
    mf.append(h("div", { class: `mark ${sev}`, style: { top: `${y(line)}px` } }));
  }
  if (flashing(flash.line)) mf.append(h("div", { class: "flash", style: { top: `${y(flash.line)}px` } }));
  marks.replaceChildren(mf);
}

// the big, read-only view
function bigGeometry() {
  const total = lineStarts.length;
  const view = source.clientHeight;
  const visible = Math.ceil(view / LINE_HEIGHT) + 1;
  const height = Math.min(total * LINE_HEIGHT + 2 * PAD, MAX_SCROLL_PX);
  const scaled = total * LINE_HEIGHT + 2 * PAD > MAX_SCROLL_PX;
  return { total, view, visible, height, scaled, maxScroll: Math.max(1, height - view) };
}
function drawBig() {
  if (big === null) return;
  const g = bigGeometry();
  const top = source.scrollTop;
  let first: number, offset: number;
  if (g.scaled) { first = Math.round((top / g.maxScroll) * Math.max(0, g.total - g.visible + 1)); offset = 0; }
  else { first = Math.max(0, Math.floor((top - PAD) / LINE_HEIGHT)); offset = PAD + first * LINE_HEIGHT - top; }
  const bad = issueLines();
  const digits = String(g.total).length;
  const frag = document.createDocumentFragment();
  for (let l = first; l < Math.min(g.total, first + g.visible + 1); l++) {
    const sev = bad.get(l + 1);
    const line = big.slice(lineStarts[l], (lineStarts[l + 1] ?? big.length + 1) - 1).replace(/\r$/, "");
    frag.append(h("div", { class: `srow${sev ? " " + sev : ""}${flashing(l + 1) ? " flashing" : ""}` },
      h("span", { class: "sn", style: { width: `${digits + 2}ch` } }, String(l + 1)), h("span", { class: "st" }, line)));
  }
  sourceRows.replaceChildren(frag);
  sourceRows.style.top = `${top + offset}px`;
}
function enterBig(src: string) {
  big = src;
  input.value = "";
  input.hidden = true;
  marks.hidden = true;
  gutterWrap.hidden = true;
  source.hidden = false;
  indexLines(src);
  let longest = 0;
  for (let l = 0; l < lineStarts.length; l++) longest = Math.max(longest, (lineStarts[l + 1] ?? src.length) - lineStarts[l]);
  sourceSpacer.style.height = `${bigGeometry().height}px`;
  sourceSpacer.style.width = `${Math.min(longest, 2000) + String(lineStarts.length).length + 6}ch`;
  source.scrollTop = 0;
  bigNote.hidden = false;
  bigNote.textContent = `${(src.length / 1e6).toFixed(1)} MB, ${lineStarts.length.toLocaleString("en")} lines: shown read-only`;
  drawBig();
}
function leaveBig() {
  if (big === null) return;
  big = null;
  source.hidden = true;
  sourceRows.replaceChildren();
  input.hidden = false;
  marks.hidden = false;
  gutterWrap.hidden = false;
  bigNote.hidden = true;
}

function jumpTo(line?: number, col?: number) {
  if (!line) return;
  flash = { line, until: performance.now() + 1200 };
  setTimeout(drawGutter, 1250);
  if (big !== null) {
    const g = bigGeometry();
    source.scrollTop = g.scaled
      ? ((line - 1) / Math.max(1, g.total - g.visible + 1)) * g.maxScroll - g.view / 3
      : PAD + (line - 1) * LINE_HEIGHT - g.view / 3;
    drawBig();
    if (matchMedia("(max-width: 900px)").matches) source.scrollIntoView({ block: "nearest", behavior: "smooth" });
    return;
  }
  const start = lineStarts[line - 1] ?? 0;
  const end = (lineStarts[line] ?? input.value.length + 1) - 1;
  input.focus({ preventScroll: true });
  input.setSelectionRange(start + Math.max(0, (col ?? 1) - 1), end);
  input.scrollTop = Math.max(0, PAD + (line - 1) * LINE_HEIGHT - input.clientHeight / 3);
  drawGutter();
  if (matchMedia("(max-width: 900px)").matches) input.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

// ------------------------------------------------------------------ run

function run() {
  const src = text();
  if (big === null) indexLines(src);
  if (!src.trim()) { last = null; drawGutter(); renderEmpty(); return; }
  const started = performance.now();
  last = validate(src, [...disabled]);
  lastMs = performance.now() - started;
  drawGutter();
  render(last, lastMs);
}
function schedule() {
  clearTimeout(timer);
  const size = text().length;
  const slow = size > 1_000_000;
  timer = window.setTimeout(() => {
    if (!slow) return run();
    // a large statement takes a second or two: say so before the page is busy
    status.className = "status";
    status.replaceChildren(h("strong", {}, "Checking…"), h("span", {}, `${(size / 1e6).toFixed(1)} MB`));
    requestAnimationFrame(() => setTimeout(run, 0));
  }, slow ? 50 : 150);
}

// ------------------------------------------------------------------ output

function renderEmpty() {
  status.className = "status";
  status.replaceChildren();
  output.replaceChildren(
    h("div", { class: "empty" },
      h("h2", {}, "Read and check an ISO 20022 message"),
      h("p", {}, "Paste a message on the left, open a file, or drop one anywhere on this page. It's checked against the official schema and the business rules, and explained in plain English."),
      h("p", { class: "muted" }, `Supported: ${SUPPORTED.join(", ")}. Everything runs in this page: the message is never uploaded or stored.`),
      h("div", { class: "sample-list" },
        h("span", { class: "muted" }, "Or try a sample:"),
        ...SAMPLES.map((s, i) => h("button", { class: "chip", type: "button", onclick: () => loadSample(i) }, s.title)),
      ),
    ),
  );
}

function render(r: Result, ms: number) {
  const errors = r.issues.filter((i) => i.severity === "error");
  const warnings = r.issues.filter((i) => i.severity === "warning");
  status.className = `status ${r.valid ? (warnings.length ? "warn" : "ok") : "bad"}`;
  status.replaceChildren(
    h("strong", {}, r.valid ? (warnings.length ? "Valid, with warnings" : "Valid") : "Not valid"),
    h("span", {}, [
      r.message ?? "unknown message",
      errors.length ? `${errors.length} error${errors.length === 1 ? "" : "s"}` : "",
      warnings.length ? `${warnings.length} warning${warnings.length === 1 ? "" : "s"}` : "",
      `checked in ${ms < 10 ? ms.toFixed(1) : Math.round(ms)} ms`,
    ].filter(Boolean).join(" · ")),
  );

  const summary = r.valid || r.document ? explain(r.message, r.document) : null;
  if (tab === "summary" && !summary) tab = r.issues.length ? "issues" : "fields";
  if (tab === "fields" && !r.document) tab = "issues";
  const tabs: [typeof tab, string, boolean][] = [
    ["summary", "Summary", !!summary],
    ["issues", `Issues${r.issues.length ? ` (${r.issues.length})` : ""}`, true],
    ["fields", "Fields", !!r.document],
  ];
  const bar = h("div", { class: "tabs", role: "tablist" }, tabs.map(([id, text, enabled]) =>
    h("button", { type: "button", role: "tab", "aria-selected": String(tab === id), disabled: !enabled, onclick: () => { tab = id; render(r, ms); } }, text)));
  const panel = h("div", { class: "panel", role: "tabpanel" });
  if (tab === "summary" && summary) panel.append(renderSummary(summary, r));
  else if (tab === "issues") panel.append(renderIssues(r));
  else if (r.document) panel.append(renderFields(r));
  output.replaceChildren(bar, panel);
}

function jumpButton(el: XElement | null | undefined, text: Child, cls = "jump") {
  if (!el) return h("span", {}, text);
  return h("button", { type: "button", class: cls, title: `Line ${el.line}: ${pathOf(el)}`, onclick: () => jumpTo(el.line, el.col) }, text);
}

function renderSummary(s: NonNullable<ReturnType<typeof explain>>, r: Result) {
  const wrap = h("div", { class: "summary" });
  if (!r.valid) wrap.append(h("p", { class: "note bad" }, "This message has errors (see Issues). The summary shows what it says, but a bank would reject it."));
  wrap.append(h("p", { class: "lead" }, s.lead));
  for (const sec of s.sections) wrap.append(renderSection(sec));
  return wrap;
}

function renderSection(sec: Section) {
  return h("section", { class: "card" },
    h("h3", {}, jumpButton(sec.el, sec.title, "jump title")),
    sec.lead ? h("p", {}, sec.lead) : null,
    h("dl", { class: "facts" }, sec.facts.flatMap((f) => [
      h("dt", {}, f.label, f.help ? h("span", { class: "help", title: f.help, "aria-label": f.help }, "?") : null),
      h("dd", {}, jumpButton(f.el, f.value)),
    ])),
    sec.table ? h("div", { class: "table-wrap" }, h("table", {},
      h("thead", {}, h("tr", {}, sec.table.head.map((c, i) => h("th", { class: i === 2 ? "num" : i === 0 ? "nowrap" : "" }, c)))),
      tbody(sec.table.rows),
      sec.table.foot ? h("tfoot", {}, h("tr", {}, sec.table.foot.map((c) => h("td", {}, c)))) : null,
    )) : null,
  );
}

/** Table rows, 500 at a time: a statement can hold tens of thousands of entries. */
function tbody(rows: Row[]) {
  const body = h("tbody");
  const PAGE = 500;
  const add = (from: number) => {
    body.querySelector(".more")?.remove();
    body.append(...rows.slice(from, from + PAGE).map((row) => h("tr", { class: row.tone ?? "", onclick: () => jumpTo(row.el.line, row.el.col), title: `Line ${row.el.line}` },
      row.cells.map((c, i) => h("td", { class: i === 2 ? "num" : i === 0 ? "nowrap" : "" }, c)))));
    if (from + PAGE < rows.length) {
      body.append(h("tr", { class: "more" }, h("td", { colspan: "6" },
        h("button", { type: "button", class: "small", onclick: (e: Event) => { e.stopPropagation(); add(from + PAGE); } },
          `Show ${Math.min(PAGE, rows.length - from - PAGE)} more of ${rows.length - from - PAGE} remaining`))));
    }
  };
  add(0);
  return body;
}

function renderIssues(r: Result) {
  const wrap = h("div", { class: "issues" });
  if (!r.issues.length) wrap.append(h("p", { class: "note ok" }, "No issues: the message matches the schema and passes every business rule below."));
  const schemaErrors = r.issues.some((i) => i.code === "SCHEMA");
  if (schemaErrors) wrap.append(h("p", { class: "note" }, "Business rules (totals, balances, IBANs…) run once the schema errors are fixed, as they rely on the message's structure."));
  wrap.append(h("ol", { class: "issue-list" }, r.issues.map((i) => h("li", { class: i.severity },
    h("button", { type: "button", class: "issue", onclick: () => jumpTo(i.line, i.col) },
      h("span", { class: "where" }, i.line ? `Line ${i.line}` : ""),
      h("span", { class: "code" }, i.code),
      h("span", { class: "msg" }, i.message),
      i.path ? h("code", { class: "path" }, i.path) : null,
    )))));

  const rules = h("details", { class: "rules" },
    h("summary", {}, `Checks (${Object.keys(RULES).length - disabled.size} of ${Object.keys(RULES).length} on)`),
    h("ul", {}, Object.entries(RULES).map(([code, desc]) => h("li", {},
      h("label", {},
        h("input", { type: "checkbox", checked: !disabled.has(code), disabled: !OPTIONAL_RULES.includes(code),
          onchange: (e: Event) => { (e.target as HTMLInputElement).checked ? disabled.delete(code) : disabled.add(code); run(); } }),
        h("code", {}, code), " ", desc))),
    ));
  if (disabled.size) rules.open = true;
  const copy = h("button", { type: "button", class: "small", onclick: async () => {
    const report = { message: r.message, valid: r.valid, issues: r.issues };
    try { await navigator.clipboard.writeText(JSON.stringify(report, null, 2)); copy.textContent = "Copied"; } catch { copy.textContent = "Couldn't copy"; }
    setTimeout(() => (copy.textContent = "Copy report as JSON"), 1500);
  } }, "Copy report as JSON");
  wrap.append(h("div", { class: "row" }, rules, copy));
  return wrap;
}

// ------------------------------------------------------------------ field tree

function valueText(el: XElement): string {
  const v = el.text.trim();
  if (el.attrs.some((a) => a.local === "Ccy" && a.ns === "")) return money(el) ?? v;
  if (el.local === "IBAN") return printIban(v);
  if (/^\d{4}-\d{2}-\d{2}T/.test(v)) return friendlyDateTime(v) ?? v;
  if (/^\d{4}-\d{2}-\d{2}$/.test(v)) return friendlyDate(v) ?? v;
  return v;
}
function codeMeaning(el: XElement): string | null {
  const v = el.text.trim();
  const table = CODES[el.local] ?? (el.local === "Cd" ? CODES.Cd : null);
  return table?.[v] ?? null;
}

function renderFields(r: Result) {
  const issuesAt = new Map<XElement, Issue[]>();
  const byPath = new Map<string, XElement>();
  (function index(e: XElement) { byPath.set(pathOf(e), e); e.children.forEach(index); })(r.document!);
  for (const i of r.issues) {
    const p = i.path?.replace(/\/@[^/]+$/, "");
    const el = p ? byPath.get(p) : undefined;
    if (el) issuesAt.set(el, [...(issuesAt.get(el) ?? []), i]);
  }
  const hasIssueInside = new Set<XElement>();
  for (const el of issuesAt.keys()) for (let c: XElement | null = el; c; c = c.parent) hasIssueInside.add(c);

  const node = (el: XElement, depth: number): HTMLElement => {
    const l = label(el.local);
    const own = issuesAt.get(el) ?? [];
    const head = [
      h("span", { class: "name" }, l.name),
      l.name.replace(/[^A-Za-z0-9]/g, "") !== el.local ? h("code", { class: "tag" }, el.local) : null,
      l.help ? h("span", { class: "help", title: l.help, "aria-label": l.help }, "?") : null,
    ];
    const issueList = own.length ? h("ul", { class: "node-issues" }, own.map((i) => h("li", { class: i.severity }, i.message))) : null;
    const line = h("button", { type: "button", class: "line-link", title: "Show in source", onclick: (e: Event) => { e.preventDefault(); jumpTo(el.line, el.col); } }, `L${el.line}`);
    if (!el.children.length) {
      const meaning = codeMeaning(el);
      return h("div", { class: `leaf${own.length ? " has-issue" : ""}` },
        h("div", { class: "leaf-row" }, h("span", { class: "label" }, head), h("span", { class: "value" }, valueText(el) || h("em", {}, "empty"), meaning ? h("span", { class: "meaning" }, ` (${meaning})`) : null), line),
        issueList);
    }
    const d = h("details", { class: `branch${own.length ? " has-issue" : ""}${hasIssueInside.has(el) ? " issue-inside" : ""}` },
      h("summary", {}, h("span", { class: "label" }, head), h("span", { class: "count", title: `${el.children.length} element${el.children.length === 1 ? "" : "s"} inside` }, el.children.length === 1 ? "" : `${el.children.length}`), line));
    if (issueList) d.append(issueList);
    const body = h("div", { class: "kids" });
    d.append(body);
    let filled = false;
    const fill = () => { if (!filled) { filled = true; body.append(...el.children.map((c) => node(c, depth + 1))); } };
    if (depth < 3 || hasIssueInside.has(el)) { d.open = true; fill(); }
    d.addEventListener("toggle", fill);
    return d;
  };

  const tools = h("div", { class: "row tree-tools" },
    h("button", { type: "button", class: "small", onclick: () => { tree.querySelectorAll("details").forEach((d) => { d.open = true; d.dispatchEvent(new Event("toggle")); }); } }, "Expand all"),
    h("button", { type: "button", class: "small", onclick: () => tree.querySelectorAll("details").forEach((d, i) => (d.open = i === 0)) }, "Collapse all"),
    h("span", { class: "muted" }, "Names are expanded from ISO 20022's abbreviations; the original tag is shown beside each."),
  );
  const tree = h("div", { class: "tree" }, node(r.document!, 0));
  return h("div", {}, tools, tree);
}

// ------------------------------------------------------------------ loading

function load(src: string) {
  if (!last || tab !== "issues") tab = "summary";
  if (src.length > BIG) { enterBig(src); schedule(); return; }
  leaveBig();
  input.value = src;
  input.scrollTop = 0;
  input.scrollLeft = 0;
  if (src.length > 1_000_000) { indexLines(src); drawGutter(); schedule(); } else run();
}
function loadSample(i: number) {
  load(SAMPLES[i].xml);
  sampleSelect.value = String(i);
  // a broken sample opens on its issues
  if (last && !last.valid) { tab = "issues"; render(last, lastMs); }
}
async function loadFile(f: File) {
  if (f.size > 50_000_000) { alert("That file is over 50 MB, which is more than this viewer handles."); return; }
  load(await f.text());
  sampleSelect.value = "";
}

const sampleSelect = $<HTMLSelectElement>("#sample");
sampleSelect.append(...SAMPLES.map((s, i) => h("option", { value: String(i) }, s.title)));
sampleSelect.addEventListener("change", () => { if (sampleSelect.value !== "") loadSample(+sampleSelect.value); });
$<HTMLInputElement>("#file").addEventListener("change", (e) => { const f = (e.target as HTMLInputElement).files?.[0]; if (f) loadFile(f); (e.target as HTMLInputElement).value = ""; });
$<HTMLButtonElement>("#clear").addEventListener("click", () => { load(""); sampleSelect.value = ""; input.focus(); });

input.addEventListener("input", () => {
  sampleSelect.value = "";
  if (input.value.length > BIG) { enterBig(input.value); schedule(); return; } // a huge paste
  indexLines(input.value);
  drawGutter();
  schedule();
});
input.addEventListener("scroll", drawGutter, { passive: true });
source.addEventListener("scroll", drawBig, { passive: true });
new ResizeObserver(drawGutter).observe(input);
new ResizeObserver(drawBig).observe(source);

let dragDepth = 0;
const drop = $<HTMLDivElement>("#drop");
window.addEventListener("dragenter", (e) => { if (e.dataTransfer?.types.includes("Files")) { dragDepth++; drop.hidden = false; } });
window.addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; drop.hidden = true; } });
window.addEventListener("dragover", (e) => e.preventDefault());
window.addEventListener("drop", (e) => {
  e.preventDefault();
  dragDepth = 0;
  drop.hidden = true;
  const f = e.dataTransfer?.files?.[0];
  if (f) loadFile(f);
  else { const t = e.dataTransfer?.getData("text/plain"); if (t) load(t); }
});

// ?sample=2 opens a sample (used by the README's links)
const q = new URLSearchParams(location.search).get("sample");
if (q !== null && SAMPLES[+q]) loadSample(+q); else run();
