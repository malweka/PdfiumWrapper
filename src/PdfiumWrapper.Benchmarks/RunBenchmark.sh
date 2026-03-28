#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "Running benchmarks..."
dotnet run -c Release

echo "Moving CSV results..."
find "$SCRIPT_DIR/BenchmarkDotNet.Artifacts/results" -name "*.csv" -exec mv {} "$SCRIPT_DIR/" \;

echo "Done. CSV files are in: $SCRIPT_DIR"
ls -1 "$SCRIPT_DIR"/*.csv 2>/dev/null
