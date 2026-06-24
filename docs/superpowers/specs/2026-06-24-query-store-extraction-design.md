# Query Store extraction — design

**Date:** 2026-06-24
**Status:** Approved (brainstorming) — ready for implementation plan
**Branch target:** `feat/query-store-extraction` (off `main`)

## Problem

SqlFerret today analyzes a workload captured from `.xel` Extended Events files. SQL Server's
**Query Store** is a richer, already-aggregated source for the same questions: it persists the
queries, their execution plans (as showplan XML — i.e. `.sqlplan`), per-interval runtime metrics,
and per-query wait statistics, directly inside the database. Extracting it lets SqlFerret analyze a
workload without needing a trace capture, and gives a downstream AI the raw material to find
regressions, expensive queries, plan instability, and waits.
We want to have both possibilities to be able to analyze all informations from a server, for a complete audit.

There is no Query Store extraction in SqlFerret today. The only live-server code is
`EstimatedPlanService` (compile-only `SET SHOWPLAN_XML ON`), which establishes the patterns this
feature follows: a `SqlConnection` from a config-supplied connection string, bracket-escaped
`USE [db]`, `.sqlplan` files written to a plans folder, `[SkippableFact]` integration tests.

## Goal

A new `query-store-import` CLI command and `QueryStoreImportService` Core service that connect to a
target database, read its entire Query Store, write each execution plan as a `.sqlplan` file, and
load queries / plans / per-interval runtime stats / wait stats into new DuckDB `qds_*` tables inside
the audit project. The result is a self-contained project a later export/MCP/feature-extraction step
can analyze offline.

## Decisions (locked during brainstorming)

1. **Scope: everything.** Pull the entire Query Store — every query, plan, runtime-stats interval,
   and wait-stats row. No top-N / regression / time-window selection; narrowing is deferred to the
   later query/export layer.
2. **Plan XML: one `.sqlplan` file per plan**, written during extraction; DuckDB holds a path index
   (`qds_plans.sqlplan_path`), not the XML. Files live under `<project>/plans/qds/<plan_id>.sqlplan`
   — a `qds/` subfolder so Query Store `plan_id`-named files never collide with the `.xel`
   estimated plans `EstimatedPlanService` writes into the plans root.
3. **Runtime stats: full per-interval fidelity** — one `qds_runtime_stats` row per
   `(plan, runtime_stats_interval)`, not collapsed.
4. **Plan-XML feature parsing is deferred** to its own later spec (it can read the `.sqlplan` files
   offline, no re-query). This spec is raw extraction only.
5. **`--plans` / `--no-plans` flag (default on)** controls whether `.sqlplan` files are written.
   When off, `qds_plans` still gets full plan metadata; `sqlplan_path` is NULL and
   `plan_written = false`. When plans are written and `redaction != off`, the command prints a
   one-line stderr warning that `.sqlplan` files may contain literal values outside redaction's
   scope (see Redaction below).

## Architecture

New Core service `QueryStoreImportService` in `SqlFerret.Core.Server`, next to `EstimatedPlanService`.
It is constructed with the data it needs (a connection string, the target `DuckDbProject`, the plans
folder, options) and performs straight ETL: read-only `SELECT`s against `sys.query_store_*`, write
`.sqlplan` files, batch-insert `qds_*` rows. No new abstraction, no DI — a primary-constructor service
(KISS).

Dependency layering is unchanged: `Server` already depends on `Storage` + `Model`; this service adds
a `Microsoft.Data.SqlClient` dependency it shares with `EstimatedPlanService`. Host wiring
(`AuditProject` → connection string, plans folder, DuckDB) lives in the CLI.

```
CLI query-store-import
   └─ AuditProject.OpenOrCreate(dir)        // existing: db + plans folder + config/.env
        └─ QueryStoreImportService(connStr, project.OpenDb(), project.PlansFolder, options)
             ├─ read sys.query_store_* (read-only)         → batch insert qds_* (DuckDB)
             └─ write <plansFolder>/qds/<plan_id>.sqlplan  (when --plans)
```

