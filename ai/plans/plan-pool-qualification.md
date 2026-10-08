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
- Idle readings decide growth: taken 2 s after the queue drains. Burst: after a warm-up (10% of the jobs, at most 500), every 2,500 jobs and at the end. Soak: after every 4-minute full-load period and at the end; the two `MinWorkers` workers live through every cycle. Growth is the second against the last idle reading, judged with at least three; over the limit means above 10% and above 8 MB (20 handles). The first reading is not the baseline: glibc keeps freed memory, so idle memory on Linux sits near the high-water mark, which is still rising at warm-up (first CI run: +17% to +43% from warm-up to the end of the burst, while the soak's long-lived worker went 377, 422, 426, 426, 427 MB). Smoke runs report growth without judging it.
- End state: no worker process alive 5 s after `DisposeAsync`, no file left in the output root or the pool temp directory.

Exit code: 0 pass; 1 a correctness or cleanup check failed; 2 only idle growth over the limit (investigate, per the plan: do not recycle workers to hide it).

## Where it runs

`.github/workflows/qualification.yml`, manual (`workflow_dispatch`): self-contained publish of the harness, then burst and soak on `ubuntu-latest` and `windows-latest`, and a 1,000-job smoke on `macos-latest` (osx-arm64). Reports are uploaded as artifacts. Locally: `dotnet run -c Release --project src/PdfiumWrapper.Qualification -- burst`.

**Load tests run on a machine we control** (user decision, 2026-10-08): Windows natively, Linux in Docker on the dev machine. Do not dispatch `qualification.yml` for load or memory measurements; shared runners are slow (a 10,000-job burst takes 30-36 min there, 7 min locally) and give unrepeatable numbers. Linux recipe, comparing two commits on the same machine without touching the working tree:

```bash
git archive --format=tar -o before.tar <commit>; git archive --format=tar -o after.tar <commit>
docker volume create pwq
MSYS_NO_PATHCONV=1 docker run --rm -v pwq:/q -v "$PWD":/in:ro mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  for v in before after; do mkdir -p /q/src-$v && tar -xf /in/$v.tar -C /q/src-$v
    dotnet publish /q/src-$v/src/PdfiumWrapper.Qualification/PdfiumWrapper.Qualification.csproj       -c Release -r linux-x64 --self-contained true -o /q/$v; done'
MSYS_NO_PATHCONV=1 docker run --rm -v pwq:/q mcr.microsoft.com/dotnet/sdk:10.0   /q/before/PdfiumWrapper.Qualification burst --jobs 10000 --work /q/work --report /q/burst-before.json
```

Run the builds one after another, never at the same time, and repeat each at least once to see the noise.

## Checklist

- [x] Harness project, added to the solution.
- [x] Local runs on win-x64 (2026-10-08). Smoke (`dotnet run`, then a self-contained publish): 1,100 jobs, all checks pass, 8 workers gone after dispose. Soak 7 min: correctness clean, but readings under load swung -40% to +78%, so growth moved to idle readings. Soak 12 min (self-contained): exit 0; idle private bytes 28-35 MB per worker after the first load period; the longest-lived worker went 28.6 -> 30.1 -> 32.1 MB over about 8,000 jobs (under the 8 MB floor), to watch in the 30-minute soak.
- [x] `qualification.yml`; PR #28 merged (`f6c8b4e`).
- [x] First run, 2026-10-08 (run 37833914527, defaults). Correctness and cleanup clean everywhere: linux-x64 burst 10,500 and soak 10,199 jobs, win-x64 burst 10,500 and soak 6,853, osx-arm64 smoke 1,100; no unexpected status, no output mismatch, no output left by a failed job, no retry, every worker (8 + 32 + 8 + 26 + 8 started) gone 5 s after dispose, no file left. Self-contained publish works on all three. Verdicts: win-x64 soak PASS; both bursts and the linux-x64 soak "investigate". Not leaks:
  - Linux idle memory sits at the glibc high-water mark: soak long-lived worker 377, 422, 426, 426, 427 MB working set (flat for the last 6,000 jobs); the bursts compared a 500-job warm-up reading with the end (+17% to +43%).
  - win-x64 burst: only the coordinator, private 28.8 -> 37.8 MB while its managed heap went 7.8 -> 5.8 MB, after a 92 MB managed peak (stream inputs spooled in memory); GC keeps the committed pages. The soak coordinator stayed at 31-34 MB.
  - win-x64 soak workers idle private 22, 23, 26, 21, 22 MB.
  - osx-arm64: .NET reports private bytes and handle count as 0 on macOS; only working set is available there, and it stays high after jobs (smoke, not judged).
  - Linux workers are 4-5x larger than Windows ones (idle 250-430 MB working set, peak 400-500 MB, against 30 MB idle and about 110 MB peak on Windows): for the sizing docs, not a defect. Composition not yet investigated.
- [ ] Harness fix (branch `fix/qualification-growth-baseline`): growth from the second idle reading, at least three readings; idle readings every 2,500 jobs in the burst. Rerun the workflow for a clean record.
- [x] Linux worker memory (branch `fix/linux-worker-memory`, PR #30), measured locally in Docker (sdk:10.0, 24 cores, 10,000-job burst, 8 workers, all 10,500 outputs correct in every run):

  | Workers | Burst | p50 / p95 processing | Worker peak | Idle worker |
  |---|---|---|---|---|
  | glibc defaults (2 runs) | 6.9 min | 98 / 3,450 ms | 355-430 MB | 316-400 MB |
  | `MALLOC_ARENA_MAX=2` + `malloc_trim` after 1 s idle (2 runs) | 6.9-7.0 min | 97-99 / 3,410-3,540 ms | 175-206 MB | 72-117 MB |
  | also `MALLOC_MMAP_THRESHOLD_=131072` (2 runs) | 7.5 min | 108-113 / 3,690-3,710 ms | 107-121 MB | 64-69 MB |

  Shipped: the second row (no throughput cost); the threshold is a documented opt-in. The arena-only runs still exit 2 on the coordinator (the harness process gets no variable; 125 -> 147 MB working set, high-water, same as before the fix). The CI run of the first version (37849740630, cancelled in the soak) agreed: idle workers 66-72 MB.
- [ ] Harness growth rule: on Linux judge VmRSS (working set), not private bytes (address space incl. 8 MB thread stacks); judge the trend over the last readings (Windows soak private bytes 32, 39, 41, 41, 48, 47, 47 MB levels off but trips second-vs-last).
- [ ] Review the reports. Any growth above 10% after warm-up: investigate before releasing.
- [ ] Record the results in `plan-pdfium-concurrency.md` (tick the Phase 6 qualification item and the self-contained publish), `ai/tmp/release-2.0-checklist.md` item 6 and `ai/current-state.md`.
- [ ] Release Processing per the release decision above. CHANGELOG: drop the "not published yet" note.
