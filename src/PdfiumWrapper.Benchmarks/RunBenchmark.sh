#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$SCRIPT_DIR"

echo "Running benchmarks..."
dotnet run -c Release

if command -v python3 >/dev/null 2>&1; then
    PYTHON_BIN="python3"
elif command -v python >/dev/null 2>&1; then
    PYTHON_BIN="python"
else
    echo "Python is required to import benchmark results into benchmark.db."
    exit 1
fi

echo "Importing CSV results into benchmark.db..."
"$PYTHON_BIN" "$SCRIPT_DIR/ImportBenchmarkResults.py" \
    --db "$REPO_ROOT/benchmark.db" \
    --run-label runbenchmark \
    "$SCRIPT_DIR/BenchmarkDotNet.Artifacts/results"

echo "Done. Results imported into: $REPO_ROOT/benchmark.db"
