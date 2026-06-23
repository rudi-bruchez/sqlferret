# Design — Blocking ingestion + AI-oriented digest export (Spec 1)

- **Date:** 2026-06-23
- **Status:** Approved (brainstorming) — pending spec review
- **Scope:** Spec 1 of 2. Spec 2 (MCP server) is deferred and described only as the reuse target.

## 1. Motivation

SQLFerret today ingests **execution events** (`rpc`, `sql_batch`, `statement`) into DuckDB and
analyzes query workload (`top-slow`). It does **not** ingest blocking — `blocked_process_report`
events currently fall through `EventMapper` as `EventClass.Unknown` and are counted `unmapped`.

The immediate driver is a real engagement: a 30 TB SQL Server with chronic **blocking** incidents,
delivered as `blocked_process_report` `.xel` captures (one file ≈ 963 MB). The analytical question is
**locality of contention**: are blocks intra-tenant (isolable by sharding) or on shared/global
resources (not isolable)? Answering this from a 963 MB binary `.xel` by hand is infeasible.

This spec adds (a) ingestion of blocking/deadlock reports, and (b) a **digest export** that turns an
ingested blocking workload into an AI-consumable artifact — **bounded by aggregation**, so it fits an
LLM context regardless of capture size.

## 2. Goals / Non-goals

**Goals**
- Ingest `blocked_process_report` (primary) and `xml_deadlock_report` (light) into DuckDB.
- A reusable **`BlockingDigest`** engine in Core that produces a host-agnostic digest record.
- A CLI command `export-blocking` emitting JSON + markdown (digest, approach B) with a `--full`
  NDJSON escape hatch for small captures.
- Compute the **locality signal** (wait-resource-type breakdown + top contended objects) so the
  intra-tenant vs shared-resource question is *calculated, not guessed*.
- Honor all existing hard invariants (microseconds, redaction-before-disk, nothing silently dropped,
  normalization version, SQL safety, KISS/no-DI).

**Non-goals (Spec 1)**
- MCP server (Spec 2 — reuses `BlockingDigest`).
- Query-workload export (`export-slow`) — the digest layer is designed to generalize later.
- Deep deadlock-graph analysis (deadlocks are ingested + counted + sampled only).
- Resolving `hobt_id`/`KEY` lock resources to table names (needs prod metadata not present in a
  schema-only restore). We keep `object_id` and the object visible in the SQL text.

## 3. Architecture (by Core namespace — follows existing layering)

`Model` ← `Normalization`/`Parameters` ← `Ingestion`/`Storage`/`Analysis` ← (`Cli` host).
No new abstractions beyond data records. Aggregation stays in DuckDB SQL. Formatting stays in hosts.

### 3.1 Model (`Model/`)
Blocking is not an `ExecutionEvent` (no duration/cpu/reads) → new records, existing model untouched.

- `BlockingReport` — `CapturedAt` (DateTimeOffset), `MonitorLoop` (int?), `DatabaseId` (int?),
  `Blocked` (BlockingProcess), `Blocking` (BlockingProcess).
- `BlockingProcess` — `Spid` (int?), `Ecid` (int?), `Status` (string?), `WaitResourceRaw` (string?),
  `WaitResourceType` (enum), `ObjectId` (long?), `HobtId` (long?), `WaitTimeUs` (long?),
  `LockMode` (string?), `IsolationLevel` (string?), `TranCount` (int?), `ClientApp` (string?),
  `HostName` (string?), `LoginName` (string?), `InputBufRaw` (string?), `InputBufFingerprint` (string?).
- `WaitResourceType` enum — `Key, Object, Page, Rid, Database, PageLatch, AppLock, Other`.
- `DeadlockReport` (light) — `CapturedAt`, `VictimSpids` (int[]), `ParticipantSpids` (int[]),
  `GraphXmlRedacted` (string) — the deadlock graph kept as a redacted sample, not shredded.

