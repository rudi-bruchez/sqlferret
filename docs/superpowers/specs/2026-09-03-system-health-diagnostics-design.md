# Design — Ingesting `sp_server_diagnostics_component_result` from a `system_health` capture

Status: proposed, revision 2
Date: 2026-09-03
Scope: `SqlFerret.Core` (Ingestion, Storage, Analysis) + the CLI host

Revision 2 follows an external review panel of five readers. Nine of its findings changed the
design rather than its wording; they are marked **[R2]** where they land. Revision 1 is in the
git history. What changed, in one list:

- `topWaits` counters are cumulative since instance start, and its two time columns cannot carry a
  headline aggregation in revision 1 was arithmetically wrong (§9).
- `health_samples` had no cycle key, and a cycle's four events do **not** share a timestamp —
  measured. The coverage block would have reported 1 344 cycles instead of 336 (§6).
- `blocking_reports` is written by a positional `INSERT`; the `source` column breaks it (§5).
- The `source` filter needs seven joins and a CTE re-key, not "one reviewable predicate" (§5).
- `component` has five documented values plus one per availability group, so an unrecognised
  component cannot be a parse failure (§2, §4).
- Revision 1's cadence claim confused `@repeat_interval` with the session's own timer (§1).
- Duration scalars reach `health_metrics` in milliseconds, breaking the microsecond invariant (§6).
- `filePath` is disclosed under every redaction mode, which revision 1 did not say (§7).
- `IngestionResult`, `FinishRun` and the CLI summary line change too (§4, §14).

---

## 0. Evidence base

Sections 1, 3, 5 and 9 rest on measurements taken against a real `system_health` capture — eight
`.xel` files, about 35 MB, 14 h 15 min of wall clock — not on recollection. That capture is
production data, is not part of this repository, and was deliberately withheld from the review
panel. Only element names, attribute names and aggregate counts are reproduced here. No value
from it appears in this document, and none may appear in a test fixture.

Per the standing invariant in `CLAUDE.md`, every claim below about SQL Server behaviour is
**measured**, **documented** (Microsoft Learn) or **unverified**, and says which. A measurement
describes the server that produced it, never the engine.

---

## 1. Problem

`sqlferret import` on a `system_health` capture produces **nothing**. Measured: `read=5610
mapped=0 unmapped=5610`, every other counter zero.

`EventMapper` routes three families — workload completions, blocking and deadlock reports, plan
profiles — and `system_health` contains almost none of them. What it does contain,
`sp_server_diagnostics_component_result`, carries a per-component XML payload no part of the tool
reads.

That payload is the only source in the project for facts no `.xel` workload trace, no Query Store
snapshot and no execution plan supplies: worker and pending-task pressure, the server's own top
waits, memory pressure and out-of-memory periods, non-yielding schedulers, spinlock backoffs, and
long I/O.

### What the capture says about coverage **[R2]**

Revision 1 claimed the session's history was thin because a flooded ring had evicted it, and
contrasted the survivors with "the 5-second cadence the event's own emission rate suggests". Both
halves were wrong.

The 5-second figure is the minimum `@repeat_interval` of the `sp_server_diagnostics` **stored
procedure** (documented). It says nothing about how often the `system_health` session records the
event, which is the engine's own timer. Revision 1 read a parameter off the wrong mechanism.

What is actually measured:

- 336 cycles over 14 h 15 min, and the files fall into **two interleaved series** offset by about
  35 minutes, each sampling at 5-minute intervals. Two sessions were recording this server in
  parallel. Combined, that yields a cycle every ~2.5 min on average, and an interval distribution
  that is bimodal — 164 of 335 gaps under a minute, most of the rest near 4.5 min.
- 107 295 of 110 387 events (97.2 %) are `security_error_ring_buffer_recorded`. The ring is
  genuinely dominated by one dull event; what is *not* established is that this evicted
  diagnostics cycles, and revision 1 should not have asserted it.

Two consequences the design must carry, not just mention:

**A capture folder may hold more than one sampling series.** `intervalLongIos` and
`tasksCompletedWithinInterval` are deltas scoped to *each session's* own interval. Summed or
averaged across interleaved series they are meaningless. The digest must detect multiple series
and refuse to combine interval-scoped metrics across them (§9).

