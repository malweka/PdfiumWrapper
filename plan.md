# PdfMerger Performance Plan

This plan focuses on improving `PdfMerger` performance without changing the benchmark shape in `src/PdfiumWrapper.Benchmarks/PdfMergeBenchmark.cs`.

The current merge benchmark measures the full end-to-end path:

1. Load the first source PDF into `PdfMerger`
2. Load the second source PDF into `PdfDocument`
3. Import all pages from the second document
4. Save the merged result to disk

That means the fastest path is not just "make `FPDF_ImportPages()` faster". We need to reduce overhead around document loading, page import orchestration, and final save, while preserving current API behavior.

## Goals

- Improve end-to-end merge throughput in the existing benchmark.
- Avoid API churn unless there is a clear performance benefit.
- Keep correctness and ownership rules explicit.
- Prefer changes that help real workloads, not only synthetic cases.

## Non-Goals

- Do not modify the benchmark structure or split it into separate measurements.
- Do not add speculative low-level changes unless profiling points to them.
- Do not parallelize PDFium calls inside a single merge operation.

## Current Observations

### 1. The benchmark includes open + import + save

`PdfMergeBenchmark.MergeTwoDocuments()` currently does:

- `new PdfMerger(GetDocPath())`
- `new PdfDocument(GetDocPath())`
- `merger.AppendDocument(second)`
- `merger.Save(outputPath)`

So any meaningful optimization has to consider:

- source document construction cost
- destination document construction cost
- `FPDF_ImportPages()` / `FPDF_ImportPagesByIndex()` cost
- `FPDF_SaveAsCopy()` cost

### 2. Append path is already thin

`AppendDocument(PdfDocument)` delegates directly to `AppendPages(sourceDoc, null)`, and that goes straight to `FPDF_ImportPages()`.

There is very little managed overhead here today. This suggests the biggest wins are more likely to come from:

- avoiding unnecessary document copies or allocations around load
- removing avoidable work in save
- avoiding patterns that force more PDFium work than necessary

### 3. Stream construction still copies non-memory streams

`PdfMerger(Stream)` currently falls back to:

- `ReadStreamToBytes()`
- pin managed array
- `FPDF_LoadMemDocument()`

That is simple and safe, but expensive for large stream-based inputs.

### 4. Save path is already on the span-based callback pattern

`PdfMerger.Save(Stream)` already writes from the unmanaged buffer directly into the target stream through `ReadOnlySpan<byte>`.

That means the old per-callback managed buffer allocation issue is likely not the main merge bottleneck anymore.

## Phase 1: Measure The Merge Path Internally

### Problem

The benchmark tells us the total time, but not which internal stage dominates for each document.

### Proposed Changes

Add lightweight internal timing during local development only while implementing the optimization work:

- time `PdfMerger(string filePath, ...)`
- time `PdfDocument(string filePath, ...)`
- time `AppendDocument(PdfDocument)`
- time `Save(string)` / `Save(Stream)`

This should not become public API and should not change benchmark code. The goal is only to guide implementation decisions while working.

### Reasoning

Without this, we risk spending time on managed wrapper code that is not on the critical path.

### Deliverable

- short before/after notes captured during implementation
- no permanent benchmark changes required

## Phase 2: Add A Custom-Loader Path For `PdfMerger(Stream)`

### Problem

`PdfMerger(Stream)` currently copies the entire remaining stream into managed memory unless the stream is a buffer-exposing `MemoryStream`.

That adds:

- one full managed allocation
- one full copy
- a pinned array for the merger lifetime

### Proposed Changes

Implement a stream-backed load path for `PdfMerger`, mirroring the ownership model planned for `PdfDocument`:

- add a private loader object owned by `PdfMerger`
- use `FPDF_LoadCustomDocument`
- hold the source stream and callback state for the full lifetime of the merger
- require a readable, seekable stream
- keep the current memory-backed path as fallback when necessary

### Reasoning

This will not change the current file-path benchmark directly, but it improves merge throughput for real stream-based callers and removes a known whole-file copy.

