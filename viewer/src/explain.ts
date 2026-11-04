// Plain-English summary of a message: who pays whom, how much, and when (pacs.008); what happened on the
// account (camt.053). Everything links back to the element it came from, so the UI can jump to its line.
import { child, children, text, attr, type XElement } from "./xml";
import { CODES, table } from "./names";

export interface Fact { label: string; value: string; el?: XElement | null; help?: string }
export interface Row { cells: string[]; el: XElement; tone?: "in" | "out" | "muted" }
export interface Section { title: string; lead?: string; facts: Fact[]; table?: { head: string[]; rows: Row[]; foot?: string[] }; el?: XElement }
export interface Summary { title: string; lead: string; sections: Section[] }

// ------------------------------------------------------------------ formatting

/** "1500.25" → "1,500.25", without going through floating point. */
export function groupDigits(v: string): string {
  const neg = v.startsWith("-");
  const [i, f] = v.replace(/^[+-]/, "").split(".");
  const int = i.replace(/^0+(?=\d)/, "").replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return (neg ? "−" : "") + int + (f !== undefined ? "." + f : "");
}
export const money = (el: XElement | null): string | null => {
  const v = el?.text.trim();
  if (!v) return null;
  const ccy = attr(el, "Ccy")?.value;
  return `${groupDigits(v)}${ccy ? " " + ccy : ""}`;
};

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
/** "2026-11-04" → "Wed 4 Nov 2026". Dates are calendar dates: no time zone is applied. */
export function friendlyDate(v: string | null): string | null {
  if (!v) return null;
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(v);
  if (!m) return v;
  const d = new Date(Date.UTC(+m[1], +m[2] - 1, +m[3]));
  if (isNaN(+d)) return v;
  return `${DAYS[d.getUTCDay()]} ${+m[3]} ${MONTHS[+m[2] - 1]} ${m[1]}`;
}
/** "2026-11-04T09:30:00+07:00" → "Wed 4 Nov 2026, 09:30 (UTC+07:00)": the time as the sender wrote it. */
export function friendlyDateTime(v: string | null): string | null {
  if (!v) return null;
  const m = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(:\d{2})?(?:\.\d+)?(Z|[+-]\d{2}:\d{2})?$/.exec(v);
  if (!m) return v;
  const zone = m[4] === "Z" ? " (UTC)" : m[4] ? ` (UTC${m[4]})` : " (no time zone given)";
  return `${friendlyDate(m[1])}, ${m[2]}${m[3] && m[3] !== ":00" ? m[3] : ""}${zone}`;
}
/** Groups an IBAN in fours, the way it's printed on paper. */
export const printIban = (v: string) => v.replace(/\s+/g, "").replace(/(.{4})(?=.)/g, "$1 ");

