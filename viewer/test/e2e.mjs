// End-to-end checks of the built viewer in Chromium, served with the production headers from vercel.json.
// Happy paths (samples, tabs, editing, files, rule switches, copying, big files) and hostile ones (every file in
// tests/hostile, script-looking text, oversized files, odd URLs, framing, network use).
// Run: npm run build && npm run test:e2e
import { chromium } from "playwright";
import { createServer } from "node:http";
import { readFileSync, existsSync, writeFileSync, mkdtempSync } from "node:fs";
import { join, extname } from "node:path";
import { tmpdir } from "node:os";
import assert from "node:assert/strict";

const dist = new URL("../dist/", import.meta.url).pathname;
const hostile = new URL("../../tests/hostile/", import.meta.url).pathname;
const headers = JSON.parse(readFileSync(new URL("../vercel.json", import.meta.url))).headers[0].headers;
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".css": "text/css", ".svg": "image/svg+xml" };

const server = createServer((req, res) => {
  const path = decodeURIComponent(new URL(req.url, "http://x").pathname);
  const file = join(dist, path === "/" ? "index.html" : path);
  if (!file.startsWith(dist) || !existsSync(file)) { res.writeHead(404); res.end(); return; }
  for (const h of headers) res.setHeader(h.key, h.value);
  res.writeHead(200, { "content-type": types[extname(file)] ?? "application/octet-stream" });
  res.end(readFileSync(file));
}).listen(0, "127.0.0.1");
await new Promise((r) => server.once("listening", r));
const origin = `http://127.0.0.1:${server.address().port}`;

const browser = await chromium.launch();
let failures = 0, passes = 0;
async function check(name, fn) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  const problems = [];
  const requests = [];
  page.on("pageerror", (e) => problems.push("page error: " + e.message));
  page.on("console", (m) => { if (m.type() === "error") problems.push("console: " + m.text()); });
  page.on("request", (r) => requests.push(r.url()));
  page.on("dialog", async (d) => { problems.push("dialog: " + d.message()); await d.dismiss(); });
  await page.addInitScript(() => {
    window.__csp = [];
    document.addEventListener("securitypolicyviolation", (e) => window.__csp.push(`${e.violatedDirective}: ${e.blockedURI}`));
  });
  try {
    await fn(page, { problems, requests, context });
    const csp = await page.evaluate(() => window.__csp).catch(() => []);
    assert.deepEqual(csp, [], "CSP violations");
    passes++;
    console.log(`ok - ${name}`);
  } catch (e) {
    failures++;
    console.log(`not ok - ${name}\n  ${String(e.message).split("\n").join("\n  ")}`);
  } finally {
    await context.close();
  }
}
const status = (page) => page.textContent("#status");
const open = async (page, file) => { await page.setInputFiles("#file", file); await page.waitForFunction(() => /checked in|Not valid|Valid/.test(document.querySelector("#status")?.textContent ?? "")); };

// ---------------------------------------------------------------- happy paths

await check("every sample loads with the right verdict and no errors", async (page, { problems }) => {
  await page.goto(origin);
  const options = await page.$$eval("#sample option", (os) => os.map((o) => [o.value, o.textContent]).filter(([v]) => v !== ""));
  assert.ok(options.length >= 8);
  for (const [value, title] of options) {
    await page.selectOption("#sample", value);
    const s = await status(page);
    assert.equal(s.startsWith("Not valid"), title.startsWith("Broken"), `${title}: ${s}`);
    for (const tab of ["Summary", "Issues", "Fields"]) {
      const b = page.locator(".tabs button", { hasText: tab });
      if (await b.isEnabled()) { await b.click(); assert.ok((await page.textContent(".panel")).length > 10, `${title}/${tab} empty`); }
    }
  }
  assert.deepEqual(problems, []);
});

await check("clicking an issue selects its line in the editor", async (page) => {
  await page.goto(origin + "/?sample=4");
  await page.click(".issue");
  const sel = await page.evaluate(() => { const t = document.querySelector("#xml"); return t.value.slice(t.selectionStart, t.selectionEnd); });
  assert.match(sel, /CtrlSum/);
  assert.equal(await page.evaluate(() => document.activeElement?.id), "xml");
});