It also gives `PdfMerger` and `PdfDocument` the same ownership model, which reduces maintenance complexity.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfMerger.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PDFium.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfHelpers.cs`

### Tests

- load merger from stream, force GC, append pages, save successfully
- repeated stream-backed merge loop to catch handle leaks

## Phase 3: Remove Small Managed Overheads In Hot Merge Paths

### Problem

The managed wrapper around page import is already thin, but there are still a few things to verify and tighten:

- repeated `PageCount` calls when appending to the end
- avoidable validation or indirection on common append paths
- duplicated save-writer implementation between `PdfDocument` and `PdfMerger`

### Proposed Changes

#### 1. Avoid redundant destination `PageCount` lookups

For append operations, compute the insertion index once per call and pass it through.

This is a small win, but it is cheap and safe.

#### 2. Consolidate save writer implementation

`PdfMerger` and `PdfDocument` both maintain a private `StreamFileWriter`.

Move to one shared internal implementation so:

- save-path behavior stays identical
- performance fixes land once
- allocation behavior stays aligned between document save and merger save

#### 3. Review `string` page range import usage

For "append whole document" and "append explicit indices", keep using the least expensive import shape:

- `FPDF_ImportPages(..., null, ...)` for all pages
- `FPDF_ImportPagesByIndex(...)` for selected pages

Avoid any conversions that build page-range strings for index-based paths.

### Reasoning

These are modest improvements, but they are low-risk and directly on the merge path.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfMerger.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfHelpers.cs`

### Tests

- append full document preserves page count and ordering
- append selected pages by index preserves page order
- save to file and save to stream produce valid output

## Phase 4: Improve File-Path Construction Cost If Profiling Shows It Matters

### Problem

The benchmark uses file-path constructors for both source documents. If internal timing shows open cost is a significant part of the total merge time, improving construction may help the benchmark more than import-path work.

### Proposed Changes

If the numbers justify it:

- review whether file-path constructors can share more code with optimized load helpers
- ensure there is no avoidable extra work during `PdfMerger(string)` and `PdfDocument(string)`
- verify that file-backed load is not doing any unnecessary managed setup compared to memory-backed load

### Reasoning

This phase should only happen if measurement shows construction is materially contributing to total runtime.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfMerger.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`

### Tests

- file-backed merge correctness on all benchmark documents
- repeated open/append/save loop to catch leaks or lifetime regressions

## Phase 5: Validate Save Flags And Output Shape

### Problem

For merged output, save behavior can dominate end-to-end time, especially on larger documents.

### Proposed Changes

Review whether the current default save mode is the best choice for merged documents:

- validate current `FPDF_SaveAsCopy(..., flags: 0)` behavior
- test whether alternative PDFium save flags improve throughput without changing expected output semantics
- only adopt a different default if the output contract remains acceptable

### Reasoning

This is potentially high leverage, but it is also the easiest place to create behavior changes. It should come after safer improvements.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfMerger.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PDFium.Ppo.cs`

### Tests

- merged output opens correctly in `PdfDocument`
- page count, content, and metadata remain valid
- encrypted-source behavior remains unchanged

## Success Criteria

Primary success criteria:

- measurable end-to-end improvement in the existing `PdfMergeBenchmark`
- no regressions in merge correctness
- no new lifetime or ownership bugs

Secondary success criteria:

- lower allocation pressure in stream-based merge workloads
- less duplicated save-path code between `PdfDocument` and `PdfMerger`

## Recommended Implementation Order

1. Internal timing to identify whether open, import, or save dominates.
2. Low-risk hot-path cleanup in `PdfMerger` and save writer consolidation.
3. Stream-backed custom-loader support for `PdfMerger(Stream)`.
4. File-path constructor optimization only if timing says it matters.
5. Save-flag experiments only if earlier changes are insufficient.

## Risks

- `FPDF_LoadCustomDocument` introduces more ownership complexity than the current copy-to-memory approach.
- Save-flag changes can alter output semantics or compatibility.
- Small mean shifts in the benchmark may still be run-to-run noise, especially on larger documents.

## Out Of Scope For This Plan

- redesigning the public merge API
- adding benchmark variants
- speculative native changes inside PDFium itself
