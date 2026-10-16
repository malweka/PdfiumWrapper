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
- `soak`: runs for M minutes (default 30) in cycles of 2 minutes at full load, an idle reading, and 90 s at one job at a time, so the pool scales up and back down to `MinWorkers` (idle timeout 60 s) every cycle.
- Both: `MinWorkers = 2`, `MaxWorkers = 8`.

Measured and reported (JSON plus a Markdown summary):

- Status counts, unexpected statuses, retries, output mismatches, timings p50/p95/p99, pool statistics and events.
- Each worker's and the coordinator's working set, private bytes and handle count every 1,000 jobs and every 60 s under load (information only: a reading under load mostly reflects which documents the worker holds; the first local soak swung -40% to +78% between readings with no trend).
- Idle readings decide growth: taken 2 s after the queue drains (a Linux worker trims its heap after 1 s idle). Burst: after a warm-up (10% of the jobs, at most 500), every 1,000 jobs and at the end. Soak: after every 2-minute full-load period and at the end; the two `MinWorkers` workers live through every cycle. Readings with no job between them count once.
- Growth rule (since 2026-10-08): the end of the run (median of the last three idle readings) against the highest idle reading of the first half, on the readings after the first (warm-up); judged with at least six. Over the limit: above 10% and above 8 MB (20 handles). A plateau passes; a leak of 0.5 KB per job fails a 38,000-job soak. Memory is private bytes on Windows (committed memory) and the working set elsewhere: on Linux `PrivateMemorySize64` is address space (8 MB per thread stack included) and on macOS .NET reports it as 0. Smoke runs report growth without judging it.
  - Why not the first or second reading against the last: allocators keep a high-water mark that rises in steps early on (glibc on Linux; the heap and the GC on Windows), so those comparisons flagged plateaus. With readings every 2,500 jobs or every 5.5 minutes, the longest-lived process had only 4-5 readings after warm-up, too few to tell a plateau from a slow rise (Windows soak worker: 39, 41, 41, 48, 47 MB).
- End state: no worker process alive 5 s after `DisposeAsync`, no file left in the output root or the pool temp directory. A worker is matched by PID and start time (within 1 s): Windows hands a freed PID to other processes, and the first 60-minute soak counted a search indexer that had taken a worker's PID.
- Worker events in the report: starts, stops, crashes, retirements, scaling, time-outs and retries (at most 500). Failed jobs and `QueueFull` are counted only: they filled the list within 18 s.

Exit code: 0 pass; 1 a correctness or cleanup check failed; 2 only idle growth over the limit (investigate, per the plan: do not recycle workers to hide it).

## Where it runs

On a machine we control, never on GitHub Actions (user decision, 2026-10-08; `qualification.yml` was removed). Shared runners are slow (a 10,000-job Linux burst took 29-36 min there, 7 min locally) and give numbers that do not repeat. Windows runs natively; Linux runs in Docker on the same machine.

Windows, self-contained:

```powershell
dotnet publish src/PdfiumWrapper.Qualification -c Release -r win-x64 --self-contained -o <dir>
<dir>/PdfiumWrapper.Qualification.exe burst --jobs 10000 --work <work> --report <work>/burst.json
<dir>/PdfiumWrapper.Qualification.exe soak --minutes 30 --work <work> --report <work>/soak.json
```

Linux, comparing two commits without touching the working tree (Git Bash; `MSYS_NO_PATHCONV=1` keeps the container paths intact):

```bash
git archive --format=tar -o before.tar <commit>
git archive --format=tar -o after.tar <commit>
docker volume create pwq
MSYS_NO_PATHCONV=1 docker run --rm -v pwq:/q -v "$PWD":/in:ro mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  for v in before after; do
    mkdir -p /q/src-$v && tar -xf /in/$v.tar -C /q/src-$v
    dotnet publish /q/src-$v/src/PdfiumWrapper.Qualification/PdfiumWrapper.Qualification.csproj \
      -c Release -r linux-x64 --self-contained true -o /q/$v
  done'
MSYS_NO_PATHCONV=1 docker run --rm -v pwq:/q mcr.microsoft.com/dotnet/sdk:10.0 \
  /q/before/PdfiumWrapper.Qualification burst --jobs 10000 --work /q/work --report /q/burst-before.json
```

Run builds one after another, never at the same time, and run each at least twice to see the noise (under 2% here).

## Results (2026-10-08, final code = PR #29 + PR #30)

**Verdict: qualified.** Every run returned every job with the expected status and output identical to the in-process call, left nothing behind, and every worker was gone 5 s after dispose. Memory levels off on both platforms; nothing leaks. The exit-2 verdicts below came from the old growth rule; the current rule passes the soaks (see "Growth rule check").

