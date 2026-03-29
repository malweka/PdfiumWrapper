# Benchmarking Guide

This document explains how benchmarking works in this repo, what gets stored in `benchmark.db`, and how to query it later when comparing runs.

## Overview

Benchmarks live in `src/PdfiumWrapper.Benchmarks`.

They use BenchmarkDotNet to run four benchmark groups:

- `PdfToJpegBenchmark`
- `PdfToPngBenchmark`
- `PdfToTiffBenchmark`
- `PdfMergeBenchmark`

The benchmark entrypoint is `src/PdfiumWrapper.Benchmarks/Program.cs`.

CSV export is enabled explicitly, with:

- invariant culture
- milliseconds as the time unit
- one CSV report per benchmark class

## Test Documents

The benchmark project reuses the PDFs from `src/PdfiumWrapper.Tests/Docs/` and copies them into the benchmark output at build time.

Current benchmark documents:

- `doc-1-page.pdf`
- `doc-3-pages-with-comments.pdf`
- `contract.pdf`
- `fw2.pdf`
- `presentation.pdf`

Each benchmark runs once per document.

## How To Run

Use one of these launchers:

- `src/PdfiumWrapper.Benchmarks/RunBenchmark.sh`
- `src/PdfiumWrapper.Benchmarks/RunBenchmark.cmd`

Both scripts do the same high-level flow:

1. `dotnet run -c Release`
2. let BenchmarkDotNet write CSVs into `src/PdfiumWrapper.Benchmarks/BenchmarkDotNet.Artifacts/results/`
3. import those CSVs into `benchmark.db`

The scripts no longer copy CSVs into `src/PdfiumWrapper.Benchmarks/`.

## What Is Being Measured

Each benchmark measures end-to-end work for that operation, not isolated internal stages.

Examples:

- image benchmarks include document load, page rendering, encoding, and output write for that benchmark path
- `PdfMergeBenchmark` includes opening the first source into `PdfMerger`, opening the second source into `PdfDocument`, importing pages, and saving the merged output

That matters when reading regressions. A change in benchmark time is not automatically caused by the obvious API in the benchmark name.

## BenchmarkDotNet Output

BenchmarkDotNet writes CSVs under:

- `src/PdfiumWrapper.Benchmarks/BenchmarkDotNet.Artifacts/results/`

Typical files:

- `PdfiumWrapper.Benchmarks.PdfToJpegBenchmark-report.csv`
- `PdfiumWrapper.Benchmarks.PdfToPngBenchmark-report.csv`
- `PdfiumWrapper.Benchmarks.PdfToTiffBenchmark-report.csv`
- `PdfiumWrapper.Benchmarks.PdfMergeBenchmark-report.csv`

Important columns imported into the database:

- `Method`
- `Document`
- `Job`
- `Runtime`
- `Platform`
- `Mean [ms]`
- `Error [ms]`
- `StdDev [ms]`
- `Median [ms]` when present

The full CSV row is also stored as JSON for later inspection.

## Database

Benchmark history is stored in:

- `benchmark.db`

The importer script is:

- `src/PdfiumWrapper.Benchmarks/ImportBenchmarkResults.py`

It creates and updates two tables:

### `runs`

One row per imported benchmark run.

Columns:

- `run_id`: text primary key
- `run_label`: logical label such as `previous`, `current`, or `runbenchmark`
- `source_dir`: directory that was imported
- `imported_at`: when the DB import happened
- `first_file_mtime`: earliest CSV timestamp in that run
- `last_file_mtime`: latest CSV timestamp in that run
- `file_count`: number of CSV files imported

### `benchmark_results`

One row per benchmark result line from the CSVs.

Columns:

- `run_id`
- `benchmark_file`
- `benchmark_name`
- `source_csv`
- `method`
- `document`
- `job`
- `runtime`
- `platform`
- `mean_ms`
- `error_ms`
- `stddev_ms`
- `median_ms`
- `raw_row_json`

Uniqueness is enforced on:

- `run_id`
- `benchmark_file`
- `method`
- `document`