## Connection & target database

- The connection string comes from `project.Config.ConnectionString` (loaded from
  `sqlferret.config.json` `server.connectionString` with `${ENV}` interpolation; secrets stay in
  `.env`). An optional `--conn <value>` flag overrides it with a literal connection string for
  ad-hoc use; documented as "prefer `.env`; do not commit secrets passed inline".
- Query Store catalog views are **per database**. The target database is selected by the connection
  string's `Initial Catalog`, or overridden by `--database <db>` which issues a bracket-escaped
  `USE [db]` (escaping `]` → `]]`, exactly as `EstimatedPlanService.CaptureAsync` does).
- **Precondition check:** before extracting, query `sys.database_query_store_options.actual_state_desc`.
  If it is `OFF` or `ERROR` (or the view is absent), fail with a clean message
  (`"Query Store is not enabled on database <db> (actual_state=OFF)"`) and a non-zero exit — never a
  raw stack trace. `SqlException` (login/connect failures), and missing/old catalog views are caught
  and presented cleanly, per the error-handling lesson from the audit-project review.

## DuckDB schema (new `qds_*` tables)

All created via `CREATE TABLE IF NOT EXISTS` in `DuckDbProject` (same place as the existing tables),
each carrying `run_id` so multiple extractions accumulate (append-by-run, like `ingestion_runs`).
Source binary columns (`query_hash`, `query_plan_hash`) are stored as hex `TEXT`. Timestamps from QS
(`datetimeoffset`) are stored as `TIMESTAMP`.

### `qds_runs` — one row per extraction (provenance + exhaustive counters)
```
run_id BIGINT PRIMARY KEY, server_name TEXT, database_name TEXT, captured_at TIMESTAMP,
sql_server_version TEXT, query_store_actual_state TEXT, query_store_desired_state TEXT,
wait_stats_available BOOLEAN, plans_requested BOOLEAN,
queries_count BIGINT, query_text_count BIGINT, plans_count BIGINT,
runtime_stat_rows BIGINT, wait_stat_rows BIGINT,
plan_files_written BIGINT, plan_write_failures BIGINT,
extractor_version INTEGER
```
`BeginRun` inserts the row with zeroed counters; `FinishRun` updates them — same shape as the
existing `ingestion_runs` `BeginRun`/`FinishRun`. Counters are mutually exclusive and exhaustive
(every plan is either written or a counted failure; nothing silently dropped).

### `qds_query_text` — `sys.query_store_query_text`
```
run_id BIGINT, query_text_id BIGINT, query_sql_text TEXT,
is_part_of_encrypted_module BOOLEAN, has_restricted_text BOOLEAN
```
Primary key `(run_id, query_text_id)`. Separate table because multiple queries share a text id.

### `qds_queries` — `sys.query_store_query` (+ resolved object name)
```
run_id BIGINT, query_id BIGINT, query_text_id BIGINT, object_id BIGINT, object_name TEXT,
query_hash TEXT, query_parameterization_type TEXT, is_internal_query BOOLEAN,
count_compiles BIGINT, last_execution_time TIMESTAMP
```
Primary key `(run_id, query_id)`. `object_name` is `schema.object` resolved via
`OBJECT_SCHEMA_NAME`/`OBJECT_NAME` (NULL for ad-hoc).

### `qds_plans` — `sys.query_store_plan`
```
run_id BIGINT, plan_id BIGINT, query_id BIGINT, query_plan_hash TEXT,
engine_version TEXT, compatibility_level INTEGER,
is_forced_plan BOOLEAN, is_trivial_plan BOOLEAN, is_parallel_plan BOOLEAN,
force_failure_count INTEGER, last_force_failure_reason_desc TEXT,
count_compiles BIGINT, last_execution_time TIMESTAMP,
sqlplan_path TEXT, plan_written BOOLEAN
```
Primary key `(run_id, plan_id)`. `sqlplan_path` is the project-relative path
(`plans/qds/<plan_id>.sqlplan`) when written, else NULL.

