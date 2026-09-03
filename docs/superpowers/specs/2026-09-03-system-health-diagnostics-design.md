# Design — Ingesting `sp_server_diagnostics_component_result` from a `system_health` capture

Status: proposed
Date: 2026-09-03
Scope: `SqlFerret.Core` (Ingestion, Storage, Analysis) + the CLI host

---

## 0. Evidence base

Everything in sections 1 and 3 was measured against a real `system_health` capture — eight
`.xel` files, about 35 MB, 14 h 15 min of wall clock — not recalled from documentation. The
capture is production data and is not part of this repository; only element names, attribute
names and aggregate counts are reproduced here. No value from it appears in this document, and
none may appear in a test fixture.

Where a claim could not be measured, it says so.

---

## 1. Problem

`sqlferret import` on a `system_health` capture produces **nothing**. Measured on the smaller of
the two captures available: `read=5610 mapped=0 unmapped=5610`, every counter else zero.

The reason is narrow. `EventMapper` routes three families — workload completions, blocking and
deadlock reports, plan profiles — and `system_health` contains almost none of them. What it does
contain, `sp_server_diagnostics_component_result`, carries an XML payload per component that no
part of the tool reads.

That payload is the only source in the project for a class of facts no `.xel` workload trace, no
Query Store snapshot and no execution plan can supply: worker and pending-task pressure, the
server's own top waits, memory pressure and out-of-memory periods, non-yielding schedulers,
spinlock backoffs, and long I/O.

Two secondary observations from the same capture shape the design more than the payload does:

- **The session is an evicting ring, and it is usually evicted by something dull.** In the larger
  capture, 107 295 of 110 387 events (97.2 %) are `security_error_ring_buffer_recorded`. The
  diagnostics history that survives is 336 cycles over 14 h 15 min — one roughly every 2.5
  minutes, not the 5-second cadence the event's own emission rate suggests.
- **The interesting nodes are usually empty.** Across 336 cycles, `blockingTasks` was non-empty
  14 times and `pendingTasks` 4 times. `topWaits` was populated in all 336, `cpuIntensiveRequests`
  in 172, `longestPendingRequests` in 10.

A digest that ranks head blockers and stays silent about coverage would therefore be honest on
paper and misleading in use. Coverage reporting is a feature of this work, not a footnote.

---

## 2. Scope

### In

- Routing and parsing `sp_server_diagnostics_component_result` for its four components:
  `QUERY_PROCESSING`, `RESOURCE`, `SYSTEM`, `IO_SUBSYSTEM`.
- Persisting them relationally in the existing project directory.
- Feeding the `blocked-process-report` documents embedded in `QUERY_PROCESSING/blockingTasks`
  into the existing blocking tables.
- A new CLI command producing a bounded, ranked digest.
- Two new `ingestion_runs` counters.

### Out, deliberately

The flat `system_health` events stay unmapped, and this is a choice rather than an oversight.
Measured counts in the larger capture:

| Event | Count | Shape |
|---|---|---|
| `security_error_ring_buffer_recorded` | 107 295 | flat |
| `scheduler_monitor_system_health_ring_buffer_recorded` | 1 676 | flat |
| `connectivity_ring_buffer_recorded` | 62 | flat |
| `error_reported` | 6 | flat |
| `wait_info` | 1 | flat |

None carries nested XML — an assumption this design started with and the capture refuted. They
belong to a later, much smaller piece of work through `EventMapper`'s ordinary path, and mixing
them in here would put two unrelated mechanisms in one spec.

`xml_deadlock_report` is already routed and needs no work: the larger capture contains three, and
they ingest today.

Also out: `sys.dm_os_ring_buffers` (a server-connected source, closer to `query-store-import`),
and any TUI surface.

---

## 3. What the payload actually contains

`sp_server_diagnostics_component_result` exposes three fields: `component`, `state`, `data`.

`component` is upper-case — `QUERY_PROCESSING`, `RESOURCE`, `SYSTEM`, `IO_SUBSYSTEM`. `state` is a
**string**, not the integer the DMV documentation suggests: `CLEAN` and `WARNING` were observed
(4 `WARNING` in 336 cycles, all `QUERY_PROCESSING`). `data` is an XML document whose root differs
per component.

### 3.1 `QUERY_PROCESSING` → `<queryProcessing>`

