# Import progress gauge — design

**Date:** 2026-06-20
**Status:** Approved (brainstorming), pending implementation plan
**Scope:** A double progress display (per-file % + overall %) shown while SQLFerret
ingests `.xel` files, in **both** the CLI `import` command and the TUI `ImportView`.

## Problem

When SQLFerret loads `.xel` files you wait with no feedback. The slow part — XELite
parsing every event out of the `.xel` XML — currently emits **zero** progress, because
`XelReader.Read()` buffers an entire file into memory (the `collected` list) *before*
yielding any event. The existing `IngestionProgress` only fires afterwards, during the
comparatively fast map/normalize/insert pass (on batch boundaries). So a gauge sits at 0%
during the dominant cost, then jumps.

Therefore the gauge must be driven from **inside the XELite read callback**, where the only
thing known per file is its byte size. Event counts are **not** known ahead of time
(streaming reader, no exposed byte offsets). Size-based event estimation is the way in — and
the natural basis for the overall gauge too.

## Non-goals (KISS — spec §2)

- **No streaming refactor of `XelReader`.** The collect-then-yield buffering stays. We add a
  per-event read callback during collection; we do not rearchitect into a producer/consumer.
- **No graphical/ANSI progress bar.** Dynamic percentage text only (user's explicit ask).
- **No two-pass pre-scan.** Exact counts would double parse cost on big files. Rejected.
- No change to the microseconds invariant, redaction, or counters — this is purely additive
  observability.

## The two gauges

- **Per-file %** = `eventsReadSoFar / estimatedEvents(file)`, driven by the XELite read
  callback as each event is collected.
- **Overall %** = `(bytesOfCompletedFiles + fileFraction × currentFileBytes) / bytesTotal`.
  **Byte-weighted**: a 200 MB file rightly dominates a 2 MB one. `bytesTotal` and per-file
  byte sizes already come from `XelSource.Resolve()` upfront.

## Estimation: self-calibrating bytes-per-event

`estimatedEvents(file) = fileBytes / bytesPerEvent`.

`bytesPerEvent` starts from a **seed constant** (measured from a `sample/` capture during
implementation, left as a commented `const`), then **refines** after each file completes: at
file completion we know the file's *exact* event count and *exact* byte size, so the running
average becomes near-perfect for every subsequent file. The first file is rough early and
accurate fast; later files are accurate from the start. The seed value is therefore
non-critical.

Calibration update on file completion (exact data):
```
totalEventsSeen += exactEventsInFile
totalBytesSeen  += fileBytes
bytesPerEvent    = max(1, totalBytesSeen / totalEventsSeen)   // never zero
```

### Guards

- **Zero-byte / zero-estimate file** → treated as instantly complete (no divide-by-zero).
- **Per-file fraction clamped to ≤ 0.99 during read.** If a file yields more events than
  estimated, the gauge pins at 99% rather than showing 100% and continuing. It snaps to
  **1.00 only on actual file completion**.
- `bytesTotal == 0` (defensive) → overall fraction reported as 0 then 1 on completion.

## New Core components (host-agnostic)

All under `SqlFerret.Core`. No new dependencies, no DI, plain records + a small stateful
coordinator (primary constructor) — consistent with the KISS architecture.

### 1. `ImportProgress` record

The unified gauge model emitted to hosts. The existing `IngestionProgress` is retained and
feeds the detail counters.

```csharp
public record ImportProgress(
    int  FileIndex,        // 1-based index of the file currently being read
    int  FileCount,        // total files in this import
    string CurrentFile,    // file name (not full path)
    double FileFraction,   // 0.0 .. 1.0  (per-file gauge)
    double OverallFraction,// 0.0 .. 1.0  (byte-weighted overall gauge)
    long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures);
```

### 2. `EventCountEstimator`

Self-calibrating bytes-per-event tracker.

- `long EstimateEvents(long fileBytes)` → `max(0, fileBytes / BytesPerEvent)`.
- `void Observe(long exactEvents, long fileBytes)` → updates the running average per the
  calibration formula above.
- Seeded with a `const long DefaultBytesPerEvent` (measured from sample data, commented).

### 3. `ImportProgressTracker`

Small coordinator that owns the import-wide progress state and produces `ImportProgress`.

- Constructed with the resolved file list `(path, bytes)[]`, `bytesTotal`, the
  `EventCountEstimator`, and the outer `IProgress<ImportProgress>` sink.
- Holds: `completedBytes` accumulator, current file index, latest detail counters.
- `OnRead(fileName, eventsInFile)` — called (throttled) from the read phase: computes
  per-file fraction (clamped ≤ 0.99) and byte-weighted overall fraction, emits `ImportProgress`.
- `OnFileComplete(fileName, exactEvents)` — calibrates the estimator, adds the file's bytes to
  `completedBytes`, snaps the file to 1.0, advances the index.
- `OnIngest(IngestionProgress)` — updates detail counters (Read/Mapped/…) for the detail line
  and re-emits.
- **Throttling**: emits only when the rounded per-file OR overall integer percent changes (or
  the file/index changes). This naturally caps update frequency (~100 ticks per file max) — no
  console/UI spam, no time-based logic.

## Wiring

- **`XelReader.Read`** gains an **optional** read-tick callback, e.g.
  `Action<string /*fileName*/, long /*eventsReadInFile*/>? onRead = null`, invoked from inside
  the XELite event callback as each event is collected. Optional ⇒ existing callers and tests
  are unaffected. The callback also fires once per file boundary so the tracker can call
  `OnFileComplete` with the exact count.
- **`IngestionService`** gets **one new overload** accepting the resolved `(files, bytesTotal)`
  plus `IProgress<ImportProgress>`. It constructs the `ImportProgressTracker`, builds the read
  with the read-tick callback wired to `tracker.OnRead` / `tracker.OnFileComplete`, and routes
  the existing per-batch `IngestionProgress` to `tracker.OnIngest`. The current `Ingest`
  signature stays — no breaking change.

## Host rendering

### CLI (`import`) — silent today

Render a single in-place line using `\r` (carriage return, no newline) updated from the
`IProgress<ImportProgress>` callback; emit a trailing newline on completion. **Plain ASCII**,
no Unicode bar (portable, redirect-safe). Example:

```
[2/5] perf_3.xel  file 47%  overall 61%  read=812k mapped=790k
```

Counts abbreviated (k/M) for compactness. The existing final summary line
(`run N: read=… mapped=…`) is printed afterwards, unchanged.

### TUI (`ImportView`)

Replace the current counter-only line with the same data including both percentages. The
`_app.Invoke` UI-thread marshaling, the `Progress<T>` wrapper, the re-entrancy guard, and the
`ImportStarted`/`ImportFinished` lifecycle are **untouched** — only the formatted text changes.

```
[2/5] perf_3.xel — file 47% · overall 61%   read=812k mapped=790k unmapped=21k cleaned=0 failures=0
```

## Testing (TDD: red → green → commit per change)

Pure-logic units (no `.xel`, no live SQL):

- **`EventCountEstimator`**: seed estimate; calibration after one file; calibration converges
  across multiple files; zero-byte file → 0 estimate, no divide-by-zero.
- **`ImportProgressTracker`**:
  - byte-weighted overall math across ≥3 files of differing sizes;
  - per-file fraction clamped to ≤ 0.99 during read, snaps to 1.0 on `OnFileComplete`;
  - throttle: repeated `OnRead` at the same integer percent emits no duplicate report;
  - single-file case (overall ≈ file);
  - detail counters from `OnIngest` flow into the emitted `ImportProgress`.
- **`XelReader`** read-tick: if the streamer seam is fakeable, assert the callback fires once
  per event and once per file boundary; otherwise gate like the existing `XelReaderTests`
  (`[SkippableFact]`, skips when `sample/` absent).
- **Hosts**: assert the formatted percentage string for a representative `ImportProgress`
  (CLI line formatter + TUI text formatter extracted as pure functions so they're testable
  without a terminal / message loop).

## Risks & mitigations

- **First-file estimate is rough** → accepted trade-off; clamp prevents overshoot past 99%.
- **Highly variable event sizes within one file** → per-file % may be non-linear, but overall
  % stays byte-accurate and the clamp keeps it sane; calibration fixes subsequent files.
- **Report frequency** → integer-percent throttling bounds it without timers.
- **Backward compatibility** → all additions are optional overloads/parameters; existing
  `Ingest` / `Read` signatures and tests are preserved.