### `qds_runtime_stats` — `sys.query_store_runtime_stats` ⋈ `runtime_stats_interval` (per interval)
```
run_id BIGINT, runtime_stats_id BIGINT, plan_id BIGINT, runtime_stats_interval_id BIGINT,
interval_start_time TIMESTAMP, interval_end_time TIMESTAMP, execution_type TEXT,
count_executions BIGINT,
-- for each metric below: avg_, min_, max_, last_, stdev_ (5 columns)
duration_us, cpu_time_us, clr_time_us,                       -- microseconds (QS native; no conversion)
logical_io_reads, logical_io_writes, physical_io_reads,      -- page counts
rowcount,                                                    -- rows
dop,                                                         -- degree of parallelism
query_max_used_memory_8kb_pages,                             -- 8KB pages (unit-explicit name)
tempdb_space_used_8kb_pages,                                 -- 8KB pages
log_bytes_used                                               -- bytes
```
Primary key `(run_id, runtime_stats_id)`. Concretely each metric expands to its five aggregates,
e.g. `avg_duration_us BIGINT, min_duration_us BIGINT, max_duration_us BIGINT, last_duration_us BIGINT,
stdev_duration_us DOUBLE` (stdev is `DOUBLE`; the rest `BIGINT`). The implementation plan enumerates
the full column list; the metric set and the avg/min/max/last/stdev convention are fixed here.

**Units:** Query Store stores `avg_duration`, `avg_cpu_time`, `avg_clr_time` in **microseconds**, so
they map to `*_us` with no conversion (honors the µs-in-Core invariant). Memory and tempdb are 8 KB
pages and IO are page counts — stored raw with unit-explicit names, never silently converted.

### `qds_wait_stats` — `sys.query_store_wait_stats` (SQL Server 2017+ / Azure)
```
run_id BIGINT, wait_stats_id BIGINT, plan_id BIGINT, runtime_stats_interval_id BIGINT,
wait_category TEXT, execution_type TEXT, count_executions BIGINT,
total_query_wait_time_us BIGINT,
avg_query_wait_time_us BIGINT, min_query_wait_time_us BIGINT,
max_query_wait_time_us BIGINT, stdev_query_wait_time_us DOUBLE
```
Primary key `(run_id, wait_stats_id)`. **Query Store wait times are milliseconds**, converted
`×1000` to microseconds at the ingestion boundary (the only conversion in this feature). Populated
only when the view exists; otherwise `qds_runs.wait_stats_available = false` and the table stays
empty for that run.

## Extraction flow

`QueryStoreImportService.Import(progress)` (synchronous over an open connection; async optional):

1. Open `SqlConnection`; if `--database` given, `USE [db]` (bracket-escaped).
2. Read `@@SERVERNAME`, `SERVERPROPERTY('ProductVersion')`, and
   `sys.database_query_store_options` (`actual_state_desc`, `desired_state_desc`). Abort cleanly if
   not `READ_WRITE`/`READ_ONLY`.
3. Detect wait-stats availability (the `sys.query_store_wait_stats` view exists, SQL 2017+).
4. `BeginRun` → `qds_runs` row (zeroed counters).
5. Stream and batch-insert in dependency order: `qds_query_text` → `qds_queries` → `qds_plans`
   (writing `plans/qds/<plan_id>.sqlplan` per plan when `--plans`, counting writes and failures) →
   `qds_runtime_stats` → `qds_wait_stats` (when available). Batched DuckDB transactions, same
   `InsertBatch`/bound-`$param` pattern as the existing inserts.
6. `FinishRun` → write final counters.
7. Report a result record (`run_id` + all counts) the CLI prints; progress is streamed to a stderr
   gauge during steps 5 (queries/plans/intervals processed), like `.xel import`.

