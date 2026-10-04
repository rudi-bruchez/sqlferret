# Data model

The project file `sqlferret.duckdb` is an ordinary DuckDB database. There is no proprietary
wrapper, no encryption and no ORM. Open it with the DuckDB CLI, DBeaver, Python, R, or anything
else that speaks DuckDB, and query it directly.

```bash
duckdb ./audits/prod-2026-08/sqlferret.duckdb
```

You do not need any of them, though: `query` runs the same SQL through the tool itself, opens the
database read-only, and formats durations for you. It is the recommended path, and the only one
that does not require a second DuckDB installation:

```bash
sqlferret query --project ./audits/prod-2026-08 --format md \
    --sql "SELECT statement_kind, count(*) n FROM normalized_queries GROUP BY 1 ORDER BY n DESC"
```

See [cli-reference.md#query](cli-reference.md#query) — in particular the default row limit and the
way duration formatting keys off the output column name.

Schema creation is idempotent (`CREATE TABLE IF NOT EXISTS` plus `ADD COLUMN IF NOT EXISTS`
migrations), so opening an older project with a newer build upgrades it in place.

## Two rules that apply everywhere

1. **All durations, CPU times and wait times are microseconds.** Column names carry a `_us`
   suffix and the type is `BIGINT`. Formatting to milliseconds or seconds happens in the hosts,
   never in the database. Divide by `1e3` for ms, `1e6` for s.
2. **Hashes are stored bare**, without a `0x` prefix, so they remain joinable.

---

## Workload tables

### `ingestion_runs`

One row per `import`. The provenance and quality record for everything that run produced.

| Column | Type | Notes |
|---|---|---|
| `run_id` | BIGINT PK | |
| `source_path` | TEXT | The path given to `import` |
| `files_count`, `bytes_total` | INTEGER / BIGINT | |
| `started_at`, `finished_at` | TIMESTAMP | |
| `events_read` | BIGINT | Total events pulled from the files |
| `events_mapped` | BIGINT | Became `executions` rows |
| `events_unmapped` | BIGINT | Event types SQLFerret does not model |
| `events_cleaned` | BIGINT | Dropped by an ingest-stage filter |
| `tokenize_failures` | BIGINT | Statement could not be tokenized; a fallback normalization was used |
| `events_blocking`, `events_deadlocks` | BIGINT | |
| `blocking_parse_failures` | BIGINT | |
| `events_plan_profiles`, `plan_parse_failures`, `plan_write_failures` | BIGINT | |
| `normalizer_version` | INTEGER | `QueryNormalizer.Version`, currently 4 |
| `redaction_policy` | TEXT | The policy in force for this run |
| `sql_text_policy` | TEXT | `raw` or `literals`. **NULL means a run imported before this column existed — read it as `raw`.** |
| `sql_text_sanitizer_version` | INTEGER | `SqlTextSanitizer.Version`, currently 1. NULL for pre-versioning runs. |
| `events_server_diagnostics` | BIGINT | `sp_server_diagnostics_component_result` events parsed into one of the four known components |
| `events_server_diagnostics_unhandled` | BIGINT | Same event, a component this tool does not read — `events`, or an availability group name. Not an error |
| `server_diagnostics_parse_failures` | BIGINT | A **known** component whose payload was absent, malformed, or had an unexpected root |
| `server_diagnostics_embedded_blocking` | BIGINT | `blocked-process-report` elements lifted out of a diagnostics cycle. **Sub-documents, not events** — excluded from the reconciliation sum |
| `server_diagnostics_embedded_blocking_failures` | BIGINT | Same, that `BlockingReportParser` refused. Also excluded |
| `sql_text_sanitize_failures` | BIGINT | Events whose text could not be tokenized and were replaced by the sanitizer's placeholder |

The counters are designed to be **mutually exclusive and exhaustive**: every event read is
accounted for in exactly one bucket. If they do not add up, that is a bug, not a rounding issue.
`sql_text_sanitize_failures` is the one exception: it is not part of that partition, but a strict
subset of `tokenize_failures` — an event that fails to tokenize under `--sanitize-sql-text
literals` increments both.

`redaction_policy` is per run. A project can contain runs imported under different policies, and
`export-events` uses this column to decide which runs even have XML to export.

`sql_text_policy` is also per run, and the same caution applies: a project can mix `raw` and
`literals` runs, and `normalized_queries` is project-wide, so a shape first seen under `raw` keeps
its raw text regardless of later runs. See [privacy.md](privacy.md#statement-text-sanitization).

### `executions`

One row per completed statement.

| Column | Type | Notes |
|---|---|---|
| `execution_id` | BIGINT PK | |
| `run_id` | BIGINT | → `ingestion_runs` |
| `captured_at` | TIMESTAMP | UTC |
| `event_name` | TEXT | Raw XE event name |
| `event_class` | TEXT | `RpcCall`, `SqlBatch`, `Statement`, `Unknown` |
| `object_name` | TEXT | Procedure name for RPC events |
| `is_system` | BOOLEAN | |
| `database_name`, `login_name`, `client_hostname`, `client_app_name` | TEXT | From capture actions; NULL when the action was not captured |
| `session_id` | INTEGER | |
| `duration_us`, `cpu_time_us` | BIGINT | |
| `logical_reads`, `physical_reads`, `writes`, `row_count` | BIGINT | |
| `query_hash`, `query_plan_hash` | TEXT | From `sqlserver.query_hash` / `query_plan_hash` actions. **NULL unless you captured them**, which is what breaks plan correlation. See [capture-session.md](capture-session.md). |
| `sql_text_raw` | TEXT | The statement as captured. **Never redacted.** |
| `normalized_hash` | TEXT | → `normalized_queries` |
| `xe_file_name`, `file_offset` | TEXT / BIGINT | Provenance back to the source `.xel` |

### `normalized_queries`

One row per distinct query shape, deduplicated across every run.

| Column | Type | Notes |
|---|---|---|
| `normalized_hash` | TEXT PK | SHA-256 (hex, lowercase) of the normalized text |
| `normalized_sql` | TEXT | Literals replaced by `?`, keywords lowercased |
| `statement_kind` | TEXT | DML (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `EXEC`, `FETCH`), DDL (`CREATE TABLE`, `ALTER TABLE ADD COLUMN`, `CREATE INDEX`, `ALTER PROCEDURE`, …) or `OTHER`. Roughly fifty values — the full mapping is in [normalization.md](normalization.md#classification) |
| `primary_table` | TEXT | The table the statement acts on, **as written** (schema prefix included when present) |
| `target_object` | TEXT | The sub-object the statement targets: column, constraint, index, procedure, function, trigger, cursor. `NULL` for most DML |
| `normalizer_version` | INTEGER | Bump invalidates comparability across projects. Covers classification too |
| `first_seen_at`, `last_seen_at` | TIMESTAMP | |

`primary_table` keeping the name as written means `dbo.T` and `T` are two distinct values. A
`GROUP BY primary_table` will split one table across two rows if the workload writes it both ways.

This is the table the whole tool is built around. See [normalization.md](normalization.md).

### `execution_parameters`

Parameters extracted from RPC calls and `sp_executesql`.

| Column | Type | Notes |
|---|---|---|
| `execution_id` | BIGINT | → `executions` |
| `ordinal` | INTEGER | |
| `name` | TEXT | e.g. `@CustomerId` |
| `source_kind` | TEXT | Currently always `RpcParameter` |
| `sql_type_guess` | TEXT | `nvarchar`, `varchar`, `int`, `decimal`, `unknown` |
| `value_text` | TEXT | **Subject to redaction.** Empty when the policy is `off` |
| `value_redacted` | BOOLEAN | True whenever the stored value is not the original |
| `is_truncated` | BOOLEAN | |
| `parse_confidence` | DOUBLE | 0.9 for a plain RPC call, 0.7 through `sp_executesql`, 0.6 otherwise |

Extraction is regex-based over the statement text and is best-effort, which is what
`parse_confidence` is telling you. See [privacy.md](privacy.md).

---

## Blocking tables

### `blocking_reports`

| Column | Type | Notes |
|---|---|---|
| `report_id` | BIGINT PK | |
| `run_id` | BIGINT | |
| `captured_at` | TIMESTAMP | |
| `monitor_loop` | INTEGER | Groups reports emitted by the same blocked-process monitor pass. Used to reconstruct chains. |
| `database_id` | INTEGER | |
| `raw_xml` | TEXT | The original report. **NULL unless the run was imported with redaction `off`.** |
| `source` | TEXT | `event` for a threshold-triggered `blocked_process_report`, `diagnostics` for a snapshot lifted out of an `sp_server_diagnostics` cycle. **NULL means a run imported before this column existed — read it as `event`.** Every filter must be written `coalesce(source,'event')`, or it silently drops every pre-migration row |

### `blocking_processes`

Two rows per report, one `blocked` and one `blocking`.

| Column | Type | Notes |
|---|---|---|
| `report_id` | BIGINT | → `blocking_reports` |
| `role` | TEXT | `blocked` or `blocking` |
| `spid`, `ecid` | INTEGER | |
| `status` | TEXT | |
| `wait_resource_raw` | TEXT | As emitted by the engine |
| `wait_resource_type` | TEXT | Parsed: `Key`, `Object`, `Page`, `Rid`, `Database`, `PageLatch`, `AppLock`, `Other` |
| `object_id`, `hobt_id` | BIGINT | Parsed out of the wait resource where present |
| `wait_time_us` | BIGINT | |
| `lock_mode`, `isolation_level` | TEXT | |
| `trancount` | INTEGER | |
| `client_app`, `host_name`, `login_name` | TEXT | |
| `inputbuf` | TEXT | The input buffer. Subject to the same redaction gate as `raw_xml`. |
| `inputbuf_fingerprint` | TEXT | Normalized-query hash of the input buffer, joinable to `normalized_queries` |

`inputbuf_fingerprint` is what makes "which query shape blocks the most" answerable: the input
buffer goes through the same normalizer as everything else.

### `deadlock_reports`

| Column | Type | Notes |
|---|---|---|
| `report_id` | BIGINT PK | |
| `run_id` | BIGINT | |
| `captured_at` | TIMESTAMP | |
| `victim_spids`, `participant_spids` | TEXT | Comma-separated |
| `graph_xml` | TEXT | The deadlock graph, or the literal `<redacted/>` when the run was not imported with redaction `off` |

---

## Execution plan tables

### `plan_profiles`

One row per `query_post_execution_plan_profile` event. Not deduplicated: this is the raw
observation table. Deduplication happens by `plan_hash` in the artifacts.

| Column | Type | Notes |
|---|---|---|
| `plan_profile_id` | BIGINT PK | |
| `run_id`, `captured_at` | | |
| `plan_hash` | TEXT | Bare, no `0x`. See [execution-plans.md](execution-plans.md#plan-identity) |
| `plan_hash_source` | TEXT | `queryplanhash`, `multi` or `content` |
| `file_stem` | TEXT | Prefixed form used for file names: `p_`, `m_` or `c_` |
| `statement_count` | INTEGER | `<StmtSimple>` elements in the plan |
| `query_hash` | TEXT | From the plan XML itself, so it is present even when the capture omitted the action |
| `statement_type` | TEXT | |
| `statement_text`, `statement_text_length` | TEXT / INTEGER | **Truncated by the engine.** Never use it as a key. |
| `duration_us`, `cpu_time_us` | BIGINT | |
| `estimated_rows`, `subtree_cost` | DOUBLE | |
| `dop` | INTEGER | |
| `serial_desired_memory_kb`, `granted_memory_kb`, `max_used_memory_kb` | BIGINT | |
| `write_outcome` | TEXT | `WroteFirst`, `WroteWorst`, `Skipped`, `Failed` |
| `sqlplan_path`, `digest_path` | TEXT | Filled by the finalize pass after import |
| `is_first`, `is_worst` | BOOLEAN | Which of the two retained files this observation is |

Indexed on `captured_at`.

### `plan_findings`

| Column | Type | Notes |
|---|---|---|
| `plan_profile_id` | BIGINT | → `plan_profiles`, indexed |
| `kind` | TEXT | One of twelve kinds, see [execution-plans.md](execution-plans.md#findings) |
| `node_id` | INTEGER | Plan node, NULL for plan-wide findings |
| `detail_json` | TEXT | Kind-specific JSON payload |

---

## Query Store tables

Populated by `query-store-import`. Mirrors `sys.query_store_*`, with time columns converted and
duration metrics kept in microseconds. `run_id` here refers to `qds_runs`, a separate sequence
from `ingestion_runs`.

| Table | Contents |
|---|---|
| `qds_runs` | One row per import: server, database, window, SQL Server version, Query Store state, row counts, plan write results |
| `qds_query_text` | Distinct query texts, with the `is_part_of_encrypted_module` and `has_restricted_text` flags |
| `qds_queries` | Query metadata: `query_hash`, parameterization type, compile count, last execution |
| `qds_plans` | Plan metadata plus `sqlplan_path` / `plan_written` for the extracted `.sqlplan` files |
| `qds_runtime_stats` | Per interval, per plan, per execution type: 11 metrics × 5 aggregates |
| `qds_wait_stats` | Per interval, per plan, per wait category. Absent on servers without `sys.query_store_wait_stats` |

The 11 runtime metrics, each stored as `avg_`, `min_`, `max_`, `last_` (BIGINT) and `stdev_`
(DOUBLE):

`duration_us`, `cpu_time_us`, `clr_time_us`, `logical_io_reads`, `logical_io_writes`,
`physical_io_reads`, `rowcount`, `dop`, `query_max_used_memory_8kb_pages`,
`tempdb_space_used_8kb_pages`, `log_bytes_used`.

The last two exist only from SQL Server 2017 onward and are NULL on 2016. IO metrics are counted
in 8 KB pages, as the engine reports them; the column names say so. See
[query-store.md](query-store.md).

---

## `obfuscation_map`

The project-wide identifier map used by `obfuscate-plan --project`.

| Column | Type |
|---|---|
| `kind` | VARCHAR — `Database`, `Schema`, `Table`, `TempTable`, `Column`, `Index`, `Statistics`, `Parameter`, `Alias` |
| `original_name` | VARCHAR |
| `token` | VARCHAR |

Primary key `(kind, original_name)`. **This table is the de-anonymization key.** A project file
containing it is exactly as sensitive as the original plans. See [privacy.md](privacy.md).

---

## Useful queries

```sql
-- Top query shapes by total time, with percentiles
SELECT n.statement_kind,
       n.primary_table,
       count(*)                                   AS execs,
       sum(e.duration_us)  / 1e6                  AS total_s,
       avg(e.duration_us)  / 1e3                  AS avg_ms,
       quantile_cont(e.duration_us, 0.95) / 1e3   AS p95_ms,
       max(e.duration_us)  / 1e3                  AS max_ms,
       n.normalized_sql
FROM executions e
JOIN normalized_queries n USING (normalized_hash)
GROUP BY ALL
ORDER BY total_s DESC
LIMIT 20;
```

```sql
-- Where is the time going, by origin?
SELECT client_app_name, database_name,
       count(*) AS execs, sum(duration_us) / 1e6 AS total_s
FROM executions
GROUP BY ALL ORDER BY total_s DESC LIMIT 20;
```

```sql
-- Shapes whose duration is wildly inconsistent: parameter sniffing candidates
SELECT n.normalized_sql, count(*) AS execs,
       min(e.duration_us) / 1e3 AS min_ms,
       max(e.duration_us) / 1e3 AS max_ms,
       max(e.duration_us)::DOUBLE / nullif(min(e.duration_us), 0) AS spread
FROM executions e JOIN normalized_queries n USING (normalized_hash)
GROUP BY 1
HAVING count(*) > 20 AND min(e.duration_us) > 0
ORDER BY spread DESC LIMIT 20;
```

```sql
-- Which query shape blocks the most, and for how long
SELECT n.normalized_sql, count(*) AS incidents, sum(p.wait_time_us) / 1e6 AS blocked_s
FROM blocking_processes p
JOIN normalized_queries n ON n.normalized_hash = p.inputbuf_fingerprint
WHERE p.role = 'blocking'
GROUP BY 1 ORDER BY incidents DESC LIMIT 10;
```

```sql
-- Plans with detected problems, worst first
SELECT pp.plan_hash, pp.statement_type,
       pp.duration_us / 1e3 AS ms,
       list(DISTINCT f.kind) AS findings
FROM plan_profiles pp
JOIN plan_findings f USING (plan_profile_id)
GROUP BY ALL
ORDER BY ms DESC LIMIT 20;
```

```sql
-- Ingestion quality: did anything get lost?
SELECT run_id, source_path, events_read,
       events_mapped + events_unmapped + events_cleaned
       + events_blocking + events_deadlocks + events_plan_profiles
       + events_server_diagnostics + events_server_diagnostics_unhandled
       + server_diagnostics_parse_failures AS accounted,
       tokenize_failures, blocking_parse_failures, plan_parse_failures
FROM ingestion_runs ORDER BY run_id;
```

`accounted` must equal `events_read`. Do **not** add `server_diagnostics_embedded_blocking` or
`server_diagnostics_embedded_blocking_failures` to that sum: they count `blocked-process-report`
elements lifted out of a diagnostics cycle, which are sub-documents of an event already counted in
`events_server_diagnostics`. Adding them makes `accounted` exceed `events_read`.

## Health tables — `sp_server_diagnostics` cycles

Written by `import` from a `system_health` capture. Empty for a workload-only capture.

`health_cycles` — one row per diagnostics cycle.

| Column | Type | Notes |
|---|---|---|
| `cycle_id` | BIGINT | |
| `run_id` | BIGINT | |
| `cycle_at` | TIMESTAMP | The earliest of the cycle's component timestamps |
| `series_key` | TEXT | Currently always `1`. A capture folder can hold more than one session recording the same server, but nothing in the capture says which cycle belongs to which session, so no split is attempted. The digest reports the irregular cadence instead |

`health_samples` — one row per component per cycle.

| Column | Type | Notes |
|---|---|---|
| `sample_id` | BIGINT | |
| `cycle_id`, `run_id` | BIGINT | |
| `captured_at` | TIMESTAMP | The component's own timestamp. The four of a cycle differ by under a millisecond, which is why `cycle_id` exists |
| `component` | TEXT | `QUERY_PROCESSING`, `RESOURCE`, `SYSTEM`, `IO_SUBSYSTEM`, or any other value SQL Server emits |
| `state` | TEXT | `CLEAN`, `WARNING`, … stored verbatim |
| `handled` | BOOLEAN | False for a component this tool does not read. The row is kept and has no children |

`health_metrics` — every scalar attribute, name/value.

| Column | Type | Notes |
|---|---|---|
| `sample_id` | BIGINT | |
| `name` | TEXT | The source attribute name. A duration carries a `Us` suffix and is in **microseconds** |
| `value_num` | DOUBLE | Non-integer values |
| `value_big` | BIGINT | Integer counters. `DOUBLE` loses integers past 2^53 |
| `value_text` | TEXT | Anything non-numeric, such as `trackingNonYieldingScheduler` |

`health_waits` — `topWaits`. **These counters are cumulative since instance start**, like
`sys.dm_os_wait_stats`. Summing them across cycles ranks uptime, not activity; take a delta.
`avg_wait_us` and `max_wait_us` are instance-lifetime figures — the maximum is a running maximum
whose record may predate the capture, and the average is rounded to whole milliseconds by the
source, so it reads 0 for the busiest waits. Nothing should be ranked on either.

| Column | Type | Notes |
|---|---|---|
| `sample_id` | BIGINT | |
| `preemptive` | BOOLEAN | |
| `ranking` | TEXT | `byCount` or `byDuration` — four independent lists, never summed together |
| `wait_type` | TEXT | |
| `waits` | BIGINT | Cumulative |
| `avg_wait_us`, `max_wait_us` | BIGINT | Since instance start |

`health_cpu_requests`, `health_pending_tasks`, `health_pending_io`, `health_memory_entries` —
the remaining repeated nodes.

| Table | Columns |
|---|---|
| `health_cpu_requests` | `sample_id`, `session_id`, `request_id`, `command`, `cpu_time_us`, `cpu_utilization`, `task_address` |
| `health_pending_tasks` | `sample_id`, `entry_point`, `task_count` |
| `health_pending_io` | `sample_id`, `duration_us`, `file_path`, `handle`, `offset_bytes` |
| `health_memory_entries` | `sample_id`, `report_name`, `unit`, `description`, `value_num`, `value_text` |

`health_pending_io.file_path` is a server-side path and is stored **verbatim under every redaction
mode**, `full` included. See [privacy.md](privacy.md).

`offset_bytes`, not `offset`: `OFFSET` is a reserved word in DuckDB.

## A caveat on `query_hash` representations

Three tables carry a column called `query_hash`, and they do **not** share a text format:

| Table | Source | Format |
|---|---|---|
| `executions.query_hash` | The `sqlserver.query_hash` capture action, stringified as XELite hands it over | Typically a decimal `UInt64` |
| `plan_profiles.query_hash` | The `QueryHash` attribute in the plan XML | Bare hex, `0x` stripped |
| `qds_queries.query_hash` | `CONVERT(varchar(34), q.query_hash, 1)` | Hex **with** a `0x` prefix |

Joining across sources therefore needs an explicit conversion on your side. Normalize to bare
uppercase hex before comparing, for example:

```sql
SELECT upper(replace(q.query_hash, '0x', '')) AS h, q.count_compiles
FROM qds_queries q;
```

Correlating plans with executions inside one `.xel` project has the same requirement, which is
why `import` warns loudly when the completion events carry no `query_hash` at all. See
[capture-session.md](capture-session.md).
