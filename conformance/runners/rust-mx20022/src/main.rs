//! Runs the conformance suite against the mx20022 crate (Rust).
//! Usage: cargo run --release -- ../../suite.json   → one JSON line per case on stdout.
//! A case counts as "valid" when the message type is detected, the payload deserialises into the
//! generated type, and validate_message() (the crate's XSD-constraint check) reports no violations.
//! mx20022 0.3.0 has generated types for pacs.008.001.08 but not camt.053.001.08 (it has 001.11),
//! so camt.053.001.08 cases are reported as "unsupported".
use mx20022::model::common::validate::IsoMessage;
use mx20022::model::generated::pacs::pacs_008_001_08::Document as Pacs008V08;
use mx20022::parse::{de, envelope};
use serde_json::{json, Value};
use std::panic;
use std::path::Path;

fn check(xml: &str) -> (&'static str, String) {
    let id = match envelope::detect_message_type(xml) {
        Ok(id) => id,
        Err(e) => return ("invalid", format!("detect: {e}")),
    };
    let dotted = id.dotted();
    if dotted != "pacs.008.001.08" {
        return ("unsupported", format!("no generated type for {dotted} in mx20022 0.3.0"));
    }
    // 0.3.x: from_str_in_envelope deserialises a bare Document or one inside a wrapper element
    let doc: Pacs008V08 = match de::from_str_in_envelope(xml) {
        Ok(d) => d,
        Err(e) => return ("invalid", format!("parse: {e}")),
    };
    let violations = doc.validate_message();
    if violations.is_empty() {
        ("valid", String::new())
    } else {
        ("invalid", violations.iter().take(3).map(|v| format!("{} {}", v.path, v.message)).collect::<Vec<_>>().join("; "))
    }
}

fn main() {
    let suite_path = std::env::args().nth(1).expect("usage: conformance-mx20022 suite.json");
    let suite: Value = serde_json::from_str(&std::fs::read_to_string(&suite_path).unwrap()).unwrap();
    let dir = Path::new(&suite_path).parent().unwrap();
    panic::set_hook(Box::new(|_| {}));
    for case in suite["cases"].as_array().unwrap() {
        let id = case["id"].as_str().unwrap();
        let xml = std::fs::read_to_string(dir.join(case["file"].as_str().unwrap())).unwrap();
        let (result, mut detail) = match panic::catch_unwind(|| check(&xml)) {
            Ok(r) => r,
            Err(_) => ("crash", "panicked".to_string()),
        };
        if detail.len() > 300 { detail.truncate(300); detail.push('…'); }
        println!("{}", json!({ "id": id, "result": result, "detail": detail }));
    }
}