Plan-file write failures (permission, disk full, bad XML) are caught per plan, counted in
`plan_write_failures`, and `plan_written=false` for that row — never fatal.

## Redaction

The project invariant is "redaction before any parameter value is written to disk." Query Store's
stored query text is engine-**parameterized** for parameterized queries (literals already replaced by
`@p`); ad-hoc statements retain their literals. Showplan XML can embed literal parameter values
(`ParameterCompiledValue`). This spec does not attempt to scrub literals out of plan XML — consistent
with the existing `EstimatedPlanService`, which writes raw showplan to `.sqlplan`.

Control and disclosure instead of silent behavior:

- `--no-plans` skips writing `.sqlplan` files entirely (metadata-only extraction) for the
  privacy-conscious or to avoid the file volume.
- When plans **are** written and `project.Config.RedactionPolicy != off`, the command prints a
  one-line stderr warning: `"warning: --plans writes raw showplan XML; .sqlplan files may contain
  literal values not covered by redaction (policy=<x>). Use --no-plans to skip."`
- A redaction pass over plan XML is explicitly deferred to the plan-feature-extraction spec.

`qds_query_text.query_sql_text` is stored as Query Store provides it (already parameterized where the
engine parameterized it). The redaction policy governs execution-time parameter *values*, which Query
Store does not expose as a separate stream, so there is no parameter-value sink to redact here.

## Hard-invariant check

- **Microseconds in Core** — runtime duration/cpu/clr are QS-native µs (stored `*_us`, no
  conversion); wait stats ms→µs at the ingestion boundary; memory/IO stored raw with unit-explicit
  names. No conversion elsewhere.
- **Secrets in `.env` only** — connection string from config/`.env` via `${ENV}`; `--conn` override
  documented as env-preferred; never logged or committed.
- **Nothing silently dropped** — every plan is written or a counted failure; `is_internal_query`
  rows are kept and flagged; all totals recorded in `qds_runs`; counters exhaustive.
- **SQL safety** — source queries are static `SELECT`s; only `--database` is interpolated, as a
  bracket-escaped identifier (the `EstimatedPlanService` pattern). DuckDB inserts use bound `$params`.
  `.sqlplan` filenames are the integer `plan_id` (no traversal) under a fixed `plans/qds/` subfolder.
- **KISS** — one primary-constructor ETL service, plain row handling, no interface/DI; all later
  aggregation stays in DuckDB SQL (query/export layer), not C# reduction here.

## Testing

- **Live integration (`[SkippableFact]`):** gated on `SQLFERRET_TEST_CONN` pointing at a database
  with Query Store enabled; skips cleanly when absent so CI stays green (same gating as
  `EstimatedPlanServiceTests`). Runs a full `Import`, asserts `qds_runs` counters > 0, a
  `qds_runtime_stats` row exists, and (with `--plans`) a `plans/qds/<id>.sqlplan` file was written;
  also asserts a clean failure when pointed at a database with Query Store off.
- **Headless units (no server):**
  - `DuckDbProject` `qds_*` schema + insert methods with fabricated rows (like
    `DuckDbProjectInsertTests`): column counts/order, `BeginRun`/`FinishRun` counter round-trip.
  - The wait-stats ms→µs conversion.
  - `.sqlplan` path construction + path-safety (plan_id is integer; fixed subfolder).
  - `qds_runs` counter exhaustiveness (written + failures == plans_count).
  - `--plans`/`--no-plans` effect on `sqlplan_path`/`plan_written` (can be exercised with a fake
    plan-writer seam if the file write is factored, else covered by the live test).

## Out of scope (deferred to their own specs)

- Plan-XML feature parsing (MissingIndexes, warnings, operators) → reads the `.sqlplan` files offline.
- AI export pack; MCP host.
- Selective extraction (top-N / regressed / time-window) — superseded by "everything".
- Incremental/delta extraction — each run is a full snapshot appended by `run_id`.
- TUI surface for Query Store data.
