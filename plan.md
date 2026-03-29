# PdfiumWrapper Lifetime And Memory Plan

This plan covers the first five issues from the `PdfDocument` and `PdfPage` review:

1. Memory-backed document ownership
2. `PdfDocument` / `PdfPage` lifetime coordination
3. Consistent disposed-state enforcement
4. Save callback allocation churn
5. Repeated unmanaged allocation churn in text and metadata paths

## Goals

- Make native ownership explicit and deterministic.
- Prevent use-after-free when documents are loaded from memory.
- Reduce allocation pressure in high-throughput workloads.
- Keep the public API predictable for callers using `using` and per-page processing loops.

## Phase 1: Fix Memory-Backed Document Ownership

### Problem

`FPDF_LoadMemDocument()` requires the backing buffer to remain valid for the full lifetime of the PDF document. The current implementation pins the array only during the load call, then frees the pin immediately.

This is the highest-priority correctness issue because it can cause invalid memory access after construction.

### Proposed Changes

#### 1. Keep the backing buffer alive for the entire `PdfDocument` lifetime

Update `PdfDocument` to store:

- A managed `byte[]? _documentBytes`
- A `GCHandle _documentBytesHandle`
- A flag indicating whether the document was loaded from memory

Construction flow:

- `PdfDocument(byte[] data, string? password = null)`
  - store `data` in `_documentBytes`
  - pin once into `_documentBytesHandle`
  - call `FPDF_LoadMemDocument()`
  - free the handle only in `Dispose(bool)`

Dispose flow:

- `FPDF_CloseDocument(Document)` first
- then free `_documentBytesHandle`
- then clear `_documentBytes`

Reasoning:

- This matches PDFium’s ownership contract.
- It avoids any transient pinned-object bug.
- It does not change the public API.

#### 2. Stop forcing stream input through a temporary full-copy path by default

Current flow:

- `Stream -> byte[] -> FPDF_LoadMemDocument`

Preferred flow:

- Add a custom-loader path using `FPDF_LoadCustomDocument`
- Keep the source stream alive for the `PdfDocument` lifetime
- Require a readable, seekable stream for this constructor

Implementation shape:

- Add a private loader object owned by `PdfDocument`
- Store:
  - the source `Stream`
  - `FPDF_FILEACCESS`
  - pinned callback delegate / GCHandle
- Use `m_GetBlock` to read requested ranges from the stream

Fallback:

- If you want to keep constructor behavior permissive, add:
  - `PdfDocument(Stream pdfStream, string? password = null, bool copyToMemory = false)`
- When `copyToMemory` is `true`, use the pinned-byte-array path.
- When `false`, prefer `FPDF_LoadCustomDocument`.

Reasoning:

- This removes an unconditional whole-file allocation for stream callers.
- It is the right model for high-throughput services processing large PDFs.
- It makes ownership explicit: the `PdfDocument` owns the stream loader until disposal.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PDFium.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfHelpers.cs`

### Tests

- Load from `byte[]`, force GC, then access page count and render a page.
- Load from stream, force GC, then extract text and save.
- Repeat in a loop to catch handle leaks.

## Phase 2: Coordinate `PdfDocument` And `PdfPage` Lifetimes

### Problem

`PdfPage` currently wraps a raw page handle with no ownership tie back to `PdfDocument`. If the document is disposed while pages are still alive, page disposal or later page access can touch invalid native state.

### Proposed Changes

#### 1. Track active pages inside `PdfDocument`

Add to `PdfDocument`:

- `int _activePageCount`
- `object _lifetimeLock`

On `GetPage()`:

- call `ThrowIfDisposed()`
- increment `_activePageCount`
- create `PdfPage` with an owner callback or owner reference

On page disposal:

- decrement `_activePageCount` exactly once

#### 2. Decide the disposal policy explicitly

Recommended policy:

- `PdfDocument.Dispose()` should throw `InvalidOperationException` if pages are still open

Reasoning:

- Failing fast is safer than silently closing the document underneath active pages.
- In a throughput service, this makes leak bugs obvious in testing instead of becoming intermittent native crashes.

Alternative policy:

- Permit document disposal and make pages no-op once owner is disposed.

I do not recommend this because it hides lifetime bugs and still leaves native ownership ambiguous.

#### 3. Make `PdfPage` owner-aware

Change `PdfPage` constructor to receive:

- the owning `PdfDocument`
- the page handle
- a release callback or a direct owner reference

`PdfPage.Dispose()` should:

- close the page handle once
- notify the owner that the page is released

`PdfPage` public methods should:

- call a `ThrowIfDisposed()`
- optionally ask the owner to verify the document is still valid

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfPage.cs`

### Tests

- Opening multiple pages increments tracking and disposing them decrements it.
- Disposing a document with an undisposed page throws.
- After page disposal, document disposal succeeds.
- Finalizer and duplicate-dispose paths do not double-decrement.

## Phase 3: Enforce Disposed State Consistently

### Problem

Some methods check `_disposed`; many do not. That makes failure modes inconsistent and can push bugs into native code instead of failing at the managed boundary.

### Proposed Changes

