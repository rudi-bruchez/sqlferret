# Getting started

This walks through a complete first run: capture, import, analyze, export. It assumes nothing
beyond the .NET 10 SDK and a SQL Server you are allowed to trace.

If you already have `.xel` files, skip to [step 2](#2-build).

---

## 1. Capture a trace

SQLFerret reads Extended Events files. The minimum useful session captures completion events;
add the plan-profile event if you want real execution plans, and the blocked-process report if
you want blocking analysis.

```sql
CREATE EVENT SESSION [sqlferret_capture] ON SERVER
  ADD EVENT sqlserver.rpc_completed (
      ACTION (sqlserver.query_hash, sqlserver.query_plan_hash,
              package0.attach_activity_id, sqlserver.session_id,
              sqlserver.database_name, sqlserver.client_app_name)
      WHERE duration > 100000),
  ADD EVENT sqlserver.sql_batch_completed (
      ACTION (sqlserver.query_hash, sqlserver.query_plan_hash,
              package0.attach_activity_id, sqlserver.session_id,
              sqlserver.database_name, sqlserver.client_app_name)
      WHERE duration > 100000),
  ADD EVENT sqlserver.query_post_execution_plan_profile (
      ACTION (package0.attach_activity_id, sqlserver.session_id,
              sqlserver.database_name))
  ADD TARGET package0.event_file (SET filename = N'sqlferret', max_file_size = 256)
  WITH (MAX_MEMORY = 8192 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
        MAX_DISPATCH_LATENCY = 30 SECONDS, STARTUP_STATE = OFF);

ALTER EVENT SESSION [sqlferret_capture] ON SERVER STATE = START;
```

`duration > 100000` is 100 ms in microseconds. Raise it on a busy server, lower it if you are
hunting a chatty ORM.

Two things worth internalizing before you run this in production:

- **`ACTION(sqlserver.query_hash)` on the completion events is not optional** if you want plans
  correlated with queries. Without it, plans and executions live in the same project but cannot be
  joined. SQLFerret warns you on import when this happens.
- **`query_post_execution_plan_profile` is the cheap one.** Do not confuse it with
  `query_post_execution_showplan`, which is dramatically more expensive and will hurt a production
  server.

The full rationale, the field-level findings from a real reference trace, and the manual
correlation fallback are in **[capture-session.md](capture-session.md)**.

Stop the session and collect the files:

```sql
ALTER EVENT SESSION [sqlferret_capture] ON SERVER STATE = STOP;
```

The `.xel` files land in the instance's log directory. Copy them to a `logs/` folder on the
machine where you will run SQLFerret.

For blocking analysis you also need the blocked-process report, which requires enabling the
detection threshold on the server. See [blocking.md](blocking.md#capturing-blocked-process-reports).

## 2. Build

```bash
git clone https://github.com/rudi-bruchez/sqlferret.git
cd sqlferret
dotnet build
```

A clean build produces zero warnings. To check the suite:

```bash
dotnet test
```

You should see 387 tests, 377 passing and 10 skipped. The skips are the tests that need a local
`.xel` sample or a live SQL Server, and they skip by design so that a fresh clone stays green.

### Making it feel like a command

Everything below is written as `sqlferret …`. The repo does not install a binary, so define a
shell alias, or publish one:

```bash
# bash / zsh
alias sqlferret='dotnet run --project /path/to/sqlferret/src/SqlFerret.Cli --'

# PowerShell
function sqlferret { dotnet run --project C:\path\to\sqlferret\src\SqlFerret.Cli -- @args }

# or produce a real executable
dotnet publish src/SqlFerret.Cli -c Release -o ./dist
./dist/SqlFerret.Cli import ./logs --project ./audits/prod
```

## 3. Import

```bash
sqlferret import ./logs --project ./audits/prod-2026-08
```

`--project` is a **directory**. It does not need to exist; SQLFerret creates it, along with a
DuckDB file, a `plans/` folder, an `exports/` folder, a `project.json` manifest and a `README.md`
explaining the layout to whoever finds the folder later.

While it runs, a live gauge is written to stderr. When it finishes, a one-line summary goes to
stdout:

```text
run 1: read=41827 mapped=41120 unmapped=612 cleaned=95 tokenizeFailures=0
       blocking=48 deadlocks=3 blockingParseFailures=0
       planProfiles=346 planParseFailures=0 planWriteFailures=0
```

Those counters are mutually exclusive and exhaustive: every event read is accounted for in exactly
one of them. Nothing is silently dropped. If `unmapped` is large, your session is capturing event
types SQLFerret does not model, which is harmless but tells you the filter could be tighter.

Importing again into the same project adds a new run rather than replacing the old one. Runs are
numbered, plan artifacts are partitioned per run, and analysis queries span all of them.

### Choosing a redaction policy

The default is `masked`. Override it per import:

```bash
sqlferret import ./logs --project ./audits/prod --redaction hash
```

`off` means "do not store parameter values at all", which is the most private setting for
parameters, but it is also the setting that *retains* raw blocking and deadlock XML. That is not a
contradiction so much as a sharp edge; [privacy.md](privacy.md) explains it properly. Read it
before you pick.

## 4. Analyze

### Ranked query shapes

```bash
sqlferret top-slow --project ./audits/prod-2026-08 --limit 20
```

```text
SELECT      1204  total=812304 ms   select o.id , o.total from dbo.Orders o where o.CustomerId = ?
EXEC         318  total=402117 ms   exec dbo.RebuildCart @CartId = ? , @UserId = ?
```

Columns are statement kind, execution count, total duration, and the normalized SQL truncated to
80 characters. Durations are formatted per the project's `display.durationUnit`, default
milliseconds; the database always stores microseconds.

### Terminal UI

```bash
dotnet run --project src/SqlFerret.Tui -- ./audits/prod-2026-08
```

A two-pane shell: a view rail on the left, content on the right. `Top Slow` gives you a sortable,
filterable table; Enter drills into individual executions with their parameters, and `c` copies
the statement. Full key map in [tui.md](tui.md).

### Direct SQL

The project file is a plain DuckDB database with no proprietary layer on top. This is often the
fastest path to an answer:

```bash
duckdb ./audits/prod-2026-08/sqlferret.duckdb
```

```sql
-- which application is burning the most time?
SELECT client_app_name, count(*) AS execs, sum(duration_us) / 1e6 AS total_s
FROM executions
GROUP BY 1 ORDER BY total_s DESC LIMIT 10;

-- the same query shape, but only the slow tail
SELECT n.normalized_sql, count(*), max(e.duration_us) / 1000 AS max_ms
FROM executions e JOIN normalized_queries n USING (normalized_hash)
WHERE e.duration_us > 1000000
GROUP BY 1 ORDER BY 2 DESC LIMIT 20;
```

Every table and column is documented in [data-model.md](data-model.md).

## 5. Export

### Blocking digest

```bash
sqlferret export-blocking --project ./audits/prod-2026-08 --format md --out blocking.md
```

A bounded, ranked summary: overview and time window, wait-resource locality, top contended
objects, top blockers and top blocked query shapes, lock modes, isolation levels, chain depths,
and a handful of concrete sample incidents per pattern. `--format json` gives the same content
as structured data; the default emits both.

### Raw event XML

```bash
sqlferret export-events --project ./audits/prod-2026-08 --out ./exports/events \
    --kind both --last 24h --limit 100
```

One file per event plus an `index.json` manifest. Only works for runs imported with
`--redaction off`, since that is the only case where the XML was retained.

### Plan artifacts

These are written during import, not on demand. Look in
`plans/profile/run_<id>/`. Start with `index.json`, which lists every distinct plan with its
execution count, duration range, file paths and the kinds of problems detected in it. From there
open the `.digest.json` for detail, or the `.sqlplan` in SSMS or Plan Explorer.

See [execution-plans.md](execution-plans.md).

## 6. Optional: Query Store

If you can reach the server, you can pull a Query Store snapshot into the same project:

```bash
sqlferret query-store-import --project ./audits/prod-2026-08 \
    --conn "Server=sql01;Database=Sales;Integrated Security=true;TrustServerCertificate=True" \
    --last 7d
```

Read-only, under `READ UNCOMMITTED`, against `sys.query_store_*`. Every plan is written out as a
`.sqlplan`. See [query-store.md](query-store.md).

Better than passing `--conn` on the command line: put it in the project's
`sqlferret.config.json` with the secret part interpolated from `.env`. See
[configuration.md](configuration.md).

## Where to go next

- Something looks wrong in a plan → [execution-plans.md](execution-plans.md)
- Two statements you expected to group together did not → [normalization.md](normalization.md)
- You need to share a plan outside your organization → [privacy.md](privacy.md)
- You want to write your own analysis on top → [data-model.md](data-model.md) and
  [architecture.md](architecture.md)