**The interesting nodes are usually empty.** Across 336 cycles, `blockingTasks` was non-empty 14
times and `pendingTasks` 4. `topWaits` was populated in all 336, `cpuIntensiveRequests` in 172,
`longestPendingRequests` in 10.

A digest that ranks head blockers and stays silent about coverage would be honest on paper and
misleading in use. Coverage reporting is a feature of this work, not a footnote.

---

## 2. Scope

### In

- Routing and parsing `sp_server_diagnostics_component_result` for the four components this tool
  understands: `QUERY_PROCESSING`, `RESOURCE`, `SYSTEM`, `IO_SUBSYSTEM`.
- Recording, without parsing, every other component it meets (§4) **[R2]**.
- Persisting them relationally in the existing project directory.
- Feeding the `blocked-process-report` documents embedded in `QUERY_PROCESSING/blockingTasks` into
  the existing blocking tables, and the write- and read-path changes that requires (§5).
- A new CLI command producing a bounded, ranked digest.
- Three new `ingestion_runs` counters, and the `IngestionResult` / `FinishRun` / CLI summary
  changes they imply.

### Out, deliberately

The flat `system_health` events stay unmapped. Measured counts:

| Event | Count | Shape |
|---|---|---|
| `security_error_ring_buffer_recorded` | 107 295 | flat |
| `scheduler_monitor_system_health_ring_buffer_recorded` | 1 676 | flat |
| `connectivity_ring_buffer_recorded` | 62 | flat |
| `error_reported` | 6 | flat |
| `wait_info` | 1 | flat |

None carries nested XML — an assumption revision 1 started with and the capture refuted. They
belong to a later, smaller piece of work through `EventMapper`'s ordinary path.

`xml_deadlock_report` is already routed and needs no work: the capture contains three and they
ingest today (measured).

Also out: `sys.dm_os_ring_buffers` (a server-connected source, closer to `query-store-import`),
and any TUI surface.

---

## 3. What the payload actually contains

The event exposes three fields: `component`, `state`, `data` (measured).

`component` arrives upper-case — `QUERY_PROCESSING`, `RESOURCE`, `SYSTEM`, `IO_SUBSYSTEM`
(measured). `state` arrives as a **string**, `CLEAN` or `WARNING` (measured: 4 `WARNING` in 336
cycles, all `QUERY_PROCESSING`). Microsoft documents the DMV's `state` as an `int` with a separate
`state_desc`; what XELite hands over for the *event* is the description, and the parser stores the
string verbatim rather than mapping to an enum, so an unobserved value cannot be lost.

**The component set is larger than this capture shows [R2].** Microsoft documents five components
— `system`, `resource`, `query_processing`, `io_subsystem`, **`events`** — and, when
`component_type` is `Always On:AvailabilityGroup`, one row **named after each availability group**,
which is arbitrary user text. Neither appeared in this capture; both exist. §4 is written around
that, not around the four.

### 3.1 `QUERY_PROCESSING` → `<queryProcessing>`

Scalars: `maxWorkers`, `workersCreated`, `workersIdle`, `pendingTasks`,
`oldestPendingTaskWaitingTime`, `hasUnresolvableDeadlockOccurred`, `hasDeadlockedSchedulersOccurred`,
`trackingNonYieldingScheduler`, `tasksCompletedWithinInterval`.

Repeated children:

- `topWaits/{nonPreemptive,preemptive}/{byCount,byDuration}/wait` with `waitType`, `waits`,
  `averageWaitTime`, `maxWaitTime`. Four independent rankings. **These counters are cumulative —
  see §9.**
- `cpuIntensiveRequests/request` with `sessionId`, `requestId`, `command`, `cpuTimeMs`,
  `cpuUtilization`, `taskAddress`.
- `pendingTasks/entryPoint` with `name`, `count`. Note the name collision: `pendingTasks` is both
  a scalar attribute of `<queryProcessing>` and a repeated child node. They are different data and
  §6 stores them separately **[R2]**.
- `blockingTasks/blocked-process-report` — see §5.

### 3.2 `RESOURCE` → `<resource>`

