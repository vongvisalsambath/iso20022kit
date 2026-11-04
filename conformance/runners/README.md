# Runners

Each folder runs the conformance suite against one library and writes `results.jsonl`: one line per case,
`{"id": "...", "result": "valid" | "invalid" | "unsupported" | "crash", "detail": "..."}`.
`python3 conformance/compare.py` turns all of them into `docs/comparison.md`.

| Folder | Library | Run from that folder |
|---|---|---|
| `dotnet-iso20022kit` | Iso20022Kit (.NET) | `dotnet run --project ../../../src/Iso20022Kit.Cli -- conformance ../../suite.json > results.jsonl` |
| `java-prowide` | Prowide ISO 20022 (Java 21, Maven) | `mvn -q compile dependency:copy-dependencies -DoutputDirectory=target/lib && java -cp "target/classes:target/lib/*" conformance.Runner ../../suite.json > results.jsonl` |
| `go-moov` | Moov iso20022 (Go) | `go run . ../../suite.json > results.jsonl` |
| `rust-mx20022` | mx20022 (Rust) | `cargo run --release -- ../../suite.json > results.jsonl` |

Library versions are pinned in each folder's build file. To add a library, write a runner that prints the same lines and add it to `LIBRARIES` in `compare.py`.