So re-importing the same run replaces that run cleanly instead of duplicating it.

## Run ID Format

`run_id` uses the earliest CSV file timestamp for the imported run, formatted as:

- `yyyyMMddHHmm`

Example:

- `202603291618`

Why earliest timestamp:

- one benchmark invocation produces multiple CSV files
- those files can finish a few minutes apart
- using the earliest timestamp gives one stable ID for the whole run

## Typical Workflow

For normal work:

1. make code changes
2. run `RunBenchmark.sh` or `RunBenchmark.cmd`
3. let the launcher import the new results into `benchmark.db`
4. compare the newest `run_id` to an older baseline run

For one-off imports:

```bash
python3 src/PdfiumWrapper.Benchmarks/ImportBenchmarkResults.py \
  --db benchmark.db \
  --run-label manual \
  src/PdfiumWrapper.Benchmarks/BenchmarkDotNet.Artifacts/results
```

## Useful Queries

List all runs:

```sql
SELECT run_id, run_label, source_dir, imported_at
FROM runs
ORDER BY run_id;
```

Get the latest and previous run IDs:

```sql
SELECT run_id, run_label, imported_at
FROM runs
ORDER BY run_id DESC
LIMIT 2;
```

Show all results for one run:

```sql
SELECT run_id, benchmark_name, method, document, mean_ms, error_ms, stddev_ms
FROM benchmark_results
WHERE run_id = '202603291618'
ORDER BY benchmark_name, document;
```

Compare latest vs previous automatically:

```sql
WITH ranked_runs AS (
    SELECT
        run_id,
        ROW_NUMBER() OVER (ORDER BY run_id DESC) AS rn
    FROM runs
),
latest_vs_previous AS (
    SELECT
        MAX(CASE WHEN rn = 1 THEN run_id END) AS latest_run_id,
        MAX(CASE WHEN rn = 2 THEN run_id END) AS previous_run_id
    FROM ranked_runs
)
SELECT
    ids.previous_run_id,
    ids.latest_run_id,
    cur.benchmark_name,
    cur.method,
    cur.document,
    base.mean_ms AS previous_ms,
    cur.mean_ms AS latest_ms,
    cur.mean_ms - base.mean_ms AS delta_ms,
    ROUND(((cur.mean_ms - base.mean_ms) / base.mean_ms) * 100.0, 2) AS delta_pct
FROM latest_vs_previous ids
JOIN benchmark_results base
    ON base.run_id = ids.previous_run_id
JOIN benchmark_results cur
    ON cur.run_id = ids.latest_run_id
   AND cur.benchmark_name = base.benchmark_name
   AND cur.method = base.method
   AND cur.document = base.document
ORDER BY cur.benchmark_name, cur.document;
```

Compare latest vs previous and show only regressions:

```sql
WITH ranked_runs AS (
    SELECT
        run_id,
        ROW_NUMBER() OVER (ORDER BY run_id DESC) AS rn
    FROM runs
),
latest_vs_previous AS (
    SELECT
        MAX(CASE WHEN rn = 1 THEN run_id END) AS latest_run_id,
        MAX(CASE WHEN rn = 2 THEN run_id END) AS previous_run_id
    FROM ranked_runs
)
SELECT
    ids.previous_run_id,
    ids.latest_run_id,
    cur.benchmark_name,
    cur.document,
    base.mean_ms AS previous_ms,
    cur.mean_ms AS latest_ms,
    cur.mean_ms - base.mean_ms AS delta_ms,
    ROUND(((cur.mean_ms - base.mean_ms) / base.mean_ms) * 100.0, 2) AS delta_pct
FROM latest_vs_previous ids
JOIN benchmark_results base
    ON base.run_id = ids.previous_run_id
JOIN benchmark_results cur
    ON cur.run_id = ids.latest_run_id
   AND cur.benchmark_name = base.benchmark_name
   AND cur.method = base.method
   AND cur.document = base.document
WHERE cur.mean_ms > base.mean_ms
ORDER BY delta_pct DESC, cur.benchmark_name, cur.document;
```