Scalars: `isAnyPoolOutOfMemory`, `outOfMemoryExceptions`, `processOutOfMemoryPeriod`,
`lastNotification`. Repeated: `memoryReport` (`name`, `unit`) containing `entry` (`description`,
`value`) — already a name/value structure in the source.

### 3.3 `SYSTEM` → `<system>`

Fifteen scalars observed: `sqlCpuUtilization`, `systemCpuUtilization`, `spinlockBackoffs`,
`sickSpinlockType`, `sickSpinlockTypeAfterAv`, `nonYieldingTasksReported`, `latchWarnings`,
`pageFaults`, `BadPagesDetected`, `BadPagesFixed`, `LastBadPageAddress`,
`isAccessViolationOccurred`, `writeAccessViolationCount`, `intervalDumpRequests`,
`totalDumpRequests`.

The inconsistent casing (`BadPagesDetected` beside `pageFaults`) is SQL Server's, and is one reason
§6 stores scalars as name/value rather than as columns.

### 3.4 `IO_SUBSYSTEM` → `<ioSubsystem>`

Scalars: `totalLongIos`, `intervalLongIos`, `ioLatchTimeouts`. Repeated:
`longestPendingRequests/pendingRequest` with `duration`, `filePath`, `handle`, `offset`.

---

## 4. Ingestion

`EventMapper` gains one predicate, mirroring `IsPlanProfile`:

```csharp
public static bool IsServerDiagnostics(string name) =>
    name.Equals("sp_server_diagnostics_component_result", StringComparison.OrdinalIgnoreCase);
```

Strict equality, not `Contains`, for the reason already recorded on `IsPlanProfile`.

`IngestionService.Ingest` grows a branch beside the blocking and plan-profile ones, parsing with a
new `ServerDiagnosticsParser` in `Ingestion`, buffering, and flushing on `BatchSize`.

### Three outcomes, not two **[R2]**

Revision 1 said an unrecognised component was a parse failure, reasoning that a future fifth
component must not vanish. The reasoning was right and the rule was wrong: the fifth component
already exists (`events`), and an Always On instance emits one row per availability group named
after the group. Under revision 1 every such capture would have reported thousands of parse
failures forever, and a counter that always fires tells a reader nothing.

`ServerDiagnosticsParser.TryParse(component, state, xml, capturedAt)` therefore has three outcomes:

| Outcome | Meaning | Counter |
|---|---|---|
| Parsed | one of the four known components, XML well-formed | `events_server_diagnostics` |
| Unhandled | any other component, including `events` and AG names | `events_server_diagnostics_unhandled` |
| Failed | a known component whose XML is malformed, absent or whose root does not match | `server_diagnostics_parse_failures` |

An unhandled component still gets a `health_samples` row with its `component` and `state`, and no
child rows. Nothing vanishes, the counter that means "something is wrong" only fires when
something is wrong, and a future component shows up as a rising `unhandled` count rather than as
noise.

A missing `data` field is a **Failed**, mirroring the plan-profile branch's handling of a missing
`showplan_xml`. `TryParse` never throws: a `catch` returning null on a parse failure is the
deliberate fallback path the project allows, commented at the site.

### Counters, and the three places they must reach **[R2]**

Three columns on `ingestion_runs`, via the existing `ADD COLUMN IF NOT EXISTS` block:
`events_server_diagnostics`, `events_server_diagnostics_unhandled`,
`server_diagnostics_parse_failures`, all `BIGINT`.

Revision 1 stopped at the database. Three other places carry the counter set and must change in
lockstep, or the numbers stop adding up where users read them:

- `IngestionResult` — a 13-field positional record.
- `DuckDbProject.FinishRun` — its signature.
- The CLI import summary line in `Program.cs`.

Counters stay mutually exclusive: a name-matched event is counted in exactly one of the three.
`events_unmapped` drops by exactly their sum, which is a testable invariant. The ingestion-quality
query published in `docs/data-model.md` sums the named counters and must gain these three, or it
silently stops reconciling to `events_read`.

---

## 5. `blockingTasks`: reuse, and what reuse actually costs

### The measurement

`blockingTasks` contains `<blocked-process-report monitorLoop="…">` documents — the same root
element and structure `BlockingReportParser` already parses, and the same path Microsoft's own
documentation uses to extract them (`/queryProcessing/blockingTasks/blocked-process-report`).