All runs: MinWorkers 2, MaxWorkers 8, seed 1, self-contained publish, one machine (24 cores; Linux in Docker, `mcr.microsoft.com/dotnet/sdk:10.0`, Debian 12).

| Run | Jobs | Time | Processing p50 / p95 / p99 | Correct, clean | Exit |
|---|---|---|---|---|---|
| win-x64 burst | 10,500 | 7.6 min | 130 / 3,550 / 4,327 ms | yes | 2 (rule: private 29 -> 38 MB, levelling) |
| win-x64 soak, 30 min | 36,365 | 30.4 min | 117 / 3,457 / 4,225 ms | yes | 2 (rule: 32, 39, 41, 41, 48, 47, 47 MB) |
| linux-x64 burst, before the fix (2 runs) | 10,500 | 6.9 min | 98 / 3,450 / 4,280 ms | yes | 2 (real high-water growth) |
| linux-x64 burst, final (2 runs) | 10,500 | 6.9-7.0 min | 97-99 / 3,410-3,540 / 4,300-4,400 ms | yes | 2 (coordinator only) |
| linux-x64 soak, final, 30 min | 38,063 | 30.4 min | 95 / 3,407 / 4,311 ms | yes, 38 workers started, 0 left | 2 (coordinator only) |
| osx-arm64 smoke (GitHub, first run) | 1,100 | | | yes | not judged |

Memory per worker process, working set (Linux: VmRSS):

| | Peak | Idle | Longest-lived worker, idle readings over the soak |
|---|---|---|---|
| Windows | 103-129 MB | 56-79 MB (private 24-48 MB) | 69 -> 79 MB, levelled off |
| Linux before the fix | 355-465 MB | 316-427 MB | 377, 422, 426, 426, 427 MB (GitHub soak) |
| Linux final (`MALLOC_ARENA_MAX=2` + idle trim) | 175-206 MB | 94-117 MB | 103 -> 106 MB over 7 readings and 38,063 jobs |
| Linux with `MALLOC_MMAP_THRESHOLD_=131072` too (opt-in) | 107-121 MB | 64-69 MB | not soaked |

Coordinator (the harness process, no allocator variable): 126-150 MB on Linux, 70-86 MB on Windows, at its high-water mark from the first readings on.

Other findings:

- The Linux fix costs no throughput; the mmap threshold costs about 9% (7.5 min, p50 108-113 ms), so it is an opt-in in the docs.
- GitHub runners were 4-5x slower than this machine (Linux burst 29-36 min, p50 processing 315-528 ms) and their timings varied; their correctness results agreed with the local runs.
- On macOS .NET reports private bytes and handle count as 0; only working set is available.

### Growth rule check (2026-10-08, branch `fix/harness-growth-rule`)

The current rule on three soaks. The two Linux soaks ran one after the other in Docker while the Windows soak ran natively, so the machine was shared and the job counts are not comparable with the table above. Correctness and cleanup clean in all three.

| Run | Jobs | Process | Idle memory, in order (MB) | First-half high -> end | Verdict | Old rule |
|---|---|---|---|---|---|---|
| win-x64 soak, 60 min | 36,836 | longest-lived worker, private | 26, 30, 32, 33, 39, 40, 42, 44, 41, 45, 45, 45, 45, 44, 44 | 44 -> 44 MB (+1.6%) | ok | over (30 -> 44 MB) |
| | | coordinator, private | 32, 33, 37, 38, 37, 37, 37, 35, 37, 37, 38, 37, 38, 38, 38 | 38 -> 38 MB (+0.5%) | ok | ok |
| linux-x64 soak, 30 min | 24,794 | longest-lived worker, working set | 102, 105, 104, 104, 103, 106, 106, 106 | 105 -> 106 MB (+0.4%) | ok | ok |
| | | coordinator, working set | 126, 131, 135, 138, 144, 147, 147, 152 | 138 -> 147 MB (+6.3%) | ok | over (131 -> 152 MB) |
| linux-x64 soak, 30 min, `MALLOC_ARENA_MAX=2` on the container | 28,161 | longest-lived worker, working set | 103, 106, 102, 102, 103, 103, 104, 104 | 106 -> 104 MB (-1.5%) | ok | ok |
| | | coordinator, working set | 123, 124, 126, 127, 126, 126, 127, 127 | 127 -> 127 MB (+0.4%) | ok | ok |

- The Windows worker climbs until about 21,600 jobs and then holds at 41-45 MB for the last 15,000: a plateau, now passed.
- The Linux coordinator's rise is glibc arena retention: with `MALLOC_ARENA_MAX=2` on the container it is flat at 123-127 MB. The harness process gets no allocator setting by default (only workers do), so this is the case the docs cover with `ENV MALLOC_ARENA_MAX=2`. It passes the rule (under 10%), but it was still rising at the end: a longer soak without the variable would trip it.
- A 30-minute soak gives 8 idle readings, enough to judge the two `MinWorkers` workers and the coordinator; workers started by scaling live 1-3 cycles and are reported, not judged.
- The 60-minute Windows run first failed its cleanup check (1 of 92 workers "alive" after dispose): PID 11952 belonged to `SearchProtocolHost`, started during the run. Fixed by matching the start time; the smoke runs on both platforms report 0 alive.