### 3.2 Parsing (`Ingestion/`)
- `WaitResourceParser` — `string -> (WaitResourceType, dbId?, objectId?, hobtId?)`. Parses the
  documented forms: `KEY: db:hobt (hash)`, `OBJECT: db:object:index`, `PAGE: db:file:page`,
  `RID: …`, `DATABASE: db:resource`, `PAGELATCH …`, `APPLICATION …`. Unknown → `Other`. **This is
  the locality classifier.** Pure, unit-tested per form.
- `BlockingReportParser` — report XML string → `BlockingReport`, via `System.Xml.Linq` (std lib, no
  new dependency). Tolerant: missing attributes → null fields, never throws on well-formed-but-sparse
  reports.
- `DeadlockReportParser` — deadlock XML → `DeadlockReport` (victims + participants + redacted graph).
- Malformed XML → parse failure (counted, see 3.4), never fatal (bare `catch` on this fallback path is
  within the C# baseline).

### 3.3 Ingestion routing (`Ingestion/EventMapper`, `IngestionService`)
- `EventMapper`: recognize `blocked_process_report` and `xml_deadlock_report` by event name; the report
  XML lives in a field (`blocked_process` / `xml_report` — confirm exact field names against XELite on
  the real sample). Surface this as a distinct outcome so `IngestionService` can route, without
  perturbing the existing `ExecutionEvent` path.
- `IngestionService`: for blocking/deadlock events → parse → normalize each `inputbuf` SQL via
  `QueryNormalizer` (Version = 1) → apply `RedactionPolicy` → buffer + `InsertBatch` into the new
  tables. Execution events keep their current path unchanged.

### 3.4 Counters (`IngestionResult`, `IngestionProgress`, `ingestion_runs`)
Extend `IngestionResult` with `Blocking`, `Deadlocks`, `BlockingParseFailures`. Counters stay
**mutually exclusive and exhaustive**: a `blocked_process_report` is counted in `Blocking` (not
`unmapped`); a report whose XML fails to parse is counted in `BlockingParseFailures` (not silently
dropped). Propagate through `IngestionProgress`, `ImportProgressText`, the `ingestion_runs` row, and
the CLI `import` summary line.

### 3.5 Storage (`Storage/DuckDbProject`)
New tables (microseconds throughout):
- `blocking_reports(run_id, report_id, captured_at_us, monitor_loop, database_id)`
- `blocking_processes(report_id, role /*blocked|blocking*/, spid, ecid, status, wait_resource_raw,
  wait_resource_type, object_id, hobt_id, wait_time_us, lock_mode, isolation_level, trancount,
  client_app, host_name, login_name, inputbuf_fingerprint)`
- `deadlock_reports(run_id, report_id, captured_at_us, victim_spids, participant_spids, graph_xml)`
`inputbuf_fingerprint` joins to existing `normalized_queries` (reuse normalization; no duplicate text
store). Redacted/normalized SQL only — raw `inputbuf` is never persisted unless `--redaction off`.

### 3.6 Analysis — the shared digest engine (`Analysis/`)
- `BlockingQueries.cs` — DuckDB SQL rollups (no C# reduction loops):
  - **overview**: report count, distinct chains, capture time range + gaps.
  - **locality**: counts/% by `wait_resource_type`.
  - **top_objects**: most-contended `object_id` / resource.
  - **top_blockers** / **top_blocked**: by normalized statement (lead-blocker = head of chain).
  - **lock_modes**, **isolation_levels**: distributions.
  - **wait_time_distribution**: `quantile_cont` p50/p95 + max (microseconds).
  - **chains**: within a `monitor_loop`, link `blocked.spid → blocking.spid`; chain depth, head
    blockers, victims per blocker.
- `BlockingDigest.cs` — assembles `BlockingDigestResult` (pure record): the rollups **+** N
  representative full reports (redacted SQL) **+** reconstructed chains. A *dominant pattern* is a
  top-K lead-blocker fingerprint (from `top_blockers`); the digest includes up to `--samples N` full
  reports for each, and records how many it omitted per pattern (no silent truncation). Pure data,
  host-agnostic. **This is the unit Spec 2's MCP server reuses verbatim.**
- `BlockingExport.cs` (`--full`) — streams every event as NDJSON (bounded by file size, not context).
  Separate from the digest path.

### 3.7 CLI (`Cli/Program.cs`)
New flat command (matches existing `import` / `top-slow` style):
```
export-blocking --project f.duckdb
                [--format json|md|both]   (default both)
                [--samples N]             (default 5 reports per dominant pattern)
                [--full]                  (NDJSON dump instead of digest)
                [--redaction off|hash|masked|full]
                [--out path]              (default stdout; reject path traversal)
```
Markdown is rendered **in the host** from the same `BlockingDigestResult` (formatting-in-hosts
invariant). `--out` path validated against traversal (SQL-safety analog).

## 4. JSON output schema (the contract — versioned)

```jsonc
{
  "schema_version": 1,
  "meta": { "source", "time_range", "report_count", "deadlock_count",
            "parse_failures", "redaction_mode" },
  "locality": [ { "wait_resource_type", "count", "pct" } ],
  "top_objects": [ { "object_id", "count" } ],
  "top_blockers": [ { "fingerprint", "normalized_sql", "victims", "count" } ],
  "top_blocked":  [ { "fingerprint", "normalized_sql", "count" } ],
  "lock_modes": [ { "mode", "count" } ],
  "isolation_levels": [ { "level", "count" } ],
  "wait_time_distribution": { "p50_us", "p95_us", "max_us" },
  "chains": [ { "monitor_loop", "depth", "head_spid", "victim_spids" } ],
  "samples": [ { /* full redacted BlockingReport */ } ]
}
```
Markdown is a narrative rendering of the same data (overview → locality verdict cue → top blockers →
chains → samples). The schema is stable across CLI and the future MCP server.

## 5. Redaction & PII (health data)

Reports carry SQL with literal PII (patient NIR, birth date). Reuse `RedactionPolicy`.
**Default = normalized SQL + redacted literals.** `--redaction off` is local-only and documented as
emitting raw values. Neither JSON nor markdown contains raw PII unless `off`. Redaction is applied in
`IngestionService` **before** any write (existing hard invariant), so the DuckDB store is already safe;
the exporter never re-derives raw text.

## 6. Error handling

- Malformed report XML → `BlockingParseFailures` counter, continue.
- No blocking events in project → digest with zero counts + explicit "no blocking captured" note,
  exit 0 (not an error).
- `--out` path traversal → reject before writing.
- `--full` on a large project → stream NDJSON, never buffer the whole set.

## 7. Testing

- **Unit** (committable, no PII): `WaitResourceParser` per resource form; `BlockingReportParser` /
  `DeadlockReportParser` on small synthetic XML fixtures; chain reconstruction; digest assembly &
  deterministic ordering; redaction actually applied to sample SQL.
- **Integration**: `[SkippableFact]` over a real blocking `.xel` in `sample/` (gitignored) — skips
  cleanly when absent (CI stays green).

## 8. Invariants honored

Microseconds in Core (`*_us`); redaction before disk; nothing silently dropped (exhaustive counters
incl. `BlockingParseFailures`); `QueryNormalizer.Version = 1` reused; SQL safety (bound params,
allow-listed identifiers, `--out` traversal check); KISS (records + static parsers + DuckDB SQL, no
DI / no repository / no interfaces beyond the existing `IXeEventData`).

## 9. Risks / to confirm during implementation

- **Exact XELite field names** for the report XML (`blocked_process` vs `xml_report`) — verify against
  the real sample before finalizing `EventMapper`.
- **Report XML shape variance** across SQL Server versions — the sample is SQL 2016; parser tolerates
  missing attributes.
- **Sampling representativeness** — `--samples N` per dominant pattern; document that `--full` exists
  when N is insufficient (no silent truncation: digest states how many reports each pattern omitted).

## 10. Spec 2 (deferred — reuse target only)

MCP server as a thin host over `BlockingDigest` + drill-down `BlockingQueries` (list chains, drill a
spid, filter by time window, fetch a blocker's SQL). No extraction/aggregation logic duplicated.