Fed the 69 embedded documents from the capture, the existing parser returned 69 reports, 0
failures, all 69 carrying an input buffer (measured). The review panel independently confirmed by
synthetic input that the varying attribute sets — reports missing `waitresource`, `loginname`,
`waittime`, `logused` — are all optional and produce `null` rather than a wrong value.

### The decision

Embedded reports are parsed by `BlockingReportParser` and stored in the existing
`blocking_reports` / `blocking_processes` tables, inheriting the wait-resource parser, the
input-buffer fingerprint that joins them to `normalized_queries`, and the privacy gate repaired in
0.2.0.

### The cost, stated honestly **[R2]**

Revision 1 argued that a `source` column "concentrates the risk in one reviewable predicate". That
was false, and it was the argument on which the decision was approved. The real cost:

**1. The write path breaks first.** `DuckDbProject.InsertBlockingBatch` inserts positionally:

```csharp
c.CommandText = "INSERT INTO blocking_reports VALUES ($id,$run,$ts,$loop,$db,$raw)";
```

Adding a seventh column makes every blocking insert fail — `Binder Error: table blocking_reports
has 7 columns but 6 values were supplied` — for ordinary `blocked_process_report` events as much
as for the new path, on existing projects as much as new ones, because the migration runs on every
`Open`. Four of five reviewers reproduced this. `InsertBlockingBatch` must move to a named column
list and `PreparedBlockingReport` must carry the source. The same positional pattern is used for
`executions`, `blocking_processes` and `deadlock_reports`, and is a standing trap for any future
`ADD COLUMN`; fixing the one this change touches is in scope, fixing the other three is not.

**2. Seven queries need a new join, not a new predicate.** `source` lives on `blocking_reports`,
but seven of the ten methods in `BlockingQueries` read only `blocking_processes`, which has no
`source` and no join to `blocking_reports`: `Locality`, `TopObjects`, `LockModes`,
`IsolationLevels`, `TopBlockers`, `TopBlocked`, `WaitTimes`. Each needs the join added.

**3. `Chains()` needs re-keying, and a filter does not fix it.** Its `edges` CTE keys on
`monitor_loop` alone, across all reports, so two unrelated reports sharing a value already merge
into one fabricated chain — a defect that predates this change and that mixing sources would make
common. Revision 1 wrote that queries must "filter **or group** on `source`"; grouping does not
fix this, because the fabrication happens inside the CTE before any grouping. The CTE must key on
`(source, monitor_loop)` **and** the query must filter. A `monitorLoop` that is NULL also breaks
the recursion, since `NULL = NULL` is unknown.

**4. `EventExport` was not in revision 1's list.** `export-events --kind blocking` reads
`blocking_reports` with no source notion; diagnostics snapshots retained under `--redaction off`
would be exported and counted as `blocked_process_report` events. The intended behaviour must be
stated: this design says `export-events` exports **only** `source = 'event'`, because the command
exists to hand back the original XE documents.

`Reclassifier` reads `blocking_processes.inputbuf` by fingerprint only; source mixing is harmless
there and it needs no change.

**5. The predicate shape is part of the design, not the implementation.** NULL means `'event'`,
the convention `sql_text_policy` already uses. Therefore every filter is written
`coalesce(source, 'event') = 'event'`. A plain `WHERE source = 'event'` would silently drop every
pre-migration row, which is the same class of silent wrongness this column exists to prevent.

### Why not separate tables

Separate `health_blocking_*` tables would remove all risk to the existing queries. They would also
duplicate the parser's output shape, the fingerprint join, the digest and the privacy gate — four
things currently correct in exactly one place. That argument survives review; the "one predicate"
argument does not. The panel's own verdict, having enumerated the cost, was to keep the column.

`BlockingSourceIsolationTests` must assert the `Chains()` case specifically, since that is the one
a filter alone does not fix.

### A measured caveat for the digest

Of the 69 embedded reports, only **3** carried a wait resource the parser could type. Embedded
snapshots are poorer than threshold-triggered reports, and any ranking by wait-resource type will
show them as `Other` almost always.

---

## 6. Schema

