---
name: analyzing-sql-workloads
description: Use when working with a SQLFerret project directory or SQL Server Extended Events (.xel) capture - investigating slow queries, blocking, deadlocks, execution plans, Query Store data, or querying a sqlferret.duckdb file.
---

# Analyzing SQL Server workloads with SQLFerret

## Overview

SQLFerret turns `.xel` Extended Events captures and Query Store snapshots into one queryable
DuckDB project directory. Answer workload questions from the **bounded exports** first; drop to
SQL against the DuckDB file only when the exports cannot answer the question.

There is no `--help`. `docs/cli-reference.md` in the SQLFerret repo is the command reference,
`docs/data-model.md` the schema reference.

## Invocation

`sqlferret` below is shorthand — the repo installs no binary:

```bash
dotnet run --project <sqlferret>/src/SqlFerret.Cli -- <command> ...
```

## Workflow

1. **Ingest** — `sqlferret import <file-or-dir> --project <dir>`
   `--project` is a **directory**, created on first use. Re-importing appends a new run; nothing
   is replaced. Read the summary line on stdout: `unmapped`, `cleaned`, `tokenizeFailures`,
   `*ParseFailures` tell you what the analysis will be missing.
2. **Survey** — `sqlferret top-slow --project <dir> --limit 20`
3. **Blocking** — `sqlferret export-blocking --project <dir> --format md`
   Bounded on purpose. Use `--full` (unbounded NDJSON) only when writing to a file.
4. **Dig** — query `<dir>/sqlferret.duckdb` directly for anything the above does not cover.

## Traps

| Trap | What to do |
|---|---|
| Durations look absurdly large | Every `*_us` column is **microseconds**. `/1e3` for ms, `/1e6` for s. Never assume ms. |
| `export-events` reports `skipped` | Blocking/deadlock XML is stored **only** for runs imported with `--redaction off`. Any other policy and it was never written. Re-import that capture. |
| Joining on `query_hash` fails silently | Three different text formats: `executions.query_hash` decimal, `plan_profiles.query_hash` bare hex, `qds_queries.query_hash` `0x`-prefixed hex. Normalize to bare uppercase hex before comparing. |
| Plans do not correlate with executions | The capture omitted the `sqlserver.query_hash` ACTION. No code fixes this — a new capture is needed. `import` warns on stderr when it happens. |
| Grouping plans by statement text | `plan_profiles.statement_text` is **truncated by the engine** and can be near-empty. Never use it as a key; use `plan_hash`. |
| `top-slow` ranks the wrong way | Its ordering (total duration) is not configurable from the CLI. Query the DuckDB file for p95/max/avg rankings. |
| Comparing shapes across projects | Only valid when `normalizer_version` matches (`ingestion_runs`, `normalized_queries`). |
| Empty `database_name` / `login_name` / `client_app_name` | Those capture ACTIONs were not in the session. Absence of data, not absence of activity. |

## Starting SQL

```sql
-- Top shapes by total time, with percentiles
SELECT n.statement_kind, n.primary_table, count(*) AS execs,
       sum(e.duration_us) / 1e6                 AS total_s,
       quantile_cont(e.duration_us, 0.95) / 1e3 AS p95_ms,
       n.normalized_sql
FROM executions e JOIN normalized_queries n USING (normalized_hash)
GROUP BY ALL ORDER BY total_s DESC LIMIT 20;
```

`docs/data-model.md#useful-queries` has ready-made queries for parameter sniffing, blocking
attribution, plan findings and ingestion-quality checks. Read it before inventing SQL.

## Before sharing anything outward

`executions.sql_text_raw` is **never redacted**, and `.sqlplan` files carry your schema and
sometimes literal values. Before a plan leaves the project, run
`sqlferret obfuscate-plan --project <dir> --plan-id <id>`, and keep the `*.map.json` /
`obfuscation_map` table behind — that map *is* the de-anonymization key.

Do not paste raw captured SQL, input buffers or unobfuscated plans into anything external
without the user saying so explicitly.