await check("editing re-checks the message", async (page) => {
  await page.goto(origin + "/?sample=0");
  assert.match(await status(page), /^Valid/);
  const xml = await page.inputValue("#xml");
  await page.fill("#xml", xml.replace("<NbOfTxs>2</NbOfTxs>", "<NbOfTxs>3</NbOfTxs>"));
  await page.waitForFunction(() => document.querySelector("#status").textContent.startsWith("Not valid"));
  await page.click(".tabs button:has-text('Issues')");
  assert.match(await page.textContent(".issue-list"), /PACS008-NBOFTXS/);
});

await check("a valid file opens", async (page) => {
  await page.goto(origin);
  await open(page, new URL("../../conformance/base/camt053-eur-iban.xml", import.meta.url).pathname);
  assert.match(await status(page), /^Valid/);
  assert.match(await page.textContent(".lead"), /Berlin Bikes GmbH/);
});

await check("rules can be switched off, the schema check can't", async (page) => {
  await page.goto(origin + "/?sample=6");
  assert.match(await status(page), /^Not valid/);
  await page.click(".rules summary");
  assert.equal(await page.isDisabled(".rules li:has-text('SCHEMA') input"), true);
  await page.uncheck(".rules li:has-text('IBAN-CHECKSUM') input");
  await page.waitForFunction(() => document.querySelector("#status").textContent.startsWith("Valid"));
});

await check("the report copies as JSON", async (page, { context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin });
  await page.goto(origin + "/?sample=4");
  await page.click("text=Copy report as JSON");
  const report = JSON.parse(await page.evaluate(() => navigator.clipboard.readText()));
  assert.equal(report.valid, false);
  assert.equal(report.issues[0].code, "PACS008-CTRLSUM");
});

await check("Tab moves focus out of the editor (no keyboard trap)", async (page) => {
  await page.goto(origin + "/?sample=0");
  await page.focus("#xml");
  await page.keyboard.press("Tab");
  assert.notEqual(await page.evaluate(() => document.activeElement?.id), "xml");
});

await check("a 2 MB+ statement opens read-only and jumps to its error", async (page) => {
  const camt = readFileSync(new URL("../../conformance/base/camt053-eur-iban.xml", import.meta.url), "utf8");
  const entry = /<Ntry>[\s\S]*?<\/Ntry>/.exec(camt)[0];
  const big = camt.replace(/<TxsSummry>[\s\S]*?<\/TxsSummry>\s*/, "").replace(/<Ntry>[\s\S]*<\/Ntry>/, Array(4000).fill(entry).join("\n"));
  const file = join(mkdtempSync(join(tmpdir(), "viewer-")), "big.xml");
  writeFileSync(file, big);
  await page.goto(origin);
  await open(page, file);
  assert.equal(await page.isVisible("#source"), true);
  assert.match(await page.textContent("#big-note"), /read-only/);
  await page.click(".tabs button:has-text('Issues')");
  await page.click(".issue");
  assert.ok(await page.locator(".srow.flashing").count() === 1);
});

// ---------------------------------------------------------------- hostile inputs

const cases = JSON.parse(readFileSync(join(hostile, "cases.json"), "utf8")).cases;
await check(`every hostile file (${cases.length}) gets the expected verdict without errors`, async (page, { problems, requests }) => {
  await page.goto(origin);
  for (const c of cases) {
    await open(page, join(hostile, c.file));
    const s = await status(page);
    assert.equal(s.startsWith("Valid"), c.expect.valid, `${c.id}: ${s}`);
    for (const tab of ["Summary", "Issues", "Fields"]) {
      const b = page.locator(".tabs button", { hasText: tab });
      if (await b.isEnabled()) await b.click();
    }
    // nothing from a message ever becomes markup
    assert.equal(await page.locator("#output img, #output script, #output iframe, #output svg").count(), 0, c.id);
    // invisible and direction-changing characters are shown as markers, never rendered as themselves
    const raw = await page.evaluate(() => /[​-‏‪-‮⁦-⁩\u0080-\u009F]/.test(document.querySelector("#output").textContent));
    assert.equal(raw, false, `${c.id}: a hidden character reached the page`);
  }
  assert.deepEqual(problems, []);
  assert.ok(requests.every((u) => u.startsWith(origin)), "a request left the page's origin: " + requests.filter((u) => !u.startsWith(origin)).join(", "));
});

