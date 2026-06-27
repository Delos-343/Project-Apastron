#!/usr/bin/env bash
# Build a self-contained, single-file Apastron for one runtime.
# Usage: ./publish.sh [rid]   e.g. ./publish.sh win-x64 | linux-x64 | osx-arm64
set -euo pipefail

RID="${1:-win-x64}"
PROJ="src/Apastron/Apastron.csproj"
OUT="publish/$RID"

echo "Publishing Apastron for $RID -> $OUT"
dotnet publish "$PROJ" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:SelfContained=true \
  -o "$OUT"

echo
echo "Done. Executable is in: $OUT"
echo "  Windows: $OUT/Apastron.exe"
echo "  Linux/macOS: $OUT/Apastron"