Eight new tables. `sample_id` and `cycle_id` are assigned by the same monotonic-id mechanism the
project already uses for `execution_id`.

```sql
CREATE TABLE IF NOT EXISTS health_cycles (
  cycle_id BIGINT PRIMARY KEY, run_id BIGINT, cycle_at TIMESTAMP, series_key TEXT);

CREATE TABLE IF NOT EXISTS health_samples (
  sample_id BIGINT PRIMARY KEY, cycle_id BIGINT, run_id BIGINT, captured_at TIMESTAMP,
  component TEXT, state TEXT, handled BOOLEAN);

CREATE TABLE IF NOT EXISTS health_metrics (
  sample_id BIGINT, name TEXT, value_num DOUBLE, value_big BIGINT, value_text TEXT);

CREATE TABLE IF NOT EXISTS health_waits (
  sample_id BIGINT, preemptive BOOLEAN, ranking TEXT,
  wait_type TEXT, waits BIGINT, avg_wait_us BIGINT, max_wait_us BIGINT);

CREATE TABLE IF NOT EXISTS health_cpu_requests (
  sample_id BIGINT, session_id INTEGER, request_id INTEGER, command TEXT,
  cpu_time_us BIGINT, cpu_utilization DOUBLE, task_address TEXT);

CREATE TABLE IF NOT EXISTS health_pending_tasks (
  sample_id BIGINT, entry_point TEXT, task_count BIGINT);

CREATE TABLE IF NOT EXISTS health_pending_io (
  sample_id BIGINT, duration_us BIGINT, file_path TEXT, handle TEXT, offset_bytes BIGINT);
-- offset_bytes, not offset: OFFSET is reserved in DuckDB. Verified by three reviewers, who each
-- ran CREATE TABLE t (offset BIGINT) and got a parser error rather than taking the comment's word.

CREATE TABLE IF NOT EXISTS health_memory_entries (
  sample_id BIGINT, report_name TEXT, unit TEXT, description TEXT,
  value_num DOUBLE, value_text TEXT);
```

All seven tables of revision 1 executed verbatim under DuckDB.NET 1.5.3 in four independent
reviews; `ranking`, `state`, `component`, `command`, `handle` and `waits` are all safe unquoted.

### The cycle key **[R2]**

Revision 1 had no way to identify a cycle, while §9's headline metric counts cycles. The obvious
candidate — `captured_at` — does not work, and the measurement is unambiguous: **1 344 diagnostics
events carry 1 344 distinct timestamps**; grouping to the second yields exactly 336 groups of 4,
with an intra-cycle spread of 0.22 to 0.67 ms. Using `captured_at` would have reported 1 344
cycles and a median interval near 0.2 ms instead of 336 and minutes — wrong by 4× and by five
orders of magnitude, in the block this design calls its honesty mechanism.

`health_cycles` therefore carries the identity explicitly. The parser groups the four component
events of a cycle by truncating to the second; `cycle_at` is the earliest of the four.

`series_key` addresses §1's interleaved series: cycles whose spacing forms a distinct arithmetic
series are tagged, so the digest can refuse to combine interval-scoped metrics across them. The
grouping rule is a heuristic over observed spacing and must be reported to the user rather than
trusted silently — the digest states how many series it found.

### Why scalars are name/value

Every scalar attribute of every component goes to `health_metrics`, keyed by its source name.
Typed columns per component would read better and be wrong sooner: the attribute set varies by
SQL Server version, and a fixed column list drops an unknown attribute silently.

`value_big BIGINT` sits beside `value_num DOUBLE` **[R2]** because these are integer counters and
`DOUBLE` loses integers above 2^53 — reviewers demonstrated `2^53 + 1` round-tripping to `2^53`.
Integers land in `value_big`, genuine reals in `value_num`, non-numerics in `value_text`. The same
reasoning adds `value_text` to `health_memory_entries`, which revision 1 left unable to store a
non-numeric entry.

The four repeated nodes stay relational because the digest ranks on them.

### Units **[R2]**

The source reports milliseconds for `averageWaitTime`, `maxWaitTime`, `cpuTimeMs` and
`pendingRequest/@duration`; these become `_us BIGINT` columns, converted in the parser.