#### 1. Add `ThrowIfDisposed()` helpers

Add a private helper to `PdfDocument`:

- `private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);`

Add a similar helper to `PdfPage`.

#### 2. Call it from every public and internal entry point

`PdfDocument`:

- all properties that hit PDFium
- `GetPage()`
- page enumeration / rendering methods
- save methods
- metadata / bookmarks / attachments accessors

`PdfPage`:

- `Width`, `Height`
- render helpers
- text extraction
- thumbnail methods
- editing methods
- object-count and object lookup methods

#### 3. Normalize argument and state validation order

Recommended order:

1. disposed check
2. argument validation
3. native call

This keeps failure modes consistent across the API surface.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfPage.cs`

### Tests

- Access every major method after disposal and assert `ObjectDisposedException`.
- Verify page methods throw after page disposal.

## Phase 4: Remove Per-Callback Save Allocations

### Problem

`SaveToStream()` currently allocates a new managed `byte[]` on every PDFium `WriteBlock` callback, copies into it, then writes it to the target stream.

This adds GC pressure during large saves and batch export.

### Proposed Changes

#### 1. Introduce a reusable callback state object

Create a private nested class in `PdfDocument`, for example:

- `StreamWriteContext`

State:

- target `Stream`
- optional rented buffer from `ArrayPool<byte>`

#### 2. Prefer direct unmanaged-span writes on .NET 8

Inside the callback:

- create `ReadOnlySpan<byte>` over `dataPtr` and `size`
- write directly to the stream

Example direction:

- `var span = new ReadOnlySpan<byte>(dataPtr.ToPointer(), checked((int)size));`
- `targetStream.Write(span);`

This removes the intermediate allocation entirely.

If you prefer not to use unsafe code in the callback:

- rent from `ArrayPool<byte>.Shared`
- reuse one buffer across callbacks
- grow only when the callback size exceeds capacity

#### 3. Preserve delegate lifetime handling

Keep the existing logic that pins:

- the stream/context handle
- the callback delegate

But move the state into a dedicated context object so ownership is cleaner.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`

### Tests

- Save to `MemoryStream` and file stream.
- Save repeatedly in a loop to ensure no callback lifetime issues.
- If you add allocation-focused benchmarks, compare before/after allocation counts.

## Phase 5: Reduce Allocation Churn In Text And Metadata Paths

### Problem

`ExtractText()`, `DocumentId`, and `GetPageLabel()` allocate unmanaged memory for every call. In throughput-heavy extraction jobs this creates avoidable allocator churn.

### Proposed Changes

#### 1. Replace `AllocHGlobal` with pooled managed buffers plus pinning

Use a helper pattern:

- determine required byte count
- rent `byte[]` or `char[]` from `ArrayPool<T>`
- pin with `fixed`
- call PDFium
- convert to string
- return the array to the pool

Applies to:

- `PdfPage.ExtractText()`
- `PdfDocument.DocumentId`
- `PdfDocument.GetPageLabel()`

#### 2. Centralize PDFium string-buffer handling

Add a small internal helper for “size query -> pooled buffer -> native call -> decode”.

Suggested helper responsibilities:

- UTF-16 buffer path for page text and labels
- byte buffer path for file identifier

This reduces repeated boilerplate and lowers the chance of buffer-length bugs.

#### 3. Stay pragmatic about what not to optimize

Do not over-engineer tiny one-off calls if they are not on hot paths.

Priority within this phase:

1. `PdfPage.ExtractText()`
2. `PdfDocument.GetPageLabel()`
3. `PdfDocument.DocumentId`

Reasoning:

- Text extraction is the most likely hot-path API in batch systems.

### Files

- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfPage.cs`
- `/Users/hamsman/Dev/PdfiumWrapper/src/PdfiumWrapper/PdfDocument.cs`

### Tests

- Extract text from empty pages, small pages, and large text pages.
- Validate page labels with and without labels.
- Validate document ID behavior on files that have and do not have an ID.

## Recommended Delivery Order

1. Fix memory-backed document ownership.
2. Add document/page lifetime coordination.
3. Add disposed guards everywhere touched by phases 1 and 2.
4. Refactor `SaveToStream()` callback allocation behavior.
5. Optimize text and metadata buffer management.

This order addresses correctness first, then reliability, then throughput polish.

## Suggested Benchmarks

After implementation, benchmark these scenarios:

- Load 10,000 PDFs from `byte[]` and extract `PageCount`
- Load 1,000 PDFs from stream and render page 1 to JPEG
- Save 1,000 modified PDFs to `MemoryStream`
- Extract text from all pages of a large document in a loop

Metrics:

- total allocated bytes
- Gen0/Gen1/Gen2 counts
- peak working set
- wall-clock time

## Definition Of Done

- No memory-backed document uses an invalid buffer after construction.
- Document disposal cannot race or conflict with live pages.
- Public APIs consistently throw managed disposal exceptions before hitting native code.
- Save callbacks do not allocate per chunk.
- Text and metadata APIs no longer use repeated `AllocHGlobal` in hot paths.
- Tests cover lifetime, disposal, and regression scenarios.