Summarize latest vs previous by benchmark family:

```sql
WITH ranked_runs AS (
    SELECT
        run_id,
        ROW_NUMBER() OVER (ORDER BY run_id DESC) AS rn
    FROM runs
),
latest_vs_previous AS (
    SELECT
        MAX(CASE WHEN rn = 1 THEN run_id END) AS latest_run_id,
        MAX(CASE WHEN rn = 2 THEN run_id END) AS previous_run_id
    FROM ranked_runs
),
paired AS (
    SELECT
        cur.benchmark_name,
        cur.document,
        base.mean_ms AS previous_ms,
        cur.mean_ms AS latest_ms
    FROM latest_vs_previous ids
    JOIN benchmark_results base
        ON base.run_id = ids.previous_run_id
    JOIN benchmark_results cur
        ON cur.run_id = ids.latest_run_id
       AND cur.benchmark_name = base.benchmark_name
       AND cur.method = base.method
       AND cur.document = base.document
)
SELECT
    benchmark_name,
    COUNT(*) AS cases,
    ROUND(AVG(latest_ms - previous_ms), 4) AS avg_delta_ms,
    ROUND(AVG(((latest_ms - previous_ms) / previous_ms) * 100.0), 2) AS avg_delta_pct
FROM paired
GROUP BY benchmark_name
ORDER BY benchmark_name;
```

Compare two runs directly:

```sql
SELECT
    cur.benchmark_name,
    cur.method,
    cur.document,
    base.mean_ms AS base_ms,
    cur.mean_ms AS current_ms,
    cur.mean_ms - base.mean_ms AS delta_ms,
    ROUND(((cur.mean_ms - base.mean_ms) / base.mean_ms) * 100.0, 2) AS delta_pct
FROM benchmark_results base
JOIN benchmark_results cur
    ON cur.benchmark_name = base.benchmark_name
   AND cur.method = base.method
   AND cur.document = base.document
WHERE base.run_id = '202603291605'
  AND cur.run_id = '202603291618'
ORDER BY cur.benchmark_name, cur.document;
```

Find only likely large regressions:

```sql
SELECT
    cur.benchmark_name,
    cur.document,
    base.mean_ms AS base_ms,
    cur.mean_ms AS current_ms,
    ROUND(((cur.mean_ms - base.mean_ms) / base.mean_ms) * 100.0, 2) AS delta_pct
FROM benchmark_results base
JOIN benchmark_results cur
    ON cur.benchmark_name = base.benchmark_name
   AND cur.method = base.method
   AND cur.document = base.document
WHERE base.run_id = '202603291605'
  AND cur.run_id = '202603291618'
  AND cur.mean_ms > base.mean_ms
  AND ((cur.mean_ms - base.mean_ms) / base.mean_ms) >= 0.02
ORDER BY delta_pct DESC;
```

## Interpreting Results

A slower mean is not always a real regression.

When comparing runs, look at:

- `mean_ms`
- `error_ms`
- `stddev_ms`

If the delta between runs is smaller than the combined uncertainty of the two runs, it may just be normal benchmark noise.

In practice:

- tiny JPEG deltas are often noise
- larger regressions on merge or large-document PNG/TIFF runs are more likely to be meaningful

## Notes And Caveats

- BenchmarkDotNet may fail to raise process priority in some environments. That can increase run-to-run noise.
- Existing legacy CSV files may still exist in `src/PdfiumWrapper.Benchmarks/` from older runs before the launcher changed.
- `benchmark.db` is the source of truth for historical comparison going forward.

## Files To Remember

- `benchmark.md`
- `benchmark.db`
- `src/PdfiumWrapper.Benchmarks/Program.cs`
- `src/PdfiumWrapper.Benchmarks/RunBenchmark.sh`
- `src/PdfiumWrapper.Benchmarks/RunBenchmark.cmd`
- `src/PdfiumWrapper.Benchmarks/ImportBenchmarkResults.py`
