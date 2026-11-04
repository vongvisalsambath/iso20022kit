#!/usr/bin/env bash
# Regenerates the browser viewer's schema models (viewer/src/schemas/*.json) from schemas/*.xsd.
set -euo pipefail
cd "$(dirname "$0")/.."
for xsd in schemas/*.xsd; do
  id="$(basename "$xsd" .xsd)"
  dotnet run --project tools/SchemaModel -c Release -- "$xsd" "viewer/src/schemas/$id.json"
done