Scalar attributes: `maxWorkers`, `workersCreated`, `workersIdle`, `pendingTasks`,
`oldestPendingTaskWaitingTime`, `hasUnresolvableDeadlockOccurred`,
`hasDeadlockedSchedulersOccurred`, `trackingNonYieldingScheduler`, `tasksCompletedWithinInterval`.

Repeated children:

- `topWaits/{nonPreemptive,preemptive}/{byCount,byDuration}/wait` with
  `waitType`, `waits`, `averageWaitTime`, `maxWaitTime`. Four independent rankings.
- `cpuIntensiveRequests/request` with `sessionId`, `requestId`, `command`, `cpuTimeMs`,
  `cpuUtilization`, `taskAddress`.
- `pendingTasks/entryPoint` with `name`, `count`.
- `blockingTasks/blocked-process-report` — see section 5.

### 3.2 `RESOURCE` → `<resource>`

Scalars: `isAnyPoolOutOfMemory`, `outOfMemoryExceptions`, `processOutOfMemoryPeriod`,
`lastNotification`. Repeated: `memoryReport` (`name`, `unit`) containing `entry`
(`description`, `value`) — already a name/value structure in the source.

### 3.3 `SYSTEM` → `<system>`

Scalars only, fifteen observed: `sqlCpuUtilization`, `systemCpuUtilization`, `spinlockBackoffs`,
`sickSpinlockType`, `sickSpinlockTypeAfterAv`, `nonYieldingTasksReported`, `latchWarnings`,
`pageFaults`, `BadPagesDetected`, `BadPagesFixed`, `LastBadPageAddress`,
`isAccessViolationOccurred`, `writeAccessViolationCount`, `intervalDumpRequests`,
`totalDumpRequests`.

Note the inconsistent casing in the source (`BadPagesDetected` beside `pageFaults`). It is
SQL Server's, and it is one of the reasons section 6 stores scalars as name/value rather than as
columns.

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

Strict equality, not `Contains`, for the reason already recorded on `IsPlanProfile`: a neighbouring
event name must not be routed here by accident.

`IngestionService.Ingest` grows a branch beside the blocking and plan-profile ones. The branch
parses with a new `ServerDiagnosticsParser` (in `Ingestion`, beside `BlockingReportParser`), buffers
the result, and flushes on `BatchSize` like the others.

`ServerDiagnosticsParser.TryParse(component, state, xml, capturedAt)` returns a
`ServerDiagnosticsSample?` — `null` on malformed XML or an unrecognised component, which increments
`server_diagnostics_parse_failures`. It never throws on bad input: a `catch` returning `null` on a
parse failure is the deliberate fallback path the project already allows, commented at the site.

An unrecognised component is a parse failure rather than a silent skip. A future SQL Server adding
a fifth component must show up in a counter, not vanish.

### Counters

Two new columns on `ingestion_runs`, added by the existing `ALTER TABLE … ADD COLUMN IF NOT EXISTS`
migration block:

- `events_server_diagnostics BIGINT`
- `server_diagnostics_parse_failures BIGINT`

They stay mutually exclusive with the existing counters and with each other: an event is counted in
exactly one. `events_unmapped` drops by exactly `events_server_diagnostics +
server_diagnostics_parse_failures` for a given capture, which is a testable invariant.

---

## 5. `blockingTasks`: reuse, and the cost of reuse

### The measurement

The `blockingTasks` node contains `<blocked-process-report monitorLoop="…">` documents — the same
root element, and the same `blocked-process` / `blocking-process` / `process` / `executionStack` /
`inputbuf` structure, that `BlockingReportParser` already parses for the `blocked_process_report`
event.

Fed the 69 embedded documents from the larger capture, the existing parser returned 69 reports,
0 failures, all 69 carrying an input buffer. This is measured, not assumed.

### The decision

Embedded reports are parsed by `BlockingReportParser` and stored in the existing
`blocking_reports` / `blocking_processes` tables. They inherit the wait-resource parser, the
input-buffer fingerprint that joins them to `normalized_queries`, the existing blocking digest, and
— importantly — the privacy gate repaired in 0.2.0, with no new code on any of those paths.

### The cost, and the column that pays it

The two sources are not the same measurement and must not be summed.

