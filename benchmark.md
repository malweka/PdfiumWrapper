# Benchmarking Guide

This document explains how benchmarking works in this repo, what gets stored in `benchmark.db`, and how to query it later when comparing runs.

## Overview

Benchmarks live in `src/PdfiumWrapper.Benchmarks`.

They use BenchmarkDotNet to run four end-to-end benchmark groups:

- `PdfToJpegBenchmark`
- `PdfToPngBenchmark`
- `PdfToTiffBenchmark`
- `PdfMergeBenchmark`

and four groups added with the native gate (see [Concurrency Benchmarks](#concurrency-benchmarks-native-gate-20)):

- `SmallDocumentBenchmark`
- `GateOverheadBenchmark`
- `ConcurrentCallersBenchmark`
- `StreamCallbackBenchmark`

The same executable also hosts the burst runner (`-- burst ...`) and the stream hold-time report (`-- streams`).

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

## Concurrency Benchmarks (native gate, 2.0)

Recorded 2026-10-02 for `ai/plans/plan-pdfium-concurrency.md`. The pre-gate code is the tag `bench-baseline-pre-gate`; "gated" is the 2.0 code that serializes all PDFium use behind `PdfiumRuntime`.

### Machine and binaries

| | |
|---|---|
| OS / RID | Windows 11 Pro 10.0.26300, `win-x64` |
| CPU | Intel Core i7-13700F (8 performance + 8 efficiency cores, 24 logical processors) |
| RAM | 31.7 GB |
| Storage | NVMe SSD (Samsung MZVL21T0HCLR) |
| .NET | SDK 10.0.401, runtime 8.0.31 |
| PDFium | 150.0.7869.0, V8 and XFA disabled |

Native library SHA-256:

| File | SHA-256 |
|---|---|
| `win-x64/pdfium.dll` | `E472F04B57181946CA1E52FA34086079599D9B733164E7AB05179B03F4D3DDCC` |
| `win-x64/pdfium_png.dll` | `5C6F0AFC3C8EF12AFA8AE500283BE72EC2F5B7902EB09C90BF08D520595964EC` |
| `win-x64/tiff.dll` | `98449CE40369EA714E7DFA81E5E60C853F576ECEEB4F59813CE426E5F14B1CB0` |
| `win-x64/tiff_shim.dll` | `F6ECAAEF094E5C98A01A85F491D254D13A9A29BB8C5CD24FD5480B62F4890F83` |
| `win-x64/turbojpeg.dll` | `3566778A142C7A0BA2D463AE91F3CF9D8DADDEF486EC606ABE97689A831ECBA0` |
| `linux-x64/libpdfium.so` | `3F3AF4E4BA46BEC9D0D11C5635262663B7020345FF4BED7EEE85E883E2CE8455` |
| `linux-x64/libpdfium_png.so` | `28D51D11CDFE9B112665650ECC394F26EFF27D87242B7A9F7780825DB0ABDAA1` |
| `linux-x64/libtiff.so` | `BB0C33C48258D9FEBD7D55B25F89E254AF860C984378163CBE8C0F2DF3D05A95` |
| `linux-x64/libtiff_shim.so` | `F42A08D616A96EB4C28F9E9CCD5C4F8B7F33D39D9E4B2AF3AA4BA1541E6B7CFB` |
| `linux-x64/libturbojpeg.so` | `75E641AB1B0EB33C8A23ADC92FE7A695C2250A8A86743C4FE4A1684C65927F38` |

All numbers below are from this one machine. They size nothing else; rerun on the deployment hardware with the deployment documents.

### How to run

```text
# BenchmarkDotNet classes (all, or a subset)
dotnet run -c Release --project src/PdfiumWrapper.Benchmarks
dotnet run -c Release --project src/PdfiumWrapper.Benchmarks -- --only GateOverheadBenchmark,ConcurrentCallersBenchmark

# Whole-batch runner: N jobs enqueued at once, measured to the last completion
dotnet run -c Release --project src/PdfiumWrapper.Benchmarks -- burst \
  --n 1000 --t 60 --callers 4 --mix tiff:50,png:30,jpeg:20 --dpi 200 \
  --input src/PdfiumWrapper.Tests/Docs --out TestOutput/burst --report burst.json \
  [--mode sync|async|async-starved] [--prewarm true|false] [--abandon-fraction 0.05] [--diagnostics true]

# Gate hold time per stream type (instrumented)
dotnet run -c Release --project src/PdfiumWrapper.Benchmarks -- streams
```

Burst runner notes:

- `--out` is a parent directory. Each run writes into a new `burst-<timestamp>-<id>` child and removes only that child afterwards (`--keep-output true` keeps it and reports its path as `outputDirectory`). Files already in `--out` are never touched.
- `--mix` weights are honored exactly over every block of total-weight jobs; the report lists `mixTotalWeight`, `mixStride` and the resulting `jobsByFormat`. The weights may sum to at most 10,000: use ratios (`png:1,jpeg:1`), not large counts. A larger total is rejected as an argument error, because a batch much shorter than the total would not reflect the mix.
- The mix is walked with a stride coprime with the total and close to 0.37 of it, so a short run still spreads across the formats. An earlier revision used a fixed stride of 37, which collapsed to a single format when the weights summed to a multiple of 37. The runs recorded below used `tiff:50,png:30,jpeg:20` (total 100), for which the stride is still 37 and the job sequence is unchanged.

Run the pre-gate code from a worktree of the tag. Put the worktree on the same volume and under the same kind of directory as the repository: on this machine a worktree under `%TEMP%` made every file open about 60 µs slower, which doubled the one-page load time and would have been read as a gate result.

### Gate overhead (`GateOverheadBenchmark` against `SmallDocumentBenchmark` at the tag)

Three alternating rounds, pre-gate then gated. Acceptance: median within 5%, P95 within 10%.

| Operation | Round | Pre-gate median | Gated median | Pre-gate P95 | Gated P95 |
|---|---|---|---|---|---|
| `LoadCountClose` | 1 | 55.8 µs | 54.4 µs | 56.1 µs | 54.9 µs |
| | 2 | 53.6 µs | 54.1 µs | 54.2 µs | 54.4 µs |
| | 3 | 53.9 µs | 55.3 µs | 54.0 µs | 56.0 µs |
| `LoadRender72Close` | 1 | 3.542 ms | 3.538 ms | 3.600 ms | 3.574 ms |
| | 2 | 3.530 ms | 3.548 ms | 3.546 ms | 3.585 ms |
| | 3 | 3.517 ms | 3.555 ms | 3.549 ms | 3.577 ms |

| Operation | Median change (mean of rounds) | P95 change | Result |
|---|---|---|---|
| `LoadCountClose` | +0.3% | +0.6% | within limits |
| `LoadRender72Close` | +0.5% | +0.4% | within limits |

`EnterExit` (one uncontended `PdfiumRuntime.Enter()` and its exit on an initialized runtime): 28.1 ns mean, 0 bytes allocated. Managed allocation per document operation rose by 24 bytes (112 to 136 for `LoadCountClose`).

### Existing suite, pre-gate against gated

Mean in ms. Both trees in the same location.

| Benchmark | Document | Pre-gate | Gated | Change |
|---|---|---|---|---|
| `ConvertToJpeg` | doc-1-page (1p) | 34.03 | 34.04 | 0.0% |
| | doc-3-pages (3p) | 91.55 | 91.16 | -0.4% |
| | contract (10p) | 287.03 | 288.24 | +0.4% |
| | fw2 (11p) | 299.39 | 302.31 | +1.0% |
| | presentation (30p) | 4924.82 | 4918.47 | -0.1% |
| `ConvertToPng` | doc-1-page (1p) | 84.13 | 84.52 | +0.5% |
| | doc-3-pages (3p) | 247.95 | 247.75 | -0.1% |
| | contract (10p) | 830.07 | 830.14 | 0.0% |
| | fw2 (11p) | 718.82 | 713.19 | -0.8% |
| | presentation (30p) | 6348.75 | 6347.41 | 0.0% |
| `ConvertToTiff` | doc-1-page (1p) | 18.86 | 18.66 | -1.1% |
| | doc-3-pages (3p) | 51.51 | 51.00 | -1.0% |
| | contract (10p) | 158.17 | 158.04 | -0.1% |
| | fw2 (11p) | 175.05 | 175.31 | +0.1% |
| | presentation (30p) | 3038.32 | 3044.62 | +0.2% |
| `MergeTwoDocuments` | doc-1-page (1p) | 1.338 | 1.259 | -5.9% |
| | doc-3-pages (3p) | 2.759 | 2.750 | -0.3% |
| | contract (10p) | 2.683 | 2.802 | +4.4% |
| | fw2 (11p) | 53.724 | 54.213 | +0.9% |
| | presentation (30p) | 15.491 | 16.121 | +4.1% |

Conversion is unchanged within 1.1%. Merge moves between -5.9% and +4.4%: the save now serializes into a pooled buffer and writes the file afterwards instead of writing from inside PDFium's callback.

### Cold start (`cold-start` host scenario)

Milliseconds from process start to the first completed 72 DPI render, three runs each.

| First native use | Pre-gate | Gated |
|---|---|---|
| `PdfDocument` | 72.1, 70.0, 69.2 | 80.0, 75.5, 75.0 |
| `PdfMerger` | process aborts (0xC0000005), 3 of 3 | 79.1, 76.3, 76.0 |
| TIFF save | 82.3, 80.3, 79.4 | 77.8, 84.2, 82.9 |

A document-first start is about 5 ms slower: initialization now also loads libtiff and installs its error handlers. A merger-first start crashed before the gate because nothing had initialized PDFium.

### In-process scaling (`ConcurrentCallersBenchmark`)

Each caller converts the whole corpus (55 pages) with its own documents. TIFF at 200 DPI bilevel, PNG and JPEG at 150 DPI. pages/sec = callers x 55 / mean seconds.

| Format | Callers | Mean (s) | Pages/sec | Speedup over 1 caller |
|---|---|---|---|---|
| TIFF | 1 | 3.436 | 16.0 | 1.00 |
| | 2 | 6.186 | 17.8 | 1.11 |
| | 4 | 12.397 | 17.7 | 1.11 |
| | 8 | 24.669 | 17.8 | 1.11 |
| PNG | 1 | 3.276 | 16.8 | 1.00 |
| | 2 | 4.857 | 22.6 | 1.35 |
| | 4 | 9.267 | 23.7 | 1.41 |
| | 8 | 18.130 | 24.3 | 1.45 |
| JPEG | 1 | 2.453 | 22.4 | 1.00 |
| | 2 | 4.524 | 24.3 | 1.08 |
| | 4 | 9.107 | 24.2 | 1.08 |
| | 8 | 18.086 | 24.3 | 1.09 |

Rendering is the serialized part and it dominates. A second caller recovers the encode share (large for PNG, small for JPEG and bilevel TIFF); callers beyond two add nothing.

### Burst runner, one process

200 jobs enqueued at once (2,480 pages), mix `tiff:50,png:30,jpeg:20`, 200 DPI, output written to disk, prewarmed.

| Code | Mode | Callers | Total (s) | Docs/sec | Pages/sec | Peak working set (MB) |
|---|---|---|---|---|---|---|
| pre-gate | sync | 1 | 153.47 | 1.303 | 16.16 | 124 |
| gated | sync | 1 | 154.29 | 1.296 | 16.07 | 124 |
| gated | sync | 2 | 126.60 | 1.580 | 19.59 | 145 |
| gated | sync | 4 | 123.87 | 1.615 | 20.02 | 180 |
| gated | sync | 8 | 123.81 | 1.615 | 20.03 | 253 |
| gated | sync | 12 | 123.39 | 1.621 | 20.10 | 327 |
| gated | sync | 16 | 123.38 | 1.621 | 20.10 | 387 |
| gated | sync | 24 | 123.52 | 1.619 | 20.08 | 493 |
| gated | async | 8 | 123.46 | 1.620 | 20.09 | 249 |
| gated | async | 16 | 123.43 | 1.620 | 20.09 | 348 |
| gated | async | 32 | 123.58 | 1.618 | 20.07 | 561 |

- Instrumented run at 8 callers: the gate was held for 99.8% of wall time and each caller spent 80.7% of its time waiting for it. The process used about 155 CPU-seconds in every run, 1.26 cores at the saturated rate.
- In-process capacity on this mix is 1.62 docs/sec (20.1 pages/sec), 1.25 times the sequential rate, reached at 4 callers. More callers cost memory and gain nothing.

### Async admission and thread-pool starvation

Burst runner, thread pool pinned to 24 threads (`async-starved`), heartbeat = wait of a trivial work item for a pool thread:

| Callers | Total (s) | Docs/sec | Heartbeat p99 (ms) |
|---|---|---|---|
| 8 | 123.50 | 1.619 | 0.26 |
| 16 | 123.42 | 1.621 | 0.27 |
| 32 | 123.54 | 1.619 | 0.25 |

Host scenario `starvation`: 192 concurrent `SaveAsTiff` conversions of a 3-page document, pool pinned to 24 threads.

| API used from pool threads | Completed | Total (ms) | Heartbeat p50 (ms) | Heartbeat p99 (ms) |
|---|---|---|---|---|
| `SaveAsTiffAsync` | 192 | 2,697 | 0.08 | 1.34 |
| `SaveAsTiff` (sync) | 192 | 2,710 | 0.06 | 2,513 |

Throughput is the same. The synchronous API parks every pool thread on the gate, so unrelated work waits seconds for a thread. The test bound for the async heartbeat p99 is 100 ms.

### Stream type and gate hold time (`StreamCallbackBenchmark`, `streams` report)

Wall time per operation (BenchmarkDotNet mean) and gate hold time per operation (instrumented, 100 iterations). The throttled stream sleeps 5 ms per read or write.

| Operation | Wall (ms) | Gate hold (ms) |
|---|---|---|
| Save to file | 0.587 | 0.461 |
| Save to `MemoryStream` | 0.345 | 0.324 |
| Save to throttled stream | 16.06 | 1.094 |
| Control: save to `MemoryStream` after 15 ms idle | | 1.292 |
| Merger from `FileStream` | 0.126 | 0.099 |
| Merger from `MemoryStream` | 0.100 | 0.100 |
| Merger from throttled stream | 64.52 | 0.476 |
| Control: merger from `MemoryStream` after 60 ms idle | | 0.516 |

The throttled stream's sleeps (16 ms and 64 ms of wall time) are not inside the gate. Its hold time is higher than the fast streams' only because native work runs slower on a thread that has just been asleep: the controls, which sleep outside any PDF call and then use a `MemoryStream`, show the same hold time. Hold time is independent of the stream type.

### Deferred release (finalizer drain)

Burst runner, 4 callers, 5% of jobs also open a document and a page and drop them without disposing.

| Abandon fraction | Total (s) | Docs/sec |
|---|---|---|
| 0 | 123.87 | 1.615 |
| 0.05 | 123.56 | 1.619 |

No measurable cost. Instrumented: 0.0035 deferred releases per gate acquisition.

### Replica scale-out (several processes)

P burst-runner processes started together, each with its own PDFium and gate, each running 60 jobs (744 pages) with 2 callers. Rate = all documents / wall time from launch to the last process exit.

| Processes | Wall (s) | Docs/sec | Pages/sec | Speedup over 1 process | Sum of peak working sets (MB) |
|---|---|---|---|---|---|
| 1 | 38.07 | 1.58 | 19.6 | 1.00 | 142 |
| 2 | 38.32 | 3.13 | 38.8 | 1.99 | 285 |
| 4 | 41.75 | 5.75 | 71.3 | 3.65 | 567 |
| 8 | 53.03 | 9.05 | 112.3 | 5.74 | 1,138 |
| 12 | 69.81 | 10.31 | 127.9 | 6.54 | 1,704 |
| 16 | 86.76 | 11.07 | 137.2 | 7.02 | 2,273 |

Processes scale where threads in one process do not: near-linear up to the 8 performance cores, then flattening. About 142 MB per process on this mix.

### Decision gate for the process pool (plan rule R13)

- Measured in-process capacity, mixed corpus at 200 DPI: `max R_inproc = 1.62 docs/sec` (20.1 pages/sec). With the plan's 25% headroom the usable rate is `0.8 x 1.62 = 1.30 docs/sec` per process.
- The rule builds the pool only if `1.30 docs/sec < N / T` **and** the consumer cannot add replicas, or the consumer requires isolation from native aborts.
- `N` and `T` were not supplied. Whether the consumer can run replicas was not supplied. The crash probe found no process-fatal input (25 damaged files, win-x64 and linux-x64), so there is no measured case for abort isolation.
- Outcome: the pool was not built. For any burst above about 1.3 docs/sec of this mix, one process is not enough; more processes are. Eight replicas gave 9.05 docs/sec on this machine. If the consumer cannot run replicas behind its queue, the pool (plan Phases 5 to 7) is the remaining route and the decision should be reopened with `N`, `T` and that constraint.

## Files To Remember

- `benchmark.md`
- `benchmark.db`
- `src/PdfiumWrapper.Benchmarks/Program.cs`
- `src/PdfiumWrapper.Benchmarks/RunBenchmark.sh`
- `src/PdfiumWrapper.Benchmarks/RunBenchmark.cmd`
- `src/PdfiumWrapper.Benchmarks/ImportBenchmarkResults.py`
- `src/PdfiumWrapper.Benchmarks/BurstRunner.cs`
- `src/PdfiumWrapper.Tests.Host` (cold-start and starvation scenarios)
