// Copies the static files next to the bundle, stamping a content hash on app.js so browsers never run a stale copy.
import { cpSync, readFileSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
cpSync("public", "dist", { recursive: true });
const hash = createHash("sha256").update(readFileSync("dist/app.js")).update(readFileSync("dist/styles.css")).digest("hex").slice(0, 10);
const html = readFileSync("dist/index.html", "utf8").replaceAll("app.js", `app.js?v=${hash}`).replaceAll("styles.css", `styles.css?v=${hash}`);
writeFileSync("dist/index.html", html);
console.log(`dist/ ready (${hash})`);