A `blocked_process_report` event fires when a block exceeds `blocked process threshold`. A
`blockingTasks` entry is a snapshot of whatever was blocking at the instant the diagnostics cycle
ran — roughly every 2.5 minutes in the observed capture. Counting them together would let a
long-running block be counted once by one mechanism and repeatedly by the other, and any
`count(*)`-based ranking in the digest would silently favour whichever source sampled more often.

`blocking_reports` therefore gains:

```sql
ALTER TABLE blocking_reports ADD COLUMN IF NOT EXISTS source TEXT;
```

`'event'` for a `blocked_process_report`, `'diagnostics'` for an embedded one. NULL means a run
imported before this column existed, and must be read as `'event'` — the same convention
`sql_text_policy` already uses.

**Every existing blocking query and the blocking digest must filter or group on `source`.** This is
the part of the change that touches working code, and it is where a regression would hide. The
implementation plan must treat "no existing blocking query silently mixes the two sources" as an
explicit, tested requirement rather than a consequence.

A second measured caveat belongs in the digest's output: of the 69 embedded reports, only **3**
carried a wait resource the parser could type. The embedded snapshots are poorer than
threshold-triggered reports, and a digest that ranks by wait-resource type will show them as
`Other` almost always.

### The alternative, and why not

A separate `health_blocking_*` pair of tables would remove all risk to the existing blocking
queries. It would also duplicate the parser's output shape, the fingerprint join, the digest, and
the privacy gate — four things that are currently correct in exactly one place. The `source` column
concentrates the risk in one reviewable predicate instead of spreading a copy of the model.

This is the decision in this document most worth attacking in review.

---

## 6. Schema

Seven new tables. `sample_id` is assigned by the same monotonic-id mechanism the project already
uses for `execution_id` and `report_id`.

```sql
CREATE TABLE IF NOT EXISTS health_samples (
  sample_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP,
  component TEXT, state TEXT);

CREATE TABLE IF NOT EXISTS health_metrics (
  sample_id BIGINT, name TEXT, value_num DOUBLE, value_text TEXT);

CREATE TABLE IF NOT EXISTS health_waits (
  sample_id BIGINT, preemptive BOOLEAN, ranking TEXT,
  wait_type TEXT, waits BIGINT, avg_wait_us BIGINT, max_wait_us BIGINT);

CREATE TABLE IF NOT EXISTS health_cpu_requests (
  sample_id BIGINT, session_id INTEGER, request_id INTEGER, command TEXT,
  cpu_time_us BIGINT, cpu_utilization DOUBLE);

CREATE TABLE IF NOT EXISTS health_pending_tasks (
  sample_id BIGINT, entry_point TEXT, task_count BIGINT);

CREATE TABLE IF NOT EXISTS health_pending_io (
  sample_id BIGINT, duration_us BIGINT, file_path TEXT, handle TEXT, offset_bytes BIGINT);
-- offset_bytes, not offset: OFFSET is reserved in DuckDB and would need quoting at every use.

CREATE TABLE IF NOT EXISTS health_memory_entries (
  sample_id BIGINT, report_name TEXT, unit TEXT, description TEXT, value_num DOUBLE);
```

`ranking` holds `byCount` or `byDuration`; with `preemptive` it identifies which of the four
`topWaits` lists a row came from.

### Why scalars are name/value

Every scalar attribute of every component goes to `health_metrics`, keyed by its source name
(`maxWorkers`, `sqlCpuUtilization`, `totalLongIos`, …), numeric in `value_num` when it parses as a
number and in `value_text` otherwise.

Typed columns per component would read better and would be wrong sooner. The attribute set varies
by SQL Server version — `BadPagesDetected` and `writeAccessViolationCount` are recent additions —
and a fixed column list drops an unknown attribute silently, which the project's "nothing silently
dropped" invariant forbids. Name/value keeps every attribute a future version adds, at the cost of
a pivot in any query that wants several at once.

The four repeated nodes stay relational because the digest ranks on them, and ranking in
name/value would mean a pivot inside every ordering.

### Units

The source reports milliseconds: `averageWaitTime`, `maxWaitTime`, `cpuTimeMs`, and
`pendingRequest/@duration`. Core stores microseconds. Conversion happens in
`ServerDiagnosticsParser`, and every column carrying a duration is named `_us`, per the standing
invariant. `cpu_utilization` and the memory entries are not durations and keep their source unit;
`health_memory_entries.unit` records what that unit was.

---

## 7. Privacy

Three of the new paths carry data that must go through the existing gate rather than beside it.