await check("a right-to-left override in a name is shown as a marker", async (page) => {
  await page.goto(origin);
  await open(page, join(hostile, "bidi-override-in-name.xml"));
  await page.click(".tabs button:has-text('Summary')");
  assert.ok(await page.locator(".summary .hidden-char").count() >= 1);
  assert.match(await page.textContent(".summary"), /\\u202E/);
});

await check("script-looking text stays text", async (page, { problems }) => {
  await page.goto(origin);
  await open(page, join(hostile, "markup-in-text.xml"));
  assert.match(await page.textContent(".summary"), /<img src=x onerror=alert\(1\)>/);
  assert.equal(await page.locator("img:not(.brand img)").count(), 0);
  assert.deepEqual(problems, []);
});

await check("files over 50 MB are refused before reading", async (page, { problems }) => {
  const file = join(mkdtempSync(join(tmpdir(), "viewer-")), "huge.xml");
  writeFileSync(file, Buffer.alloc(51_000_000, 0x20));
  await page.goto(origin);
  await page.setInputFiles("#file", file);
  await page.waitForTimeout(500);
  assert.ok(problems.some((p) => p.startsWith("dialog:") && /50 MB/.test(p)), problems.join("; "));
  problems.length = 0;
});

await check("odd ?sample= values are ignored", async (page, { problems }) => {
  for (const q of ["__proto__", "constructor", "-1", "99", "1e9", "%00", "<script>"]) {
    await page.goto(origin + "/?sample=" + encodeURIComponent(q));
    assert.match(await page.textContent("#output"), /Read and check an ISO 20022 message/, q);
  }
  assert.deepEqual(problems, []);
});

await check("dropped text is checked like a file", async (page) => {
  await page.goto(origin);
  const xml = readFileSync(join(hostile, "two-documents.xml"), "utf8");
  await page.evaluate((text) => {
    const dt = new DataTransfer();
    dt.setData("text/plain", text);
    window.dispatchEvent(new DragEvent("drop", { dataTransfer: dt, bubbles: true, cancelable: true }));
  }, xml);
  await page.waitForFunction(() => document.querySelector("#status").textContent.startsWith("Not valid"));
  await page.click(".tabs button:has-text('Issues')");
  assert.match(await page.textContent(".issue-list"), /MSG-MULTIPLE-DOCUMENTS/);
});

await check("Trusted Types block HTML injection sinks", async (page) => {
  await page.goto(origin);
  const blocked = await page.evaluate(() => { try { document.body.insertAdjacentHTML("beforeend", "<b id=tt>x</b>"); return false; } catch { return true; } });
  assert.equal(blocked, true);
  await page.evaluate(() => { window.__csp = []; }); // the blocked attempt itself is a violation report
});

await check("the page can't be framed by another site", async (page) => {
  // a different origin (localhost vs 127.0.0.1) tries to frame it
  const other = createServer((_, res) => { res.writeHead(200, { "content-type": "text/html" }); res.end(`<iframe src="${origin}/"></iframe>`); }).listen(0, "127.0.0.1");
  await new Promise((r) => other.once("listening", r));
  const refused = [];
  page.on("console", (m) => { if (/Refused to (display|frame)/.test(m.text())) refused.push(m.text()); });
  await page.goto(`http://localhost:${other.address().port}/`);
  await page.waitForTimeout(800);
  const framed = page.frames().find((f) => f.url().startsWith(origin));
  const loaded = framed ? await framed.evaluate(() => !!document.querySelector("#xml")).catch(() => false) : false;
  other.close();
  assert.equal(loaded, false, "the viewer loaded inside another site's frame");
});

await browser.close();
server.close();
console.log(`\n# pass ${passes}\n# fail ${failures}`);
process.exit(failures ? 1 : 0);