Revision 1 stopped there, and left a hole four reviewers found independently:
`oldestPendingTaskWaitingTime` and `processOutOfMemoryPeriod` are durations that land in
`health_metrics`, where a name/value row cannot carry a `_us` suffix — so the invariant's
enforcement mechanism does not reach them, and `sqlferret query`'s duration formatting, which keys
off column names, cannot help a reader either.

Duration-valued scalars are converted at parse time by an explicit allow-list and stored under a
name carrying the unit: `oldestPendingTaskWaitingTimeUs`. The allow-list is a named constant in
`ServerDiagnosticsParser`, so adding to it is a visible edit rather than a guess at a name.

`cpu_utilization` and the memory entries are not durations and keep their source unit;
`health_memory_entries.unit` records what that was.

---

## 7. Privacy

- **`blockingTasks`** — the embedded reports carry `inputbuf`, `loginname`, `hostname` and
  `clientapp`. They are prepared by `IngestionService.PrepareProc`, unchanged, so they inherit
  `VerbatimStatementTextAllowed`, the input-buffer normalization and the QI-collapse fix. Four
  reviewers verified this against the code; it needs no new gate.
- **`cpuIntensiveRequests/@command`** — a command class (`SELECT`, `BACKUP DATABASE`), not
  statement text. Stored as-is. The plan must include an assertion pinning that shape, so a future
  SQL Server putting statement text there is noticed rather than absorbed.
- **`longestPendingRequests/@filePath`** — a server-side path disclosing instance name, drive
  layout and database file names. Revision 1 said it was out of scope. It must also say the part
  revision 1 omitted **[R2]**: this disclosure is **unconditional across all four redaction modes**,
  including `full`, which is the mode a user picks in order to share a project. Unlike statement
  text, no flag removes it. `docs/privacy.md`'s table is organised around which columns survive
  which policy and needs a row saying the health tables sit outside that structure. The same is
  true of `health_cpu_requests.session_id` and `health_memory_entries.description`.

`health_metrics` holds counters and flags from SQL Server's own diagnostics, plus two free-form
engine strings (`lastNotification`, `sickSpinlockType`) — not user data, but not "only counters"
either.

---

## 8. Provenance and versioning

No new version constant. `QueryNormalizer.Version` and `SqlTextSanitizer.Version` are untouched:
nothing here changes normalization, classification or the sanitizer's output.

`blocking_reports.source` is a column rather than a version because it distinguishes rows within a
run, not runs from each other.

`HealthDigest.SchemaVersion = 1`, mirroring `BlockingDigest`, and the JSON output is wrapped in a
versioned envelope like `BlockingDigestEnvelope` **[R2]**. A coverage-first digest is exactly the
kind of output whose shape will move.

---

## 9. `export-health`

```text
sqlferret export-health --project <dir> [--format json|md|both] [--out <file>] [--limit <n>]
```

`--limit`, not `--top`: the two bounding flags in this CLI are `--limit` and `--samples`, and a
third name for the same idea is a gratuitous inconsistency **[R2]**. `--out` rejects path
traversal, in Core, as `export-blocking` and `export-events` already do.

Aggregation lives in DuckDB SQL, in a new `HealthQueries` beside `BlockingQueries`, with a
`HealthDigest` assembling the bounded result.

On a project with **no** `health_samples` at all, the command says so and exits 0. That is a
different message from "a healthy server produced no findings", and conflating them is the
misleading output §1 exists to prevent.

### Coverage first

- first and last cycle, wall-clock span, cycle count, median interval, largest gap;
- **how many sampling series were detected**, and their individual cadences;
- the share of the capture's events that are not diagnostics.

### Then, ranked and bounded

**1. Cycles whose `state` is not `CLEAN`**, by component.

**2. Waits — as deltas, never sums [R2].** Revision 1 said "top waits aggregated across cycles".
Measured on one series of 147 cycles: `waits` is strictly monotonic increasing for every wait type
— 146 increasing transitions, 0 decreasing, values around 8.9 × 10⁹. These are **cumulative
counters since instance start**, like `sys.dm_os_wait_stats`. Summing them across 336 cycles would
have produced a ranking of uptime, not of activity.

The digest computes, per `(wait_type, preemptive, ranking)` within one series, `last − first`
across the window, and divides by the window's elapsed time for a rate.