- **`blockingTasks`** — the embedded reports carry `inputbuf`, `loginname`, `hostname` and
  `clientapp`. They are prepared by `IngestionService.PrepareProc`, unchanged, so they inherit
  `VerbatimStatementTextAllowed`, the input-buffer normalization, and the QI-collapse fix. No new
  policy, no new gate. This is a further argument for the reuse decision in section 5.
- **`cpuIntensiveRequests/@command`** — a command *class* (`SELECT`, `BACKUP DATABASE`), not
  statement text. Stored as-is. If a future SQL Server puts statement text there, the value must be
  routed through the sanitizer; the plan should include an assertion that pins the observed shape
  so the change is noticed.
- **`longestPendingRequests/@filePath`** — a server-side file path, which discloses instance and
  database layout. It is not statement text and `--sanitize-sql-text` does not cover it. This spec
  stores it verbatim and documents it in `docs/privacy.md`'s table of what a project holds. Adding
  a policy for it is deliberately out of scope; naming it is not.

`health_metrics` holds only counters and flags from SQL Server's own diagnostics, no user data.

---

## 8. Provenance and versioning

No new version constant. `QueryNormalizer.Version` and `SqlTextSanitizer.Version` are untouched:
nothing here changes normalization, classification or the sanitizer's output.

The `source` column on `blocking_reports` is the one provenance addition, and it is a column rather
than a version because it distinguishes rows within a run, not runs from each other.

---

## 9. `export-health`

A new CLI command, shaped like `export-blocking`: bounded, ranked, JSON and Markdown, written to a
file or to stdout.

```text
sqlferret export-health --project <dir> [--format json|md|both] [--out <file>] [--top <n>]
```

Aggregation lives in DuckDB SQL, in a new `HealthQueries` class beside `BlockingQueries`, with a
`HealthDigest` assembling the bounded result — the structure `BlockingQueries`/`BlockingDigest`
already uses.

The digest opens with **coverage**, before any finding:

- first and last diagnostics sample, wall-clock span, cycle count, and the median interval between
  cycles;
- the largest gap between consecutive cycles;
- the share of the capture's events that are not diagnostics.

On the measured capture that block would read: 336 cycles over 14 h 15 min, median interval ~2.5
min, and 98.8 % of the events spent on something other than diagnostics, 97.2 % of the
whole file on one flooded ring buffer. A reader who sees that knows what the
rest of the digest is worth. A reader who does not will over-read it.

Then, ranked and bounded:

1. Cycles whose `state` is not `CLEAN`, by component.
2. Top waits aggregated across cycles, from `health_waits`, separately for the four rankings —
   never summed across `byCount` and `byDuration`, which count different things.
3. Memory pressure: `outOfMemoryExceptions`, `processOutOfMemoryPeriod`, `isAnyPoolOutOfMemory`,
   and the memory-report entries that moved most across the window.
4. Stability signals from `SYSTEM`: non-yielding tasks, spinlock backoffs, latch warnings, dump
   requests, access violations — reported when non-zero, and omitted when zero rather than listed
   as a wall of zeros.
5. Worker pressure: `pendingTasks`, `oldestPendingTaskWaitingTime`, `workersIdle` against
   `maxWorkers`.
6. I/O: `totalLongIos` / `intervalLongIos` over time, and the worst pending requests.
7. Blocking observed in diagnostics cycles — from `blocking_reports WHERE source = 'diagnostics'`,
   labelled as snapshots and never mixed with threshold-triggered reports.

---

## 10. What this promises, and what it does not

To be written into `docs/` and repeated in the digest's own preamble.

**It gives** the server's own view of itself over the window the ring still holds: what it was
waiting on, whether it was short of memory or workers, whether anything went non-yielding, and what
was blocking at each sampling instant.

**It does not give** a workload profile. The cadence is minutes, the history is whatever the ring
has not yet evicted, and the sampling is blind to everything between two cycles. A statement that
ran badly for ninety seconds between cycles leaves no trace here. For "which query shape costs the
server", the `.xel` workload capture and Query Store remain the sources; this complements them and
replaces neither.

**It is silent by construction on a healthy server.** `blockingTasks` was non-empty in 14 of 336
cycles and `pendingTasks` in 4. An empty finding list is a normal, informative result, and the
digest must present it as such rather than as a lack of data.

---

## 11. Architecture