const signed = (sign: string, v: string) => (/[1-9]/.test(v) ? sign + v : v);
const capital = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);
const words = ["no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
const count = (n: number, one: string, many = one + "s") => `${words[n] ?? n}${one ? " " + (n === 1 ? one : many) : ""}`;
const join = (parts: (string | null | undefined)[], sep = ", ") => parts.filter(Boolean).join(sep);

// ------------------------------------------------------------------ parties

function place(p: XElement | null): string | null {
  const a = child(p, "PstlAdr");
  return join([text(a, "TwnNm"), text(a, "Ctry")]) || null;
}
function account(a: XElement | null): string | null {
  if (!a) return null;
  const iban = text(a, "Id", "IBAN");
  if (iban) return `IBAN ${printIban(iban)}`;
  const other = text(a, "Id", "Othr", "Id");
  const scheme = text(a, "Id", "Othr", "SchmeNm", "Cd") ?? text(a, "Id", "Othr", "SchmeNm", "Prtry");
  if (other) return `account ${other}${scheme ? ` (${scheme})` : ""}`;
  const proxy = text(a, "Prxy", "Id");
  return proxy ? `proxy ${proxy}` : null;
}
/** A bank: its BIC, else its clearing-system member ID, else its name. */
export function bank(agent: XElement | null): string | null {
  const fi = child(agent, "FinInstnId");
  if (!fi) return null;
  const bic = text(fi, "BICFI");
  const member = text(fi, "ClrSysMmbId", "MmbId");
  const system = text(fi, "ClrSysMmbId", "ClrSysId", "Cd") ?? text(fi, "ClrSysMmbId", "ClrSysId", "Prtry");
  const name = text(fi, "Nm");
  if (bic) return name ? `${name} (${bic})` : bic;
  if (member) return `${name ? name + ", " : ""}${system ?? "clearing"} member ${member}`;
  return name ?? text(fi, "Othr", "Id") ?? text(fi, "LEI");
}
function partyName(p: XElement | null): string | null {
  return text(p, "Nm") ?? text(p, "Pty", "Nm") ?? text(p, "Agt", "FinInstnId", "BICFI");
}

// ------------------------------------------------------------------ pacs.008

const SETTLEMENT: Record<string, string> = table({
  INDA: "through an account the receiving bank holds for the sender (INDA)",
  INGA: "through an account the sender holds for the receiving bank (INGA)",
  COVE: "through a separate cover payment between correspondent banks (COVE)",
  CLRG: "through a clearing system (CLRG)",
});
const CHARGES: Record<string, string> = table({
  DEBT: "The payer pays all bank charges (DEBT).",
  CRED: "The payee pays all bank charges (CRED).",
  SHAR: "Charges are shared: each side pays its own bank (SHAR).",
  SLEV: "Charges follow the rules of the service level (SLEV).",
});

function pacs008(doc: XElement): Summary {
  const body = child(doc, "FIToFICstmrCdtTrf");
  const hdr = child(body, "GrpHdr");
  const txs = children(body, "CdtTrfTxInf");
  const from = bank(child(hdr, "InstgAgt")) ?? bank(child(txs[0], "InstgAgt")) ?? bank(child(txs[0], "DbtrAgt"));
  const to = bank(child(hdr, "InstdAgt")) ?? bank(child(txs[0], "InstdAgt")) ?? bank(child(txs[0], "CdtrAgt"));
  const total = money(child(hdr, "TtlIntrBkSttlmAmt"));
  const sttlmDate = text(hdr, "IntrBkSttlmDt") ?? text(txs[0], "IntrBkSttlmDt");
  const method = text(hdr, "SttlmInf", "SttlmMtd");
  const clearing = text(hdr, "SttlmInf", "ClrSys", "Cd") ?? text(hdr, "SttlmInf", "ClrSys", "Prtry");
  const amounts = txs.map((t) => money(child(t, "IntrBkSttlmAmt")));

  const lead = join([
    `${from ?? "A bank"} asks ${to ?? "another bank"} to make ${count(txs.length, "customer payment")}`,
    total ? ` totalling ${total}` : txs.length === 1 && amounts[0] ? ` of ${amounts[0]}` : "",
    sttlmDate ? `, settled between the banks on ${friendlyDate(sttlmDate)}` : "",
    method ? ` ${method === "CLRG" && clearing ? `through the ${clearing} clearing system (CLRG)` : SETTLEMENT[method] ?? `(${method})`}` : "",
    ".",
  ], "");

  const sections: Section[] = [{
    title: "Message",
    el: hdr ?? undefined,
    facts: [
      { label: "Message ID", value: text(hdr, "MsgId") ?? "–", el: child(hdr, "MsgId"), help: "The sender's unique reference for this message." },
      { label: "Created", value: friendlyDateTime(text(hdr, "CreDtTm")) ?? "–", el: child(hdr, "CreDtTm") },
      { label: "Transactions", value: text(hdr, "NbOfTxs") ?? String(txs.length), el: child(hdr, "NbOfTxs") },
      ...(text(hdr, "CtrlSum") ? [{ label: "Control sum", value: groupDigits(text(hdr, "CtrlSum")!), el: child(hdr, "CtrlSum") }] : []),
      ...(total ? [{ label: "Total settled", value: total, el: child(hdr, "TtlIntrBkSttlmAmt") }] : []),
      ...(sttlmDate ? [{ label: "Settlement date", value: friendlyDate(sttlmDate)!, el: child(hdr, "IntrBkSttlmDt") ?? child(txs[0], "IntrBkSttlmDt") }] : []),
      ...(method ? [{ label: "Settlement method", value: `${method}: ${CODES.SttlmMtd[method] ?? ""}`, el: child(hdr, "SttlmInf", "SttlmMtd") }] : []),
      ...(from ? [{ label: "Sending bank", value: from, el: child(hdr, "InstgAgt") }] : []),
      ...(to ? [{ label: "Receiving bank", value: to, el: child(hdr, "InstdAgt") }] : []),
    ],
  }];

  txs.forEach((t, n) => {
    const dbtr = child(t, "Dbtr"), cdtr = child(t, "Cdtr");
    const amount = money(child(t, "IntrBkSttlmAmt"));
    const instructed = money(child(t, "InstdAmt"));
    const chrg = text(t, "ChrgBr");
    const who = (p: XElement | null) => { const nm = text(p, "Nm"); const pl = place(p); return nm ? `${nm}${pl ? ` (${pl})` : ""}` : "someone"; };
    const remit = children(child(t, "RmtInf"), "Ustrd").map((u) => u.text.trim()).join(" ") ||
      join(children(child(t, "RmtInf"), "Strd").map((s) => text(s, "RfrdDocInf", "Nb") ?? text(s, "CdtrRefInf", "Ref")));
    const lead = join([
      `${who(dbtr)} pays ${amount ?? "an amount"} to ${who(cdtr)}.`,
      instructed && instructed !== amount ? `They asked to send ${instructed}; the difference is charges or currency conversion.` : null,
      chrg ? CHARGES[chrg] ?? `Charge bearer: ${chrg}.` : null,
      remit ? `For: “${remit}”.` : null,
    ], " ");
    sections.push({
      title: txs.length > 1 ? `Payment ${n + 1} of ${txs.length}` : "Payment",
      lead,
      el: t,
      facts: [
        { label: "Amount", value: amount ?? "–", el: child(t, "IntrBkSttlmAmt") },
        ...(instructed ? [{ label: "Instructed amount", value: instructed, el: child(t, "InstdAmt") }] : []),
        { label: "Payer", value: join([text(dbtr, "Nm"), place(dbtr)]) || "–", el: dbtr },
        { label: "Payer's account", value: account(child(t, "DbtrAcct")) ?? "–", el: child(t, "DbtrAcct") },
        { label: "Payer's bank", value: bank(child(t, "DbtrAgt")) ?? "–", el: child(t, "DbtrAgt") },
        ...[1, 2, 3].flatMap((k) => { const b = bank(child(t, `IntrmyAgt${k}`)); return b ? [{ label: `Intermediary bank ${k}`, value: b, el: child(t, `IntrmyAgt${k}`) }] : []; }),
        { label: "Payee's bank", value: bank(child(t, "CdtrAgt")) ?? "–", el: child(t, "CdtrAgt") },
        { label: "Payee's account", value: account(child(t, "CdtrAcct")) ?? "–", el: child(t, "CdtrAcct") },
        { label: "Payee", value: join([text(cdtr, "Nm"), place(cdtr)]) || "–", el: cdtr },
        ...(text(t, "UltmtDbtr", "Nm") ? [{ label: "On behalf of", value: text(t, "UltmtDbtr", "Nm")!, el: child(t, "UltmtDbtr") }] : []),
        ...(text(t, "UltmtCdtr", "Nm") ? [{ label: "Finally for", value: text(t, "UltmtCdtr", "Nm")!, el: child(t, "UltmtCdtr") }] : []),
        { label: "End-to-end ID", value: text(t, "PmtId", "EndToEndId") ?? "–", el: child(t, "PmtId", "EndToEndId"), help: "The payer's reference, passed on unchanged to the payee." },
        ...(text(t, "PmtId", "UETR") ? [{ label: "UETR", value: text(t, "PmtId", "UETR")!, el: child(t, "PmtId", "UETR"), help: "A unique ID used to track the payment across banks (SWIFT gpi)." }] : []),
        ...(remit ? [{ label: "Remittance", value: remit, el: child(t, "RmtInf") }] : []),
      ],
    });
  });
  return { title: "Customer credit transfer (pacs.008)", lead, sections };
}

// ------------------------------------------------------------------ camt.053

const DOMAINS: Record<string, string> = table({ PMNT: "Payments", ACMT: "Account management", CAMT: "Cash management", LDAS: "Loans and deposits", SECU: "Securities", FORX: "Foreign exchange", TRAD: "Trade services", XTND: "Extended" });
const FAMILIES: Record<string, string> = table({
  RCDT: "received credit transfer", ICDT: "sent credit transfer", RDDT: "received direct debit", IDDT: "direct debit collected",
  RRCT: "received instant credit transfer", IRCT: "sent instant credit transfer", RCHQ: "received cheque", ICHQ: "issued cheque",
  CCRD: "card payment", MCRD: "card acquiring", CNTR: "counter transaction", MCOP: "other credit", MDOP: "other debit",
});
const SUBFAMILIES: Record<string, string> = table({
  ESCT: "SEPA credit transfer", DMCT: "domestic credit transfer", XBCT: "cross-border credit transfer", SALA: "salary",
  STDO: "standing order", CHRG: "charges", INTR: "interest", ESDD: "SEPA direct debit", BBDD: "SEPA B2B direct debit",
  POSD: "card payment at a point of sale", CWDL: "cash withdrawal",
});
function txCode(ntry: XElement): { code: string; meaning: string } | null {
  const d = child(ntry, "BkTxCd", "Domn");
  const domain = text(d, "Cd"), family = text(d, "Fmly", "Cd"), sub = text(d, "Fmly", "SubFmlyCd");
  if (!domain) {
    const p = text(ntry, "BkTxCd", "Prtry", "Cd");
    return p ? { code: p, meaning: "bank's own code" } : null;
  }
  const meaning = join([family && FAMILIES[family], sub && SUBFAMILIES[sub]], ": ") || DOMAINS[domain] || "";
  return { code: join([domain, family, sub], " / "), meaning };
}

/** A balance as the customer would read it: overdrawn balances shown negative. */
function balanceText(b: XElement | null): string | null {
  const amt = child(b, "Amt");
  const v = money(amt);
  if (!v) return null;
  return text(b, "CdtDbtInd") === "DBIT" ? `−${v} (overdrawn)` : v;
}

function entryDescription(n: XElement): string {
  const tx = children(child(n, "NtryDtls"), "TxDtls")[0] ?? null;
  const credit = text(n, "CdtDbtInd") === "CRDT";
  const other = credit ? partyName(child(tx, "RltdPties", "Dbtr")) : partyName(child(tx, "RltdPties", "Cdtr"));
  const remit = text(tx, "RmtInf", "Ustrd");
  const info = text(n, "AddtlNtryInf");
  return join([other ? `${credit ? "from" : "to"} ${other}` : null, remit ? `“${remit}”` : null, !other && !remit ? info : null], " · ") || info || "";
}

function camt053(doc: XElement): Summary {
  const body = child(doc, "BkToCstmrStmt");
  const hdr = child(body, "GrpHdr");
  const stmts = children(body, "Stmt");
  const sections: Section[] = [{
    title: "Message",
    el: hdr ?? undefined,
    facts: [
      { label: "Message ID", value: text(hdr, "MsgId") ?? "–", el: child(hdr, "MsgId") },
      { label: "Created", value: friendlyDateTime(text(hdr, "CreDtTm")) ?? "–", el: child(hdr, "CreDtTm") },
      ...(text(hdr, "MsgRcpt", "Nm") ? [{ label: "For", value: text(hdr, "MsgRcpt", "Nm")!, el: child(hdr, "MsgRcpt") }] : []),
      { label: "Statements", value: String(stmts.length) },
    ],
  }];
  const leads: string[] = [];

  for (const s of stmts) {
    const acct = child(s, "Acct");
    const owner = text(acct, "Ownr", "Nm");
    const servicer = bank(child(acct, "Svcr"));
    const acctText = account(acct) ?? "an account";
    const ccy = text(acct, "Ccy");
    const bals = children(s, "Bal");
    const code = (b: XElement) => text(b, "Tp", "CdOrPrtry", "Cd") ?? text(b, "Tp", "CdOrPrtry", "Prtry");
    const opening = bals.find((b) => code(b) === "OPBD" || code(b) === "PRCD") ?? null;
    const closing = bals.find((b) => code(b) === "CLBD") ?? null;
    const entries = children(s, "Ntry");
    const credits = entries.filter((n) => text(n, "CdtDbtInd") === "CRDT");
    const debits = entries.filter((n) => text(n, "CdtDbtInd") === "DBIT");
    const from = text(s, "FrToDt", "FrDtTm"), to = text(s, "FrToDt", "ToDtTm");
    const sameDay = from && to && from.slice(0, 10) === to.slice(0, 10);
    const period = sameDay ? friendlyDate(from) : from && to ? `${friendlyDate(from)} to ${friendlyDate(to)}` : friendlyDate(text(s, "CreDtTm"));
    const seq = text(s, "ElctrncSeqNb") ?? text(s, "LglSeqNb");
    const booked = (n: XElement) => (text(n, "Sts", "Cd") ?? text(n, "Sts", "Prtry")) === "BOOK";
    const unbooked = entries.filter((n) => !booked(n)).length;

    const lead = join([
      `Statement${seq ? ` no. ${seq}` : ""} for ${owner ? `${owner}'s ` : ""}${acctText}${ccy ? ` (${ccy})` : ""}${servicer ? ` at ${servicer}` : ""}${period ? `, ${period}` : ""}.`,
      opening && closing ? `The balance went from ${balanceText(opening)} to ${balanceText(closing)}.` : closing ? `Closing balance: ${balanceText(closing)}.` : null,
      entries.length ? capital(`${count(credits.length, "credit")} and ${count(debits.length, "debit")}${unbooked ? `, ${unbooked === 1 ? "one" : count(unbooked, "")} of them not yet booked` : ""}.`) : "No entries.",
    ], " ");
    leads.push(lead);

    const sumOf = (list: XElement[]) => {
      const scale = Math.max(2, ...list.map((n) => (text(n, "Amt")?.split(".")[1] ?? "").length));
      const total = list.reduce((a, n) => {
        const [i, f = ""] = (text(n, "Amt") ?? "0").split(".");
        return a + BigInt(i + f.padEnd(scale, "0"));
      }, 0n);
      const str = total.toString().padStart(scale + 1, "0");
      return groupDigits(`${str.slice(0, -scale)}.${str.slice(-scale)}`);
    };

    sections.push({
      title: stmts.length > 1 ? `Statement ${text(s, "Id") ?? ""}` : "Account",
      lead: stmts.length > 1 ? lead : undefined,
      el: s,
      facts: [
        { label: "Account", value: acctText, el: acct },
        ...(owner ? [{ label: "Owner", value: owner, el: child(acct, "Ownr") }] : []),
        ...(servicer ? [{ label: "Bank", value: servicer, el: child(acct, "Svcr") }] : []),
        ...(period ? [{ label: "Period", value: period, el: child(s, "FrToDt") }] : []),
        ...(seq ? [{ label: "Sequence number", value: seq, el: child(s, "ElctrncSeqNb") ?? child(s, "LglSeqNb"), help: "Statements are numbered so a missing one can be noticed." }] : []),
        ...bals.map((b) => {
          const c = code(b) ?? "?";
          return { label: `${(CODES.Cd[c] ?? c).replace(/^./, (x) => x.toUpperCase())} (${c})`, value: `${balanceText(b) ?? "–"}${text(b, "Dt", "Dt") ? `, ${friendlyDate(text(b, "Dt", "Dt"))}` : ""}`, el: b };
        }),
      ],
      table: entries.length ? {
        head: ["Booked", "", "Amount", "Details", "Type", "Status"],
        rows: entries.map((n) => {
          const credit = text(n, "CdtDbtInd") === "CRDT";
          const status = text(n, "Sts", "Cd") ?? text(n, "Sts", "Prtry") ?? "";
          const tc = txCode(n);
          return {
            el: n,
            tone: status !== "BOOK" ? "muted" : credit ? "in" : "out",
            cells: [
              friendlyDate(text(n, "BookgDt", "Dt") ?? text(n, "BookgDt", "DtTm")) ?? "not booked",
              credit ? "In" : "Out",
              `${credit ? "+" : "−"}${money(child(n, "Amt")) ?? ""}`,
              entryDescription(n),
              tc ? `${tc.meaning}${tc.meaning ? " " : ""}(${tc.code})` : "",
              CODES.Cd[status] ?? status,
            ],
          } as Row;
        }),
        foot: ["", "", "", `Booked: in ${signed("+", sumOf(credits.filter(booked)))}, out ${signed("−", sumOf(debits.filter(booked)))}${ccy ? " " + ccy : ""}`, "", ""],
      } : undefined,
    });
  }
  return { title: "Bank-to-customer statement (camt.053)", lead: leads.join(" "), sections };
}

export function explain(message: string | null, doc: XElement | null): Summary | null {
  if (!doc || !message) return null;
  if (message.startsWith("pacs.008")) return pacs008(doc);
  if (message.startsWith("camt.053")) return camt053(doc);
  return null;
}
