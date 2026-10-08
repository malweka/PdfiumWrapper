# Plan: Pool qualification and the PdfiumWrapper.Processing release

Created 2026-10-08. Closes the last Phase 6 item of `plan-pdfium-concurrency.md` (qualification job) and checklist item 6 of `ai/tmp/release-2.0-checklist.md`, then publishes `PdfiumWrapper.Processing`.

## Context

- 2.0.0 of `PdfiumWrapper` and the four runtime packages is on nuget.org (2026-10-08, run 37823100509, tag `v2.0.0` at `3d2a752`). Processing was held back (`publish_processing` unticked).
- Processing depends on exactly the core version it was built with and uses core internals, so Processing 2.0.0 must be built from the 2.0.0 core source. `release/2.0.0` holds it.
- Covered by the test suite already: correctness oracle, cross-PID overlap, backpressure, fault injection, crash isolation, sizing, hosting through `dotnet <dll>` and an apphost. Not covered: long runs (memory or handle growth over thousands of jobs), a 10,000-job burst, a self-contained publish of the worker.

## Release decision

- **No defect found:** publish Processing 2.0.0 by rerunning `release.yml` on `release/2.0.0` with `publish_processing` ticked. `--skip-duplicate` skips the five packages already published. Before that, confirm `git diff release/2.0.0 main -- src/PdfiumWrapper src/PdfiumWrapper.Processing` is empty or holds nothing the qualification depended on: the harness builds from its own branch.
- **Defect found in core or Processing:** fix on `main`, rerun the qualification, release 2.0.1 of everything (core, runtimes, Processing) from `release/2.0.1`.

## Harness: `src/PdfiumWrapper.Qualification`

Console app, not packed, in the solution so PR CI compiles it. It is its own worker (`PdfWorkerHost.TryRun()` first in `Main`, `WorkerPath = null`), the hosting model the README recommends.

Workload (deterministic, seeded):

- Operations: page count, PNG, JPEG, TIFF (bilevel), text extraction, mixed.
- Documents: the five good test documents, plus `encrypted.pdf` without its password and a truncated PDF, about 1% each, which must end `Failed` and leave no output.
- Inputs: file path, byte array and stream.
- Every successful result is checked against the same call made in-process before the pool starts (SHA-256 per page for PNG and JPEG, per file for TIFF, exact text, page count). Outputs are deleted after the check.

Modes:

- `burst`: N jobs (default 10,000) submitted as fast as backpressure allows (pool `QueueCapacity` 256, harness keeps 512 in flight), then a 500-document batch through the batch API.
- `soak`: runs for M minutes (default 30) in cycles of 4 minutes at full load, an idle reading, and 90 s at one job at a time, so the pool scales up and back down to `MinWorkers` (idle timeout 60 s) every cycle.
- Both: `MinWorkers = 2`, `MaxWorkers = 8`.

Measured and reported (JSON plus a Markdown summary, also written to `GITHUB_STEP_SUMMARY`):

- Status counts, unexpected statuses, retries, output mismatches, timings p50/p95/p99, pool statistics and events.
- Each worker's and the coordinator's working set, private bytes and handle count every 1,000 jobs and every 60 s under load (information only: a reading under load mostly reflects which documents the worker holds; the first local soak swung -40% to +78% between readings with no trend).
- Idle readings decide growth: taken 2 s after the queue drains. Burst: after a warm-up (10% of the jobs, at most 500) and at the end. Soak: after every 4-minute full-load period and at the end; the two `MinWorkers` workers live through every cycle. Growth is first against last idle reading; over the limit means above 10% and above 8 MB (20 handles). Smoke runs report growth without judging it.
- End state: no worker process alive 5 s after `DisposeAsync`, no file left in the output root or the pool temp directory.

Exit code: 0 pass; 1 a correctness or cleanup check failed; 2 only idle growth over the limit (investigate, per the plan: do not recycle workers to hide it).

## Where it runs

`.github/workflows/qualification.yml`, manual (`workflow_dispatch`): self-contained publish of the harness, then burst and soak on `ubuntu-latest` and `windows-latest`, and a 1,000-job smoke on `macos-latest` (osx-arm64). Reports are uploaded as artifacts. Locally: `dotnet run -c Release --project src/PdfiumWrapper.Qualification -- burst`.

## Checklist

- [x] Harness project, added to the solution.
- [x] Local runs on win-x64 (2026-10-08). Smoke (`dotnet run`, then a self-contained publish): 1,100 jobs, all checks pass, 8 workers gone after dispose. Soak 7 min: correctness clean, but readings under load swung -40% to +78%, so growth moved to idle readings. Soak 12 min (self-contained): exit 0; idle private bytes 28-35 MB per worker after the first load period; the longest-lived worker went 28.6 -> 30.1 -> 32.1 MB over about 8,000 jobs (under the 8 MB floor), to watch in the 30-minute soak.
- [ ] `qualification.yml`; PR; CI green.
- [ ] Run the workflow: 10,000-job burst and 30-minute soak on win-x64 and linux-x64, smoke on osx-arm64.
- [ ] Review the reports. Any growth above 10% after warm-up: investigate before releasing.
- [ ] Record the results in `plan-pdfium-concurrency.md` (tick the Phase 6 qualification item and the self-contained publish), `ai/tmp/release-2.0-checklist.md` item 6 and `ai/current-state.md`.
- [ ] Release Processing per the release decision above. CHANGELOG: drop the "not published yet" note.
