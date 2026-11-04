/** A lookup table without a prototype, so a tag or code such as "__proto__" or "constructor" is just a missing key. */
export function table<T>(entries: Record<string, T>): Record<string, T> {
  return Object.assign(Object.create(null) as Record<string, T>, entries);
}

// Turns ISO 20022 tag names (IntrBkSttlmAmt) into words (Interbank Settlement Amount), and explains the fields
// people look at most. The abbreviations are ISO 20022's standard ones; the explanations are written for this viewer.

const TOKENS: Record<string, string> = table({
  Accptnc: "Acceptance", Acct: "Account", Actl: "Actual", Actn: "Action", Actvty: "Activity", Addtl: "Additional", Adjstmnt: "Adjustment",
  Admstn: "Administration", Admstr: "Administrator", Adr: "Address", Aggtd: "Aggregated", Agt: "Agent", Agts: "Agents", Amt: "Amount",
  Amts: "Amounts", Amtsd: "Amortised", And: "and", Anncd: "Announced", Any: "Any", Apld: "Applied", Apprvl: "Approval", Attndnc: "Attendance",
  Attndnt: "Attendant", Authntcn: "Authentication", Authrty: "Authority", Authstn: "Authorisation", Avlbty: "Availability",
  Bal: "Balance", Base: "Base", Bdry: "Boundary", Birth: "Birth", Bk: "Bank", Bldg: "Building", Bookg: "Booking", Br: "Branch", Brnch: "Branch",
  Brnd: "Brand", Btch: "Batch", Biz: "Business", Bx: "Box",
  Card: "Card", Cd: "Code", Cdt: "Credit", Cdtr: "Creditor", Cpblties: "Capabilities", Cpbl: "Capable", Cpy: "Copy", Ccy: "Currency",
  Cert: "Certificate", Chanl: "Channel", Chq: "Cheque", Chrg: "Charge", Chrgs: "Charges", City: "City", Clr: "Clearing", Cmpnt: "Component",
  Cntxt: "Context", CntrVal: "Counter-Value", Comssn: "Commission", Corp: "Corporate", Cre: "Creation", Crdhldr: "Cardholder", Csh: "Cash",
  Cstmr: "Customer", Ctct: "Contact", Ctgy: "Category", Ctrct: "Contract", Ctrctl: "Contractual", Ctrl: "Control", Ctry: "Country",
  Cur: "Current", Data: "Data", Dbt: "Debit", Dbtr: "Debtor", Deal: "Deal", Dept: "Department", Desc: "Description", Disp: "Display",
  Dnmtn: "Denomination", Doc: "Document", Domn: "Domain", Dplct: "Duplicate", Dpst: "Deposit", Dlvrg: "Delivering", Dscnt: "Discount",
  Dstrct: "District", Dt: "Date", Dts: "Dates", DtTm: "Date and Time", Dtls: "Details", Due: "Due", Dvsn: "Division",
  Elctrnc: "Electronic", Email: "Email", End: "End", Envlp: "Envelope", Envt: "Environment", Evt: "Event",
  Face: "Face", Fax: "Fax", Fcst: "Forecast", Fctv: "Effective", Fin: "Financial", Fllbck: "Fallback", Flr: "Floor", Fmly: "Family", Fmt: "Format",
  Fr: "From", Frms: "Forms", Frst: "First", Grnshee: "Garnishee", Grnshmt: "Garnishment", Grp: "Group",
  Id: "Identification", Incl: "Included", Ind: "Indicator", Indv: "Individual", Inf: "Information", Initg: "Initiating", Inpt: "Input",
  Instd: "Instructed", Instg: "Instructing", Instn: "Institution", Instr: "Instruction", Instrm: "Instrument", Insrnc: "Insurance",
  Intr: "Interbank", Intrmy: "Intermediary", Intrst: "Interest", Invcee: "Invoicee", Invcr: "Invoicer", Issg: "Issuing", Issr: "Issuer",
  Job: "Job", Lang: "Language", Last: "Last", Lcl: "Local", Lctn: "Location", Lgl: "Legal", Line: "Line", Lines: "Lines", Mdclr: "Medical",
  Mdcl: "Medical", Mdl: "Model", Manfctr: "Manufacturer", Mgmt: "Management", Mkt: "Market", Mmb: "Member", Mndt: "Mandate", Mob: "Mobile",
  Msg: "Message", Mtd: "Method", Mplyee: "Employee", Nb: "Number", Net: "Net", Nm: "Name", Note: "Note", Notes: "Notes", Ntry: "Entry",
  Ntries: "Entries", Ntty: "Entity", Of: "of", On: "On", Org: "Organisation", Orgnl: "Original", Orgtr: "Originator", Othr: "Other",
  Ownr: "Owner", Pctg: "Percentage", Pdct: "Product", Per: "per", Pg: "Page", Pgntn: "Pagination", Phne: "Phone", Plain: "Plain", Plc: "Place",
  Pmt: "Payment", Poolg: "Pooling", Pre: "Pre", Pd: "Paid", Prefrd: "Preferred", Pres: "Present", Pric: "Price", Prcg: "Processing", Prd: "Period",
  Prfx: "Prefix", Prtry: "Proprietary", Prt: "Print", Prty: "Priority", Prvc: "Province", Prvs: "Previous", Prvt: "Private", Prxy: "Proxy",
  Pst: "Post", Pstg: "Posting", Pstl: "Postal", Pty: "Party", Pties: "Parties", Purp: "Purpose", Pybl: "Payable", Qty: "Quantity", Qties: "Quantities",
  Qtn: "Quotation", Qry: "Query", Rate: "Rate", Rcncltn: "Reconciliation", Rcpt: "Receipt", Rcrd: "Record", Rcvg: "Receiving", Rdng: "Reading",
  Ref: "Reference", Refs: "References", Regn: "Registration", Req: "Request", Rfrd: "Referred", Rg: "Range", Rgltry: "Regulatory", Rjct: "Reject",
  Rltd: "Related", Rmbrsmnt: "Reimbursement", Rmt: "Remittance", Rmtd: "Remitted", Room: "Room", Rptg: "Reporting", Rsn: "Reason",
  Rspnsblty: "Responsibility", Rtr: "Return", Rvsl: "Reversal", RePresntmnt: "Re-presentment", Res: "Residence",
  Sale: "Sale", Schme: "Scheme", Scty: "Security", Seq: "Sequence", Sfkpg: "Safekeeping", Sfx: "Suffix", Shrt: "Short", Splmtry: "Supplementary",
  Src: "Source", Srl: "Serial", Start: "Start", Stmt: "Statement", Strd: "Structured", Strt: "Street", Sts: "Status", Sttlm: "Settlement",
  Sub: "Sub", Sum: "Sum", Svc: "Service", Svcr: "Servicer", Sys: "System", Tax: "Tax", Taxbl: "Taxable", Tech: "Technical", Termntn: "Termination",
  Till: "Till", Titl: "Title", Tm: "Time", Indctn: "Indication", To: "To", Tp: "Type", Trad: "Trade", Tradg: "Trading", Trck: "Track", Trf: "Transfer",
  Trgt: "Target", Ttl: "Total", Twn: "Town", Tx: "Transaction", Txs: "Transactions", Ultmt: "Ultimate", Unit: "Unit", Measr: "Measure",
  Ustrd: "Unstructured", Val: "Value", Vldtn: "Validation", Vldty: "Validity", Vrfctn: "Verification", Vrsn: "Version", Wdth: "Width",
  Width: "Width", Wvr: "Waiver", Xchg: "Exchange", Xpry: "Expiry", Yldd: "Yielded", Yr: "Year", Zone: "Zone", Lvl: "Level", Svcs: "Services",
  Nmbr: "Number", Days: "Days", Dtl: "Detail", Mtds: "Methods", Pstng: "Posting", Md: "Mode", Nxt: "Next", Infrstrctr: "Infrastructure",
  Thrd: "Third", For: "for",
  // acronyms and fixed codes
  BICFI: "BIC (Financial Institution)", BIC: "BIC", IBAN: "IBAN", UETR: "UETR", LEI: "LEI", ISIN: "ISIN", PAN: "Card Number (PAN)", POI: "Point of Interaction",
  CLS: "CLS", CSC: "Card Security Code", ICC: "Chip Card", EQ: "Equal", NEQ: "Not Equal", FI: "Financial Institution",
});
const KEYS = Object.keys(TOKENS).sort((a, b) => b.length - a.length);