Two consequences that must be handled, not glossed:

- **Only the top N appear per cycle.** A wait type that leaves the list mid-window has a delta
  computed over a shorter span than the window, and one that enters late likewise. The digest
  records, per wait type, the span its delta actually covers, and does not compare a delta over
  40 minutes with one over 14 hours as though they were the same measurement.
- **A counter that decreases means the instance restarted.** That interval is dropped and the
  count of dropped intervals is reported.

**The two time columns cannot carry a window ranking at all (measured).** Over the same 147
cycles:

- `maxWaitTime` is **constant** for every wait type — not rising, frozen. That is what a running
  maximum since instance start looks like when its record was set before the window. It is not the
  worst wait observed during the capture and must never be presented as one.
- `averageWaitTime` is a **cumulative average**, near-frozen because its denominators are in the
  billions, and it sometimes *decreases* (`LCK_M_IX`: 1 rise against 5 falls) — which a running
  maximum cannot do and a running mean can. It is also **rounded to whole milliseconds**, so it
  reads `0` for the highest-frequency waits.

Deriving a window mean as `(avg × waits)_last − (avg × waits)_first` is therefore arithmetic on a
value already rounded to zero for exactly the waits that matter most.

So the digest ranks waits **by count delta only**, and says so. Duration-based wait ranking is not
recoverable from this source at useful precision; the workload capture is where that question is
answered. `avg_wait_us` and `max_wait_us` are still stored — they are the instance-lifetime figures
and are worth having — but they are labelled as since-startup values wherever they appear, and no
ranking orders on them.

**3. Memory pressure** — `outOfMemoryExceptions`, `processOutOfMemoryPeriod`,
`isAnyPoolOutOfMemory`, and the memory-report entries that moved most across the window.

**4. Stability signals from `SYSTEM`** — non-yielding tasks, spinlock backoffs, latch warnings,
dump requests, access violations. Reported when non-zero, omitted when zero rather than listed as
a wall of zeros. These are cumulative too and are reported as deltas.

**5. Worker pressure** — `pendingTasks` (the scalar), `oldestPendingTaskWaitingTimeUs`,
`workersIdle` against `maxWorkers`. These are point-in-time gauges: aggregated as min / median /
p95 / max, not summed.

**6. I/O** — `totalLongIos` as a delta; `intervalLongIos` **only within one series**, since it is
scoped to that session's interval; and the worst pending requests.

**7. Blocking observed in diagnostics cycles** — from `blocking_reports` where
`coalesce(source,'event') = 'diagnostics'`, labelled as snapshots, never mixed with
threshold-triggered reports.

---

## 10. What this promises, and what it does not

**It gives** the server's own view of itself over the window the ring still holds: what it waited
on, whether it was short of memory or workers, whether anything went non-yielding, and what was
blocking at each sampling instant.

**It does not give** a workload profile. The cadence is minutes, the history is whatever the ring
has not evicted, and the sampling is blind between cycles. A statement that ran badly for ninety
seconds between two cycles leaves no trace. For "which query shape costs the server", the `.xel`
workload capture and Query Store remain the sources.

**It is silent by construction on a healthy server.** `blockingTasks` was non-empty in 14 of 336
cycles. An empty finding list is a normal, informative result and the digest presents it as such.

**Its counters are mostly cumulative.** Every number the digest reports as activity is a delta over
a stated span, and the span is printed beside it.

---

## 11. Architecture

No new dependency edge, no new package, no new abstraction. `ServerDiagnosticsParser` is a static
class like `BlockingReportParser`; `HealthQueries` is a primary-constructor class like
`BlockingQueries`; aggregation stays in DuckDB SQL. Parsing uses `System.Xml.Linq`, already used.

Four reviewers checked this against the enumerated KISS prohibitions in `CLAUDE.md` and found no
violation, including the specific question of whether name/value storage forces C#-side reduction
loops. It does not: pivots and rankings over it stay expressible in DuckDB SQL.

---

## 12. Performance

1 344 diagnostics events against 110 387 total (measured): one `XElement.Parse` per diagnostics
event is noise next to the existing hot path, and does not repeat the three-parse problem
`docs/development.md#known-gaps` records for the workload path.

