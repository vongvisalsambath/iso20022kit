// Samples are the conformance suite's own messages (made-up data), bundled as text.
import twoPayments from "../../conformance/cases/pacs008-valid-two-transactions.xml";
import clearing from "../../conformance/cases/pacs008-valid-minimal.xml";
import statement from "../../conformance/cases/camt053-valid-eur-iban.xml";
import overdraft from "../../conformance/cases/camt053-valid-usd-overdraft.xml";
import badTotals from "../../conformance/cases/pacs008-business-control-sum.xml";
import wrongOrder from "../../conformance/cases/pacs008-schema-wrong-order.xml";
import badIban from "../../conformance/cases/pacs008-business-iban-check-digits.xml";
import unbalanced from "../../conformance/cases/camt053-business-closing-balance.xml";

export const SAMPLES: { title: string; xml: string }[] = [
  { title: "pacs.008 · two payments", xml: twoPayments },
  { title: "pacs.008 · one payment through a clearing system", xml: clearing },
  { title: "camt.053 · EUR statement", xml: statement },
  { title: "camt.053 · overdrawn account, pending entry", xml: overdraft },
  { title: "Broken: totals don't add up", xml: badTotals },
  { title: "Broken: elements in the wrong order", xml: wrongOrder },
  { title: "Broken: IBAN check digits", xml: badIban },
  { title: "Broken: statement doesn't balance", xml: unbalanced },
];
