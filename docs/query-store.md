# Query Store import

`query-store-import` takes a read-only snapshot of a database's Query Store and stores it in the
same project file as your `.xel` workload.

```bash
sqlferret query-store-import --project ./audits/prod \
    --conn "Server=sql01;Database=Sales;Integrated Security=true;TrustServerCertificate=True" \
    --last 7d
```

## Why bother, if you already have a trace

They answer different questions.

| | Extended Events | Query Store |
|---|---|---|
| Coverage | Only what your session filter caught, during the window you were capturing | Everything the server compiled, over the retention period |
| Granularity | Individual executions, with parameters | Aggregates per plan per interval |
| Plans | Actual plans with runtime counters, if you captured the profile event | Cached plans, no runtime counters |
| Waits | Not directly | Per plan, per wait category |
| Cost to collect | A running session on the instance | Already there, if it is enabled |

A trace tells you what happened during one hour under one filter. Query Store tells you what the
server has been doing for the last month, including everything that fell below your `duration`
threshold and everything that ran while you were not looking. Having both in one file lets you
check whether the hour you captured was representative.

## What it does

Opens a `SqlConnection`, sets `READ UNCOMMITTED`, optionally issues `USE [database]`, then reads:

| Source view | Target table |
|---|---|
| `sys.query_store_query_text` | `qds_query_text` |
| `sys.query_store_query` | `qds_queries` |
| `sys.query_store_plan` | `qds_plans` |
| `sys.query_store_runtime_stats` (+ `_interval`) | `qds_runtime_stats` |
| `sys.query_store_wait_stats` | `qds_wait_stats` |

Every read is a `SELECT`. Nothing is written to, forced on, or cleared from the target server.

Before it starts, it checks `actual_state` and fails cleanly if Query Store is `OFF`, `ERROR`, or
absent:

```text
query-store-import: Query Store is not enabled on the target database (actual_state=OFF)
```

`qds_runs` records what the server reported at snapshot time: server name, database, version,
`actual_state`, `desired_state`, whether wait stats were available, and all the row counts. That
matters later — a Query Store in `READ_ONLY` because it hit its size cap holds stale data, and the
run record is the only place that fact survives.

## Time windows

Without `--from`/`--to`/`--last`, everything in the Query Store is extracted.

With a window, the filter is applied on the **runtime stats interval**:

```sql
WHERE i.start_time < @to AND i.end_time > @from
```

That is an overlap test, not containment, so an interval straddling a boundary is included rather
than dropped.

The window also propagates backwards: query text, queries and plans are restricted to those with
runtime stats in the window, via joins. You do not get the entire query catalogue back when you
asked for the last 24 hours.

```bash
sqlferret query-store-import --project ./audits/prod --last 24h
sqlferret query-store-import --project ./audits/prod --from 2026-08-01 --to 2026-08-08
```

`--last` and `--from`/`--to` are mutually exclusive. Datetimes are parsed with invariant culture,
so use `2026-08-01` or `2026-08-01 14:30:00`, not a locale-specific format.

## Plans

By default every plan's XML is written to `plans/<plan_id>.sqlplan`, and the path plus a success
flag are recorded on `qds_plans`. Plans with a NULL `query_plan` are skipped without counting as a
failure.

`--no-plans` skips the file writes and keeps the metadata. Use it when you only want the runtime
statistics, or when you do not want raw showplan XML on disk.

**Warning on redaction.** Showplan XML can contain literal predicate values that your redaction
policy would have covered for parameters. When plans are requested and the project's policy is not
`off`, stderr says so before the import starts. Either pass `--no-plans`, or run `obfuscate-plan`
over `plans/` afterwards. See [privacy.md](privacy.md).

## Version differences

Two metrics exist only from SQL Server 2017:

- `tempdb_space_used`
- `log_bytes_used`

On 2016 the import substitutes typed NULLs rather than failing, so the DuckDB schema is identical
regardless of the source version and queries written against one work against the other.

`sys.query_store_wait_stats` also arrived in 2017. Its absence is detected up front, recorded as
`qds_runs.wait_stats_available = false`, and the wait import is skipped. Do not read an empty
`qds_wait_stats` as "no waits"; check the flag.

## Units

Query Store reports duration and CPU in microseconds natively, and those columns are stored
unchanged, consistent with the microseconds-everywhere rule.

Wait times are the exception in the source: `sys.query_store_wait_stats` reports milliseconds. The
importer converts them to microseconds on the way in, and the column names say `_us`.

IO metrics stay in 8 KB pages, as the engine counts them. The column names say so
(`query_max_used_memory_8kb_pages`, `tempdb_space_used_8kb_pages`); multiply by 8192 for bytes.

## Output

```text
qds run 1: queries=8412 queryText=7980 plans=9134 runtimeRows=214880 waitRows=48210
           plansWritten=9134 planFailures=0
```

`queryText` below `queries` is normal: several queries can share one text row.

`planFailures` above zero means some plan XML could not be written to disk. The metadata is still
in `qds_plans` with `plan_written = false`.

## Connection strings

Passing `--conn` on the command line puts a password in your shell history. Prefer configuration:

```json
{
  "server": {
    "connectionString": "Server=sql01;Database=Sales;${SQLFERRET_AUTH};TrustServerCertificate=True"
  }
}
```

```ini
# .env, gitignored
SQLFERRET_AUTH=User ID=sqlferret;Password=…
```

`--conn` overrides the configured value when both are present. See
[configuration.md](configuration.md).

The account needs `VIEW DATABASE STATE` on the target database. That is all; nothing here requires
elevated rights.

## Querying the snapshot

```sql
-- top plans by total duration in the snapshot window
SELECT q.object_name, p.plan_id,
       sum(r.count_executions)                              AS execs,
       sum(r.avg_duration_us * r.count_executions) / 1e6    AS total_s
FROM qds_runtime_stats r
JOIN qds_plans p   USING (run_id, plan_id)
JOIN qds_queries q USING (run_id, query_id)
GROUP BY ALL ORDER BY total_s DESC LIMIT 20;
```

```sql
-- queries with more than one plan: plan instability candidates
SELECT query_id, count(DISTINCT plan_id) AS plans
FROM qds_plans GROUP BY 1 HAVING count(DISTINCT plan_id) > 1
ORDER BY 2 DESC LIMIT 20;
```

```sql
-- where does the waiting happen?
SELECT wait_category, sum(total_query_wait_time_us) / 1e6 AS waited_s
FROM qds_wait_stats GROUP BY 1 ORDER BY 2 DESC;
```

```sql
-- forced plans, and whether forcing is failing
SELECT plan_id, query_id, is_forced_plan, force_failure_count, last_force_failure_reason_desc
FROM qds_plans WHERE is_forced_plan ORDER BY force_failure_count DESC;
```

Full column reference in [data-model.md](data-model.md#query-store-tables).