## Checklist

- [x] Harness project, added to the solution.
- [x] Local runs on win-x64 (2026-10-08). Smoke (`dotnet run`, then a self-contained publish): 1,100 jobs, all checks pass, 8 workers gone after dispose. Soak 7 min: correctness clean, but readings under load swung -40% to +78%, so growth moved to idle readings. Soak 12 min (self-contained): exit 0; idle private bytes 28-35 MB per worker after the first load period; the longest-lived worker went 28.6 -> 30.1 -> 32.1 MB over about 8,000 jobs (under the 8 MB floor), to watch in the 30-minute soak.
- [x] `qualification.yml`; PR #28 merged (`f6c8b4e`). Removed on 2026-10-08: load tests run locally.
- [x] First run, 2026-10-08 (run 37833914527, defaults). Correctness and cleanup clean everywhere: linux-x64 burst 10,500 and soak 10,199 jobs, win-x64 burst 10,500 and soak 6,853, osx-arm64 smoke 1,100; no unexpected status, no output mismatch, no output left by a failed job, no retry, every worker (8 + 32 + 8 + 26 + 8 started) gone 5 s after dispose, no file left. Self-contained publish works on all three. Verdicts: win-x64 soak PASS; both bursts and the linux-x64 soak "investigate". Not leaks:
  - Linux idle memory sits at the glibc high-water mark: soak long-lived worker 377, 422, 426, 426, 427 MB working set (flat for the last 6,000 jobs); the bursts compared a 500-job warm-up reading with the end (+17% to +43%).
  - win-x64 burst: only the coordinator, private 28.8 -> 37.8 MB while its managed heap went 7.8 -> 5.8 MB, after a 92 MB managed peak (stream inputs spooled in memory); GC keeps the committed pages. The soak coordinator stayed at 31-34 MB.
  - win-x64 soak workers idle private 22, 23, 26, 21, 22 MB.
  - osx-arm64: .NET reports private bytes and handle count as 0 on macOS; only working set is available there, and it stays high after jobs (smoke, not judged).
  - Linux workers are 4-5x larger than Windows ones (idle 250-430 MB working set, peak 400-500 MB, against 30 MB idle and about 110 MB peak on Windows): for the sizing docs, not a defect. Composition not yet investigated.
- [x] Harness fix (PR #29): growth from the second idle reading, at least three readings; idle readings every 2,500 jobs in the burst. Rerun the workflow for a clean record.
- [x] Linux worker memory (branch `fix/linux-worker-memory`, PR #30), measured locally in Docker (sdk:10.0, 24 cores, 10,000-job burst, 8 workers, all 10,500 outputs correct in every run):

  | Workers | Burst | p50 / p95 processing | Worker peak | Idle worker |
  |---|---|---|---|---|
  | glibc defaults (2 runs) | 6.9 min | 98 / 3,450 ms | 355-430 MB | 316-400 MB |
  | `MALLOC_ARENA_MAX=2` + `malloc_trim` after 1 s idle (2 runs) | 6.9-7.0 min | 97-99 / 3,410-3,540 ms | 175-206 MB | 72-117 MB |
  | also `MALLOC_MMAP_THRESHOLD_=131072` (2 runs) | 7.5 min | 108-113 / 3,690-3,710 ms | 107-121 MB | 64-69 MB |

  Shipped: the second row (no throughput cost); the threshold is a documented opt-in. The arena-only runs still exit 2 on the coordinator (the harness process gets no variable; 125 -> 147 MB working set, high-water, same as before the fix). The CI run of the first version (37849740630, cancelled in the soak) agreed: idle workers 66-72 MB.
- [x] Harness growth rule (branch `fix/harness-growth-rule`): working set on Linux and macOS, private bytes on Windows; end median against the first half's high; idle readings every 1,000 jobs (burst) and every 2-minute load period (soak); workers matched by PID and start time. Checked on a 60-minute Windows soak and two 30-minute Linux soaks: all pass, see "Growth rule check".
- [x] Review the reports. Linux growth investigated (glibc retention, fixed in PR #30); Windows and the coordinator level off. See Results.
- [x] Record the results in `plan-pdfium-concurrency.md` (tick the Phase 6 qualification item and the self-contained publish), `ai/tmp/release-2.0-checklist.md` item 6 and `ai/current-state.md`.
- [ ] Release Processing per the release decision above. CHANGELOG: drop the "not published yet" note.
