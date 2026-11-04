// Runs the conformance suite against moov-io/iso20022 (Go).
// Usage: go run . ../../suite.json   → one JSON line per case on stdout.
// A case counts as "valid" when ParseIso20022Document succeeds and Validate() returns nil.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/moov-io/iso20022/pkg/document"
)

type suite struct {
	Cases []struct {
		ID   string `json:"id"`
		File string `json:"file"`
	} `json:"cases"`
}

type result struct {
	ID     string `json:"id"`
	Result string `json:"result"` // valid | invalid | unsupported | crash
	Detail string `json:"detail,omitempty"`
}

func run(buf []byte) (res result) {
	defer func() {
		if r := recover(); r != nil {
			res = result{Result: "crash", Detail: fmt.Sprint(r)}
		}
	}()
	doc, err := document.ParseIso20022Document(buf)
	if err != nil {
		if strings.Contains(strings.ToLower(err.Error()), "unsupported") {
			return result{Result: "unsupported", Detail: err.Error()}
		}
		return result{Result: "invalid", Detail: err.Error()}
	}
	if err := doc.Validate(); err != nil {
		return result{Result: "invalid", Detail: err.Error()}
	}
	return result{Result: "valid"}
}

func main() {
	path := os.Args[1]
	raw, err := os.ReadFile(path)
	if err != nil {
		panic(err)
	}
	var s suite
	if err := json.Unmarshal(raw, &s); err != nil {
		panic(err)
	}
	enc := json.NewEncoder(os.Stdout)
	for _, c := range s.Cases {
		buf, err := os.ReadFile(filepath.Join(filepath.Dir(path), c.File))
		if err != nil {
			panic(err)
		}
		r := run(buf)
		r.ID = c.ID
		if len(r.Detail) > 300 {
			r.Detail = r.Detail[:300] + "…"
		}
		_ = enc.Encode(r)
	}
}