No new dependency edge. `Ingestion` already depends on `Model` and `Normalization`;
`ServerDiagnosticsParser` adds nothing. `Analysis` already reads DuckDB. The CLI host gains one
command in the existing `switch`.

No new abstraction: `ServerDiagnosticsParser` is a static class like `BlockingReportParser`,
`HealthQueries` a primary-constructor class like `BlockingQueries`. There is no second
implementation of anything here, so there is no interface.

Parsing uses `System.Xml.Linq`, as `BlockingReportParser` does. No new package.

---

## 12. Performance

Diagnostics events are rare — 1 344 in a 35 MB capture, against 107 295 of a single flat event —
so their parse cost is irrelevant next to the existing hot path. The XML documents are small: the
largest observed is a `QUERY_PROCESSING` cycle with a populated `blockingTasks`, and even those
parse in the same pass as everything else.

One `XElement.Parse` per diagnostics event, no repetition of the three-parse problem `docs/
development.md#known-gaps` records for the workload path.

---

## 13. Testing

Fixtures are synthetic and anonymous, in the project's established vocabulary (`SampleApp`,
`AppSchema`, `WidgetRecalc`, `@WidgetId`, `@GadgetCode`). **No fragment of the real capture may be
copied into a test**, including file paths, host names, login names and wait-resource strings. The
element and attribute names in section 3 are SQL Server's schema and are safe; everything between
the tags is not.

- `ServerDiagnosticsParserTests` — one fixture per component; unit conversion from milliseconds
  asserted explicitly; an unknown component and malformed XML both returning `null`.
- `ServerDiagnosticsIngestionTests` — the counter invariant: for a fixture stream,
  `events_unmapped` falls by exactly the number of diagnostics events routed, and the counters stay
  mutually exclusive.
- `HealthBlockingReuseTests` — an embedded `blocked-process-report` reaches `blocking_reports` with
  `source = 'diagnostics'`, carries its input-buffer fingerprint, and obeys
  `VerbatimStatementTextAllowed` under all four combinations of the two policies.
- `BlockingSourceIsolationTests` — the existing blocking queries and digest do not mix the two
  sources. This is the regression guard for section 5's risk, and it is the test that must exist
  before the `source` column is written.
- `HealthQueriesTests` — coverage block computed from seeded samples with a deliberate gap;
  `byCount` and `byDuration` never summed.
- `HealthDigestTests` — bounded output, and an all-clean project yielding an explicit "nothing
  found" rather than an empty document.

The environment-gated integration test over a real `system_health` capture follows the existing
`sample/` convention and skips when absent, like `XelReaderTests`.

---

## 14. Documentation to update

- `docs/data-model.md` — seven tables, the two counters, `blocking_reports.source` and its NULL
  convention.
- `docs/cli-reference.md` — `export-health`, and the ninth command in the usage line.
- `docs/privacy.md` — the `filePath` disclosure, and a line stating that embedded reports go
  through the same gate as event-sourced ones.
- `docs/capture-session.md` — `system_health` is always on and needs no session; what its ring
  eviction costs, and that a flooded ring is the normal case rather than a misconfiguration.
- `.agents/skills/analyzing-xel-workloads/SKILL.md` — the coverage trap: never read a health
  digest without reading its coverage block first.
- `CLAUDE.md` — the counter list in the "nothing silently dropped" invariant.

---

## 15. Open questions

1. **`blocking_reports.source` versus separate tables** — argued in section 5, decided in favour of
   the column. The strongest counter-argument is that it puts a filter obligation on every existing
   blocking query, including ones written later by someone who has not read this document. A
   reviewer who thinks that obligation will not hold should say so.
2. **Should `export-health` refuse to run when coverage is below a threshold?** A digest computed
   over three surviving cycles is arithmetically valid and practically worthless. Refusing is
   paternalistic; reporting coverage and continuing is what section 9 proposes. Not settled.
3. **Cross-source correlation** — joining diagnostics windows to slow executions captured in the
   same interval was raised and deferred. It needs both captures to overlap, which nothing
   guarantees. Out of scope here; noted so the schema does not foreclose it (`captured_at` on
   `health_samples` is what a later join would use).
4. **`state` values beyond `CLEAN` and `WARNING`** — only those two were observed. The parser stores
   the string verbatim rather than mapping to an enum, so an unobserved value cannot be lost, but no
   test can assert what has not been seen.