---

## 13. Testing

Fixtures are synthetic and anonymous, in the project's vocabulary (`SampleApp`, `AppSchema`,
`WidgetRecalc`, `@WidgetId`, `@GadgetCode`). **No fragment of the real capture may be copied into a
test**, including file paths, host names, login names and wait-resource strings. Element and
attribute names are SQL Server's schema and are safe; everything between the tags is not.

- `ServerDiagnosticsParserTests` — one fixture per component; ms→µs conversion asserted, including
  the `oldestPendingTaskWaitingTimeUs` allow-list; the three outcomes of §4 each asserted, with
  `events` and an availability-group name both landing in *unhandled* and not in *failed*.
- `HealthCycleGroupingTests` — four component events whose timestamps differ by under a
  millisecond form one cycle; two interleaved series are detected as two.
- `ServerDiagnosticsIngestionTests` — `events_unmapped` falls by exactly the sum of the three new
  counters; they stay mutually exclusive; `IngestionResult` and the CLI line carry them.
- `HealthBlockingReuseTests` — an embedded report reaches `blocking_reports` with
  `source = 'diagnostics'`, carries its fingerprint, and obeys `VerbatimStatementTextAllowed` under
  all four policy combinations.
- `BlockingSourceIsolationTests` — **written before the `source` column exists**, and asserting the
  `Chains()` fabrication case specifically, since a filter alone does not fix it. This is the
  regression guard for §5 and the most important test in the set.
- `BlockingWritePathTests` — `InsertBlockingBatch` survives the migration. This test fails today
  against the revision-1 design, which is why it exists.
- `HealthWaitDeltaTests` — a wait type whose counter rises across cycles yields a delta and a rate,
  not a sum; one that decreases marks a restart and is dropped and counted; one that leaves the top
  list has its actual span reported.
- `HealthQueriesTests` — the coverage block over seeded cycles with a deliberate gap; `byCount` and
  `byDuration` never summed together.
- `HealthDigestTests` — bounded output; an all-clean project yielding an explicit "nothing found";
  a project with no health data at all yielding a different message.

The environment-gated integration test over a real capture follows the `sample/` convention and
skips when absent.

---

## 14. Documentation to update

- `docs/data-model.md` — eight tables, three counters, `blocking_reports.source` and its NULL
  convention, and the ingestion-quality query, which must keep reconciling to `events_read`.
- `docs/cli-reference.md` — `export-health`; the usage line; the `import` section's "Recognized
  events" table and its "anything else is counted as unmapped" sentence, both of which go stale on
  the day this ships; the import summary line's counters.
- `docs/privacy.md` — the unconditional `filePath` disclosure, and a line stating that embedded
  reports pass the same gate as event-sourced ones.
- `docs/blocking.md` — the `source` distinction, everywhere it documents the blocking tables and
  their raw-SQL examples.
- `docs/capture-session.md` — `system_health` is always on; what its ring eviction costs; that a
  folder may contain more than one sampling series.
- `docs/README.md` and the root `README.md` — both carry a table of the command set.
- `.agents/skills/analyzing-xel-workloads/SKILL.md` — never read a health digest without reading
  its coverage block first; the counters are cumulative.
- `CLAUDE.md` — section A says "eight of them" of the CLI commands; section B's counter list.

---

## 15. Open questions

1. **The series-detection heuristic.** Grouping cycles into sampling series by observed spacing is
   inference, not fact. What should the digest do when the spacing is irregular enough that the
   grouping is unreliable — refuse to report interval-scoped metrics, or report them with a
   warning? This design leans to refusing, and it is not settled.
2. **Whether `export-events` should ever export diagnostics snapshots.** §5 says no. Someone
   analysing a blocking incident might want them. Cheap to change later, and stated so that the
   decision is visible.
3. **`state` values beyond `CLEAN` and `WARNING`** — only those two observed, and the string is
   stored verbatim so nothing can be lost, but no test can assert what has not been seen.
4. **The other three positional inserts.** `executions`, `blocking_processes` and
   `deadlock_reports` carry the same trap this change hits on `blocking_reports`. Out of scope
   here; worth a separate task before the next `ADD COLUMN`.