/** Expands an ISO 20022 tag name into words. Unknown parts are kept as written. */
export function expand(tag: string): string {
  if (FIELDS[tag]) return FIELDS[tag].name;
  const words: string[] = [];
  let i = 0;
  while (i < tag.length) {
    const key = KEYS.find((k) => tag.startsWith(k, i) && !/[a-z]/.test(tag[i + k.length] ?? "") );
    if (key) { if (TOKENS[key]) words.push(TOKENS[key]); i += key.length; continue; }
    const m = /^[A-Z0-9]?[a-z]*\d*/.exec(tag.slice(i))!;
    const w = m[0] || tag[i];
    words.push(w);
    i += w.length;
  }
  const out = words.join(" ").replace(/\s+/g, " ").trim();
  return out.charAt(0).toUpperCase() + out.slice(1);
}

export interface Field { name: string; help?: string }

/** The fields worth explaining, with what they mean in practice. */
export const FIELDS: Record<string, Field> = table({
  Document: { name: "Document", help: "The message itself. Its namespace says which message and version it is." },
  AppHdr: { name: "Business Application Header", help: "Envelope details: who sent the message, to whom, and what it is." },
  FIToFICstmrCdtTrf: { name: "Bank-to-Bank Customer Credit Transfer", help: "A pacs.008: one bank tells another to pay money to a customer on behalf of its own customer." },
  BkToCstmrStmt: { name: "Bank-to-Customer Statement", help: "A camt.053: the bank's end-of-day statement of an account." },
  GrpHdr: { name: "Group Header", help: "Details that apply to the whole message: its ID, when it was created, how many transactions it carries." },
  MsgId: { name: "Message ID", help: "The sender's reference for this message. It must be unique for the sender." },
  CreDtTm: { name: "Created", help: "When the message was created." },
  NbOfTxs: { name: "Number of Transactions", help: "How many transactions the message carries. Must match the actual count." },
  CtrlSum: { name: "Control Sum", help: "The sum of every transaction amount, as a cross-check. Currencies are ignored." },
  TtlIntrBkSttlmAmt: { name: "Total Interbank Settlement Amount", help: "The total the banks settle between themselves, in one currency." },
  IntrBkSttlmDt: { name: "Interbank Settlement Date", help: "The day the money moves between the banks." },
  SttlmInf: { name: "Settlement Information", help: "How the two banks settle with each other." },
  SttlmMtd: { name: "Settlement Method", help: "INDA / INGA: through an account one bank holds with the other. COVE: through a separate cover payment. CLRG: through a clearing system." },
  ClrSys: { name: "Clearing System" },
  InstgAgt: { name: "Instructing Agent", help: "The bank that sends this message." },
  InstdAgt: { name: "Instructed Agent", help: "The bank that receives this message and must act on it." },
  CdtTrfTxInf: { name: "Credit Transfer Transaction", help: "One payment." },
  PmtId: { name: "Payment Identification", help: "References that follow the payment from end to end." },
  InstrId: { name: "Instruction ID", help: "A reference between the instructing and instructed bank only." },
  EndToEndId: { name: "End-to-End ID", help: "The payer's reference. It travels unchanged all the way to the payee." },
  TxId: { name: "Transaction ID", help: "The first instructing bank's reference for this transaction." },
  UETR: { name: "UETR", help: "Unique End-to-end Transaction Reference: a UUID (version 4) that lets the payment be tracked across banks, e.g. in SWIFT gpi." },
  PmtTpInf: { name: "Payment Type", help: "Service level, local instrument and purpose: how the payment should be processed." },
  SvcLvl: { name: "Service Level", help: "An agreed level of service, e.g. SEPA or G001 (SWIFT gpi)." },
  LclInstrm: { name: "Local Instrument" },
  CtgyPurp: { name: "Category Purpose", help: "The broad reason for the payment, e.g. SALA (salary) or SUPP (supplier)." },
  IntrBkSttlmAmt: { name: "Interbank Settlement Amount", help: "The amount the banks settle for this payment, after any charges taken so far." },
  InstdAmt: { name: "Instructed Amount", help: "The amount the payer asked to send, before any charges or conversion." },
  XchgRate: { name: "Exchange Rate" },
  ChrgBr: { name: "Charge Bearer", help: "Who pays the bank charges. DEBT: the payer. CRED: the payee. SHAR: shared, each pays their own bank. SLEV: as the service level says." },
  ChrgsInf: { name: "Charges", help: "Charges taken by banks along the way." },
  Dbtr: { name: "Debtor (Payer)", help: "The person or company paying." },
  DbtrAcct: { name: "Debtor Account", help: "The account the money is taken from." },
  DbtrAgt: { name: "Debtor Agent (Payer's Bank)", help: "The bank of the person or company paying." },
  Cdtr: { name: "Creditor (Payee)", help: "The person or company being paid." },
  CdtrAcct: { name: "Creditor Account", help: "The account the money goes to." },
  CdtrAgt: { name: "Creditor Agent (Payee's Bank)", help: "The bank of the person or company being paid." },
  UltmtDbtr: { name: "Ultimate Debtor", help: "The party the payment is really on behalf of, when that isn't the account holder." },
  UltmtCdtr: { name: "Ultimate Creditor", help: "The party who finally receives the funds, when that isn't the account holder." },
  IntrmyAgt1: { name: "Intermediary Bank 1", help: "A bank between the payer's and the payee's bank." },
  IntrmyAgt2: { name: "Intermediary Bank 2" },
  IntrmyAgt3: { name: "Intermediary Bank 3" },
  RmtInf: { name: "Remittance Information", help: "What the payment is for: an invoice number or a free-text note for the payee." },
  Ustrd: { name: "Unstructured", help: "Free text, e.g. 'Invoice 1234'." },
  Strd: { name: "Structured" },
  Purp: { name: "Purpose" },
  RgltryRptg: { name: "Regulatory Reporting", help: "Information some countries require for cross-border payments." },
  FinInstnId: { name: "Financial Institution" },
  BICFI: { name: "BIC", help: "The bank's SWIFT code: 4 letters for the bank, 2 for the country, 2 for the location, and an optional 3 for the branch." },
  ClrSysMmbId: { name: "Clearing System Member", help: "The bank's ID in a national clearing system, e.g. a US routing number." },
  MmbId: { name: "Member ID" },
  LEI: { name: "LEI", help: "Legal Entity Identifier: a 20-character global ID for a company." },
  IBAN: { name: "IBAN", help: "International Bank Account Number. Its two check digits catch most typing mistakes." },
  Othr: { name: "Other" },
  Nm: { name: "Name" },
  PstlAdr: { name: "Postal Address" },
  Ctry: { name: "Country", help: "ISO 3166 two-letter country code." },
  TwnNm: { name: "Town" },
  StrtNm: { name: "Street" },
  BldgNb: { name: "Building Number" },
  PstCd: { name: "Postcode" },
  AdrLine: { name: "Address Line" },
  Id: { name: "Identification" },
  Acct: { name: "Account" },
  Ccy: { name: "Currency", help: "ISO 4217 three-letter currency code." },
  Stmt: { name: "Statement", help: "One account statement." },
  ElctrncSeqNb: { name: "Electronic Sequence Number", help: "Counts statements for this account, so a missing one can be noticed." },
  LglSeqNb: { name: "Legal Sequence Number" },
  FrToDt: { name: "Period" },
  FrDtTm: { name: "From" },
  ToDtTm: { name: "To" },
  StmtPgntn: { name: "Page", help: "Page number, and whether this is the last page of the statement." },
  Bal: { name: "Balance", help: "A balance of the account at a point in time." },
  Tp: { name: "Type" },
  CdOrPrtry: { name: "Code" },
  Cd: { name: "Code" },
  Prtry: { name: "Proprietary" },
  Amt: { name: "Amount" },
  CdtDbtInd: { name: "Credit or Debit", help: "CRDT: money in (or a positive balance). DBIT: money out (or an overdrawn balance)." },
  Dt: { name: "Date" },
  DtTm: { name: "Date and Time" },
  TxsSummry: { name: "Transactions Summary", help: "Counts and totals of the entries. Must agree with the entries listed." },
  TtlNtries: { name: "All Entries" },
  TtlCdtNtries: { name: "Credit Entries" },
  TtlDbtNtries: { name: "Debit Entries" },
  NbOfNtries: { name: "Number of Entries" },
  Sum: { name: "Sum" },
  TtlNetNtry: { name: "Net Total", help: "Credits minus debits, with its direction." },
  Ntry: { name: "Entry", help: "One movement on the account." },
  NtryRef: { name: "Entry Reference" },
  Sts: { name: "Status", help: "BOOK: booked. PDNG: pending. INFO: for information only. Only booked entries change the booked balance." },
  BookgDt: { name: "Booking Date", help: "When the bank booked the entry." },
  ValDt: { name: "Value Date", help: "When the money starts or stops earning interest." },
  AcctSvcrRef: { name: "Bank's Reference", help: "The account-servicing bank's own reference." },
  BkTxCd: { name: "Bank Transaction Code", help: "What kind of movement this is, as Domain / Family / Sub-family (e.g. PMNT / RCDT / ESCT: a received SEPA credit transfer)." },
  Domn: { name: "Domain" },
  Fmly: { name: "Family" },
  SubFmlyCd: { name: "Sub-family" },
  NtryDtls: { name: "Entry Details" },
  TxDtls: { name: "Transaction Details" },
  Refs: { name: "References" },
  RltdPties: { name: "Related Parties", help: "Who paid or was paid." },
  RltdAgts: { name: "Related Banks" },
  AddtlNtryInf: { name: "Additional Entry Information", help: "Free text from the bank about this entry." },
  AddtlStmtInf: { name: "Additional Statement Information" },
  SplmtryData: { name: "Supplementary Data", help: "Extra data a community has agreed to add, outside the standard." },
});

/** Codes that appear as values, with what they mean. */
export const CODES: Record<string, Record<string, string>> = table({
  ChrgBr: table({ DEBT: "the payer pays all charges", CRED: "the payee pays all charges", SHAR: "each side pays its own bank", SLEV: "as the service level says" }),
  SttlmMtd: table({ INDA: "the instructed bank debits an account it holds for the sender", INGA: "the sender debits an account it holds for the instructed bank", COVE: "a separate cover payment", CLRG: "a clearing system" }),
  CdtDbtInd: table({ CRDT: "credit", DBIT: "debit" }),
  Cd: table({
    OPBD: "opening booked balance", CLBD: "closing booked balance", PRCD: "previous closing booked balance", CLAV: "closing available balance",
    ITBD: "interim booked balance", ITAV: "interim available balance", FWAV: "forward available balance", OPAV: "opening available balance",
    BOOK: "booked", PDNG: "pending", INFO: "information only",
  }),
});

/** "IntrBkSttlmAmt" → "Interbank Settlement Amount", with the tag kept when the expansion isn't certain. */
export function label(tag: string): { name: string; help?: string; tag: string } {
  const f = FIELDS[tag];
  return f ? { ...f, tag } : { name: expand(tag), tag };
}
