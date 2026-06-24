# Query Store Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `query-store-import` CLI command + `QueryStoreImportService` that read a database's Query Store (all, or a `--from/--to`/`--last` window) into new DuckDB `qds_*` tables and write each plan as a `.sqlplan` file.

**Architecture:** Storage first (DuckDB `qds_*` schema + insert methods on a `partial DuckDbProject`, with the repetitive runtime-stats columns generated from one metric list), then a pure time-window parser, then the `Microsoft.Data.SqlClient` ETL service that streams `sys.query_store_*` into those inserts under READ UNCOMMITTED, then the CLI command. Straight ETL — no new abstraction, all aggregation deferred to a later query/export layer.

**Tech Stack:** .NET 10 / C# 14, `DuckDB.NET`, `Microsoft.Data.SqlClient` (already referenced via `EstimatedPlanService`), xUnit + `Xunit.SkippableFact`.

## Global Constraints

- **Target framework** `net10.0`, Nullable + ImplicitUsings, LangVersion latest. Build is **0 warnings**; run `dotnet format` before committing.
- **Microseconds in Core.** QS `avg/min/max/last/stdev_duration`, `_cpu_time`, `_clr_time` are already microseconds → stored `*_us`, no conversion. QS **wait** times are milliseconds → converted `×1000` to `*_us` at the ingestion boundary (the only conversion). Memory/tempdb (8 KB pages) and IO (page counts) stored raw with unit-explicit names.
- **Secrets in `.env` only** — connection string from `project.Config.ConnectionString` (`${ENV}` interpolation) or `--conn` override; never logged.
- **Nothing silently dropped** — every plan is written or a counted failure; all totals recorded in `qds_runs`; counters exhaustive (`plan_files_written + plan_write_failures == plans_count` when `--plans`).
- **SQL safety** — source queries are static `SELECT`s; the time window is passed as **bound parameters**; only `--database` is interpolated as a bracket-escaped identifier (`]`→`]]`, the `EstimatedPlanService.CaptureAsync` pattern). DuckDB inserts use bound `$params` (the existing `Add` helper strips the leading `$`). `.sqlplan` filenames are the integer `plan_id` under a fixed `plans/qds/` subfolder.
- **READ UNCOMMITTED** on the extraction connection (`SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;`).
- **KISS** — primary-constructor service, plain records, no DI/interfaces.
- **Extractor version** is a constant `QueryStoreImportService.Version = 1`, persisted on `qds_runs.extractor_version`.
- `git` is wrapped with `rtk`; commits co-authored with the trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Work stays on branch `feat/query-store-extraction`.
- **QS column-name note:** the exact `sys.query_store_*` column names used in Task 5 are documented assumptions, **verified by the live `[SkippableFact]`** against a real Query Store (the precedent set by the blocking-ingestion plan). If the live test surfaces a name mismatch, fix the SELECT — the table schema and DTOs do not change.

---

## File Structure

- `src/SqlFerret.Core/Storage/QdsRows.cs` (new) — row DTO records + `QdsSchema.RuntimeMetrics` (the single metric list driving the generated columns).
- `src/SqlFerret.Core/Storage/DuckDbProject.cs` (modify) — make the class `partial`; call `CreateQdsSchema(conn)` from `CreateSchema`.
- `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs` (new, partial) — `qds_*` schema + `BeginQdsRun`/`InsertQds*`/`FinishQdsRun`.
- `src/SqlFerret.Core/Server/QueryStoreWindow.cs` (new) — pure `--from/--to/--last` parser.
- `src/SqlFerret.Core/Server/QueryStoreImportService.cs` (new) — the SqlClient ETL + options/result records.
- `src/SqlFerret.Cli/Program.cs` (modify) — `query-store-import` command.
- Tests: `QdsStorageTests.cs`, `QueryStoreWindowTests.cs`, `QueryStoreImportServiceTests.cs` (new).

---

## Task 1: qds run lifecycle + schema scaffolding

**Files:**
- Create: `src/SqlFerret.Core/Storage/QdsRows.cs`
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (add `partial`; call `CreateQdsSchema`)
- Create: `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs`
- Test: `tests/SqlFerret.Core.Tests/QdsStorageTests.cs`

**Interfaces:**
- Produces:
  - `static class QdsSchema { static readonly (string Duck, string Qs)[] RuntimeMetrics; }`
  - `record QdsRunInfo(string? ServerName, string? DatabaseName, DateTime? WindowFrom, DateTime? WindowTo, string? SqlServerVersion, string? ActualState, string? DesiredState, bool WaitStatsAvailable, bool PlansRequested)`
  - `record QdsRunCounters(long QueriesCount, long QueryTextCount, long PlansCount, long RuntimeStatRows, long WaitStatRows, long PlanFilesWritten, long PlanWriteFailures)`
  - `long DuckDbProject.BeginQdsRun(QdsRunInfo info)` (inserts `qds_runs`, zeroed counters, `captured_at = now()`, returns `run_id`)
  - `void DuckDbProject.FinishQdsRun(long runId, QdsRunCounters c)`

- [ ] **Step 1: Write the failing test**

Create `tests/SqlFerret.Core.Tests/QdsStorageTests.cs`:

```csharp
using SqlFerret.Core.Storage;
using Xunit;

public class QdsStorageTests
{
    static string NewDb() => Path.Combine(Path.GetTempPath(), $"qds_{Guid.NewGuid():N}.duckdb");

    [Fact]
    public void BeginQdsRun_then_FinishQdsRun_roundtrips()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginQdsRun(new QdsRunInfo(
                ServerName: "SRV1", DatabaseName: "Sales",
                WindowFrom: new DateTime(2026, 6, 1), WindowTo: new DateTime(2026, 6, 2),
                SqlServerVersion: "16.0.1000", ActualState: "READ_WRITE", DesiredState: "READ_WRITE",
                WaitStatsAvailable: true, PlansRequested: true));
            Assert.Equal(1L, run);

            p.FinishQdsRun(run, new QdsRunCounters(
                QueriesCount: 10, QueryTextCount: 8, PlansCount: 12,
                RuntimeStatRows: 100, WaitStatRows: 40,
                PlanFilesWritten: 11, PlanWriteFailures: 1));

            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT server_name FROM qds_runs"; Assert.Equal("SRV1", c.ExecuteScalar());
            c.CommandText = "SELECT plans_count FROM qds_runs"; Assert.Equal(12L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT plan_files_written FROM qds_runs"; Assert.Equal(11L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT wait_stats_available FROM qds_runs"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
            c.CommandText = "SELECT window_from IS NOT NULL FROM qds_runs"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
            c.CommandText = "SELECT extractor_version FROM qds_runs"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Second_run_gets_incrementing_id()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var a = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, false, false));
            var b = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, false, false));
            Assert.Equal(a + 1, b);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter QdsStorageTests`
Expected: FAIL — `QdsRunInfo`/`BeginQdsRun` do not exist (compile error).

- [ ] **Step 3: Create the DTO + metric list**

Create `src/SqlFerret.Core/Storage/QdsRows.cs`:

```csharp
namespace SqlFerret.Core.Storage;

/// <summary>The single source of the Query Store runtime-stats metric set. Each entry yields five
/// generated columns (avg_/min_/max_/last_/stdev_<Duck>) read from QS columns (avg_/.../stdev_<Qs>).</summary>
public static class QdsSchema
{
    // (DuckDB column prefix, Query Store source column base).
    public static readonly (string Duck, string Qs)[] RuntimeMetrics =
    [
        ("duration_us", "duration"),                            // µs (QS native)
        ("cpu_time_us", "cpu_time"),                            // µs
        ("clr_time_us", "clr_time"),                            // µs
        ("logical_io_reads", "logical_io_reads"),              // 8KB pages
        ("logical_io_writes", "logical_io_writes"),            // 8KB pages
        ("physical_io_reads", "physical_io_reads"),            // 8KB pages
        ("rowcount", "rowcount"),                              // rows
        ("dop", "dop"),                                        // degree of parallelism
        ("query_max_used_memory_8kb_pages", "query_max_used_memory"),
        ("tempdb_space_used_8kb_pages", "tempdb_space_used"),  // 2017+
        ("log_bytes_used", "log_bytes_used"),                  // 2017+, bytes
    ];

    // Metrics added in SQL Server 2017 (NULL on 2016).
    public static readonly HashSet<string> V2017Plus = ["tempdb_space_used", "log_bytes_used"];
}

/// <summary>One aggregate set for a metric; any value may be NULL (QS NULLs, or 2017+ metric on 2016).</summary>
public readonly record struct MetricAggregate(long? Avg, long? Min, long? Max, long? Last, double? Stdev);

public record QdsRunInfo(string? ServerName, string? DatabaseName, DateTime? WindowFrom, DateTime? WindowTo,
    string? SqlServerVersion, string? ActualState, string? DesiredState, bool WaitStatsAvailable, bool PlansRequested);

public record QdsRunCounters(long QueriesCount, long QueryTextCount, long PlansCount,
    long RuntimeStatRows, long WaitStatRows, long PlanFilesWritten, long PlanWriteFailures);

public record QdsQueryTextRow(long QueryTextId, string? QuerySqlText, bool IsPartOfEncryptedModule, bool HasRestrictedText);

public record QdsQueryRow(long QueryId, long? QueryTextId, long? ObjectId, string? ObjectName,
    string? QueryHash, string? QueryParameterizationType, bool IsInternalQuery, long CountCompiles, DateTime? LastExecutionTime);

public record QdsPlanRow(long PlanId, long QueryId, string? QueryPlanHash, string? EngineVersion,
    int? CompatibilityLevel, bool IsForcedPlan, bool IsTrivialPlan, bool IsParallelPlan,
    int ForceFailureCount, string? LastForceFailureReasonDesc, long CountCompiles, DateTime? LastExecutionTime,
    string? SqlplanPath, bool PlanWritten);

public record QdsRuntimeStatRow(long RuntimeStatsId, long PlanId, long IntervalId,
    DateTime IntervalStart, DateTime IntervalEnd, string? ExecutionType, long CountExecutions,
    IReadOnlyList<MetricAggregate> Metrics); // length 11, in QdsSchema.RuntimeMetrics order

public record QdsWaitStatRow(long WaitStatsId, long PlanId, long IntervalId, string? WaitCategory,
    string? ExecutionType, long CountExecutions, MetricAggregate WaitTimeUs, long TotalWaitTimeUs);
```

- [ ] **Step 4: Make DuckDbProject partial and call the qds schema builder**

In `src/SqlFerret.Core/Storage/DuckDbProject.cs`, change the class declaration:

```csharp
public sealed partial class DuckDbProject : IDisposable
```

Then, inside `CreateSchema`, after `cmd.ExecuteNonQuery();` (the end of the existing DDL), add the qds-schema call. Locate:

```csharp
        """;
        cmd.ExecuteNonQuery();
    }
```
(the closing of `CreateSchema`) and change it to:

```csharp
        """;
        cmd.ExecuteNonQuery();
        CreateQdsSchema(conn);
    }
```

- [ ] **Step 5: Create the qds schema + run lifecycle (partial file)**

Create `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs`:

```csharp
using System.Linq;
using DuckDB.NET.Data;

namespace SqlFerret.Core.Storage;

public sealed partial class DuckDbProject
{
    public const int QdsExtractorVersion = 1;

    private long _nextQdsRunId = -1;

    private static string RuntimeMetricColumns() =>
        string.Join(",\n          ", QdsSchema.RuntimeMetrics.Select(m =>
            $"avg_{m.Duck} BIGINT, min_{m.Duck} BIGINT, max_{m.Duck} BIGINT, last_{m.Duck} BIGINT, stdev_{m.Duck} DOUBLE"));

    private static void CreateQdsSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
        CREATE TABLE IF NOT EXISTS qds_runs (
          run_id BIGINT PRIMARY KEY, server_name TEXT, database_name TEXT, captured_at TIMESTAMP,
          window_from TIMESTAMP, window_to TIMESTAMP,
          sql_server_version TEXT, query_store_actual_state TEXT, query_store_desired_state TEXT,
          wait_stats_available BOOLEAN, plans_requested BOOLEAN,
          queries_count BIGINT, query_text_count BIGINT, plans_count BIGINT,
          runtime_stat_rows BIGINT, wait_stat_rows BIGINT,
          plan_files_written BIGINT, plan_write_failures BIGINT, extractor_version INTEGER);

        CREATE TABLE IF NOT EXISTS qds_query_text (
          run_id BIGINT, query_text_id BIGINT, query_sql_text TEXT,
          is_part_of_encrypted_module BOOLEAN, has_restricted_text BOOLEAN,
          PRIMARY KEY (run_id, query_text_id));

        CREATE TABLE IF NOT EXISTS qds_queries (
          run_id BIGINT, query_id BIGINT, query_text_id BIGINT, object_id BIGINT, object_name TEXT,
          query_hash TEXT, query_parameterization_type TEXT, is_internal_query BOOLEAN,
          count_compiles BIGINT, last_execution_time TIMESTAMP,
          PRIMARY KEY (run_id, query_id));

        CREATE TABLE IF NOT EXISTS qds_plans (
          run_id BIGINT, plan_id BIGINT, query_id BIGINT, query_plan_hash TEXT,
          engine_version TEXT, compatibility_level INTEGER,
          is_forced_plan BOOLEAN, is_trivial_plan BOOLEAN, is_parallel_plan BOOLEAN,
          force_failure_count INTEGER, last_force_failure_reason_desc TEXT,
          count_compiles BIGINT, last_execution_time TIMESTAMP,
          sqlplan_path TEXT, plan_written BOOLEAN,
          PRIMARY KEY (run_id, plan_id));

        CREATE TABLE IF NOT EXISTS qds_runtime_stats (
          run_id BIGINT, runtime_stats_id BIGINT, plan_id BIGINT, runtime_stats_interval_id BIGINT,
          interval_start_time TIMESTAMP, interval_end_time TIMESTAMP, execution_type TEXT,
          count_executions BIGINT,
          {RuntimeMetricColumns()},
          PRIMARY KEY (run_id, runtime_stats_id));

        CREATE TABLE IF NOT EXISTS qds_wait_stats (
          run_id BIGINT, wait_stats_id BIGINT, plan_id BIGINT, runtime_stats_interval_id BIGINT,
          wait_category TEXT, execution_type TEXT, count_executions BIGINT,
          total_query_wait_time_us BIGINT,
          avg_query_wait_time_us BIGINT, min_query_wait_time_us BIGINT,
          max_query_wait_time_us BIGINT, last_query_wait_time_us BIGINT, stdev_query_wait_time_us DOUBLE,
          PRIMARY KEY (run_id, wait_stats_id));
        """;
        cmd.ExecuteNonQuery();
    }

    public long BeginQdsRun(QdsRunInfo info)
    {
        if (_nextQdsRunId < 0) _nextQdsRunId = Scalar("SELECT COALESCE(MAX(run_id),0) FROM qds_runs") + 1;
        long runId = _nextQdsRunId++;
        using var c = Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO qds_runs(run_id, server_name, database_name, captured_at, window_from, window_to,
            sql_server_version, query_store_actual_state, query_store_desired_state,
            wait_stats_available, plans_requested,
            queries_count, query_text_count, plans_count, runtime_stat_rows, wait_stat_rows,
            plan_files_written, plan_write_failures, extractor_version)
          VALUES ($id,$srv,$db, now(), $wf,$wt, $ver,$as,$ds, $wsa,$pr, 0,0,0,0,0, 0,0, $ev)
          """;
        Add(c, "$id", runId); Add(c, "$srv", (object?)info.ServerName); Add(c, "$db", (object?)info.DatabaseName);
        Add(c, "$wf", (object?)info.WindowFrom); Add(c, "$wt", (object?)info.WindowTo);
        Add(c, "$ver", (object?)info.SqlServerVersion); Add(c, "$as", (object?)info.ActualState); Add(c, "$ds", (object?)info.DesiredState);
        Add(c, "$wsa", info.WaitStatsAvailable); Add(c, "$pr", info.PlansRequested); Add(c, "$ev", QdsExtractorVersion);
        c.ExecuteNonQuery();
        return runId;
    }

    public void FinishQdsRun(long runId, QdsRunCounters c)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
          UPDATE qds_runs SET queries_count=$q, query_text_count=$qt, plans_count=$p,
            runtime_stat_rows=$rt, wait_stat_rows=$w, plan_files_written=$pw, plan_write_failures=$pf
          WHERE run_id=$id
          """;
        Add(cmd, "$q", c.QueriesCount); Add(cmd, "$qt", c.QueryTextCount); Add(cmd, "$p", c.PlansCount);
        Add(cmd, "$rt", c.RuntimeStatRows); Add(cmd, "$w", c.WaitStatRows);
        Add(cmd, "$pw", c.PlanFilesWritten); Add(cmd, "$pf", c.PlanWriteFailures); Add(cmd, "$id", runId);
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test --filter QdsStorageTests`
Expected: PASS (2 tests).

- [ ] **Step 7: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Storage/QdsRows.cs src/SqlFerret.Core/Storage/DuckDbProject.cs src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs tests/SqlFerret.Core.Tests/QdsStorageTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): qds_* DuckDB schema + run lifecycle

partial DuckDbProject + QdsRows DTOs. qds_runs/query_text/queries/plans/runtime_stats/
wait_stats tables; runtime-stats metric columns generated from QdsSchema.RuntimeMetrics.
BeginQdsRun/FinishQdsRun mirror the ingestion_runs lifecycle.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: qds metadata inserts (query text, queries, plans)

**Files:**
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs`
- Test: `tests/SqlFerret.Core.Tests/QdsStorageTests.cs`

**Interfaces:**
- Consumes: `QdsQueryTextRow`, `QdsQueryRow`, `QdsPlanRow` (Task 1).
- Produces:
  - `void DuckDbProject.InsertQdsQueryText(long runId, IReadOnlyList<QdsQueryTextRow> rows)`
  - `void DuckDbProject.InsertQdsQueries(long runId, IReadOnlyList<QdsQueryRow> rows)`
  - `void DuckDbProject.InsertQdsPlans(long runId, IReadOnlyList<QdsPlanRow> rows)`

- [ ] **Step 1: Write the failing test**

Append to `tests/SqlFerret.Core.Tests/QdsStorageTests.cs` (inside the class):

```csharp
    [Fact]
    public void Insert_metadata_rows_bind_columns()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, false, true));

            p.InsertQdsQueryText(run, [new QdsQueryTextRow(7, "SELECT * FROM dbo.Orders WHERE id=@p", false, false)]);
            p.InsertQdsQueries(run, [new QdsQueryRow(3, 7, 100, "dbo.Orders", "0xABCD", "None", false, 2, new DateTime(2026, 6, 1))]);
            p.InsertQdsPlans(run, [new QdsPlanRow(5, 3, "0xPLAN", "16.0", 150, true, false, true, 0, null, 1, new DateTime(2026, 6, 1), "qds/5.sqlplan", true)]);

            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT query_sql_text FROM qds_query_text WHERE query_text_id=7";
            Assert.Equal("SELECT * FROM dbo.Orders WHERE id=@p", c.ExecuteScalar());
            c.CommandText = "SELECT object_name FROM qds_queries WHERE query_id=3"; Assert.Equal("dbo.Orders", c.ExecuteScalar());
            c.CommandText = "SELECT is_forced_plan FROM qds_plans WHERE plan_id=5"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
            c.CommandText = "SELECT sqlplan_path FROM qds_plans WHERE plan_id=5"; Assert.Equal("qds/5.sqlplan", c.ExecuteScalar());
            c.CommandText = "SELECT plan_written FROM qds_plans WHERE plan_id=5"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_plan_without_file_stores_null_path()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, false, false));
            p.InsertQdsPlans(run, [new QdsPlanRow(9, 1, null, null, null, false, false, false, 0, null, 0, null, null, false)]);
            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT sqlplan_path IS NULL FROM qds_plans WHERE plan_id=9"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
            c.CommandText = "SELECT plan_written FROM qds_plans WHERE plan_id=9"; Assert.False(Convert.ToBoolean(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter QdsStorageTests`
Expected: FAIL — `InsertQdsQueryText` etc. do not exist.

- [ ] **Step 3: Add the insert methods**

Append to the `DuckDbProject` partial in `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs` (inside the class):

```csharp
    public void InsertQdsQueryText(long runId, IReadOnlyList<QdsQueryTextRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_query_text VALUES ($run,$id,$txt,$enc,$rst)";
            Add(c, "$run", runId); Add(c, "$id", r.QueryTextId); Add(c, "$txt", (object?)r.QuerySqlText);
            Add(c, "$enc", r.IsPartOfEncryptedModule); Add(c, "$rst", r.HasRestrictedText);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertQdsQueries(long runId, IReadOnlyList<QdsQueryRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_queries VALUES ($run,$id,$tid,$oid,$obj,$qh,$pt,$int,$cc,$let)";
            Add(c, "$run", runId); Add(c, "$id", r.QueryId); Add(c, "$tid", (object?)r.QueryTextId);
            Add(c, "$oid", (object?)r.ObjectId); Add(c, "$obj", (object?)r.ObjectName); Add(c, "$qh", (object?)r.QueryHash);
            Add(c, "$pt", (object?)r.QueryParameterizationType); Add(c, "$int", r.IsInternalQuery);
            Add(c, "$cc", r.CountCompiles); Add(c, "$let", (object?)r.LastExecutionTime);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertQdsPlans(long runId, IReadOnlyList<QdsPlanRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_plans VALUES ($run,$id,$qid,$qph,$ev,$cl,$f,$t,$par,$ffc,$frr,$cc,$let,$path,$pw)";
            Add(c, "$run", runId); Add(c, "$id", r.PlanId); Add(c, "$qid", r.QueryId); Add(c, "$qph", (object?)r.QueryPlanHash);
            Add(c, "$ev", (object?)r.EngineVersion); Add(c, "$cl", (object?)r.CompatibilityLevel);
            Add(c, "$f", r.IsForcedPlan); Add(c, "$t", r.IsTrivialPlan); Add(c, "$par", r.IsParallelPlan);
            Add(c, "$ffc", r.ForceFailureCount); Add(c, "$frr", (object?)r.LastForceFailureReasonDesc);
            Add(c, "$cc", r.CountCompiles); Add(c, "$let", (object?)r.LastExecutionTime);
            Add(c, "$path", (object?)r.SqlplanPath); Add(c, "$pw", r.PlanWritten);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter QdsStorageTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs tests/SqlFerret.Core.Tests/QdsStorageTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): qds metadata inserts (query text, queries, plans)

InsertQdsQueryText/Queries/Plans, batched transactions, bound $params.
sqlplan_path/plan_written reflect whether a .sqlplan was written.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: qds stats inserts (runtime stats via metric generation, wait stats)

**Files:**
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs`
- Test: `tests/SqlFerret.Core.Tests/QdsStorageTests.cs`

**Interfaces:**
- Consumes: `QdsRuntimeStatRow`, `QdsWaitStatRow`, `MetricAggregate`, `QdsSchema.RuntimeMetrics` (Task 1).
- Produces:
  - `void DuckDbProject.InsertQdsRuntimeStats(long runId, IReadOnlyList<QdsRuntimeStatRow> rows)`
  - `void DuckDbProject.InsertQdsWaitStats(long runId, IReadOnlyList<QdsWaitStatRow> rows)`

- [ ] **Step 1: Write the failing test**

Append to `tests/SqlFerret.Core.Tests/QdsStorageTests.cs` (inside the class):

```csharp
    static IReadOnlyList<MetricAggregate> ElevenMetrics(long seed)
    {
        var list = new List<MetricAggregate>();
        for (int i = 0; i < QdsSchema.RuntimeMetrics.Length; i++)
            list.Add(new MetricAggregate(seed + i, 0, seed + i + 100, seed + i, 1.5));
        return list;
    }

    [Fact]
    public void Insert_runtime_stats_binds_generated_metric_columns()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, false, false));
            p.InsertQdsRuntimeStats(run, [new QdsRuntimeStatRow(
                RuntimeStatsId: 1, PlanId: 5, IntervalId: 42,
                IntervalStart: new DateTime(2026, 6, 1, 10, 0, 0), IntervalEnd: new DateTime(2026, 6, 1, 11, 0, 0),
                ExecutionType: "Regular", CountExecutions: 9, Metrics: ElevenMetrics(1000))]);

            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT count_executions FROM qds_runtime_stats WHERE runtime_stats_id=1";
            Assert.Equal(9L, Convert.ToInt64(c.ExecuteScalar()));
            // first metric is duration_us → avg = seed+0 = 1000
            c.CommandText = "SELECT avg_duration_us FROM qds_runtime_stats WHERE runtime_stats_id=1";
            Assert.Equal(1000L, Convert.ToInt64(c.ExecuteScalar()));
            // last metric (index 10) is log_bytes_used → avg = 1000+10 = 1010
            c.CommandText = "SELECT avg_log_bytes_used FROM qds_runtime_stats WHERE runtime_stats_id=1";
            Assert.Equal(1010L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT stdev_duration_us FROM qds_runtime_stats WHERE runtime_stats_id=1";
            Assert.Equal(1.5, Convert.ToDouble(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_runtime_stats_null_metric_stored_null()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, false, false));
            var metrics = ElevenMetrics(1).ToList();
            metrics[10] = new MetricAggregate(null, null, null, null, null); // log_bytes_used absent (2016)
            p.InsertQdsRuntimeStats(run, [new QdsRuntimeStatRow(2, 5, 1, new DateTime(2026,6,1), new DateTime(2026,6,1), "Regular", 1, metrics)]);
            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT avg_log_bytes_used IS NULL FROM qds_runtime_stats WHERE runtime_stats_id=2";
            Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_wait_stats_stores_microseconds()
    {
        var path = NewDb();
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginQdsRun(new QdsRunInfo(null, null, null, null, null, null, null, true, false));
            p.InsertQdsWaitStats(run, [new QdsWaitStatRow(1, 5, 42, "CPU", "Regular", 3,
                new MetricAggregate(2000, 1000, 5000, 2000, 0.5), TotalWaitTimeUs: 6000)]);
            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT avg_query_wait_time_us FROM qds_wait_stats WHERE wait_stats_id=1";
            Assert.Equal(2000L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT total_query_wait_time_us FROM qds_wait_stats WHERE wait_stats_id=1";
            Assert.Equal(6000L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter QdsStorageTests`
Expected: FAIL — `InsertQdsRuntimeStats`/`InsertQdsWaitStats` do not exist.

- [ ] **Step 3: Add the stats insert methods (runtime SQL generated from the metric list)**

Append to the `DuckDbProject` partial in `src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs` (inside the class):

```csharp
    private static readonly string RuntimeInsertSql = BuildRuntimeInsertSql();

    private static string BuildRuntimeInsertSql()
    {
        var baseCols = "run_id, runtime_stats_id, plan_id, runtime_stats_interval_id, " +
                       "interval_start_time, interval_end_time, execution_type, count_executions";
        var basePh = "$run,$id,$plan,$iid,$istart,$iend,$etype,$cnt";
        var metricCols = string.Join(", ", QdsSchema.RuntimeMetrics.SelectMany(m =>
            new[] { $"avg_{m.Duck}", $"min_{m.Duck}", $"max_{m.Duck}", $"last_{m.Duck}", $"stdev_{m.Duck}" }));
        var metricPh = string.Join(", ", QdsSchema.RuntimeMetrics.Select((_, i) =>
            $"$a{i},$mn{i},$mx{i},$ls{i},$sd{i}").ToArray());
        return $"INSERT INTO qds_runtime_stats ({baseCols}, {metricCols}) VALUES ({basePh}, {metricPh})";
    }

    public void InsertQdsRuntimeStats(long runId, IReadOnlyList<QdsRuntimeStatRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = RuntimeInsertSql;
            Add(c, "$run", runId); Add(c, "$id", r.RuntimeStatsId); Add(c, "$plan", r.PlanId); Add(c, "$iid", r.IntervalId);
            Add(c, "$istart", r.IntervalStart); Add(c, "$iend", r.IntervalEnd); Add(c, "$etype", (object?)r.ExecutionType);
            Add(c, "$cnt", r.CountExecutions);
            for (int i = 0; i < QdsSchema.RuntimeMetrics.Length; i++)
            {
                var m = r.Metrics[i];
                Add(c, $"$a{i}", (object?)m.Avg); Add(c, $"$mn{i}", (object?)m.Min); Add(c, $"$mx{i}", (object?)m.Max);
                Add(c, $"$ls{i}", (object?)m.Last); Add(c, $"$sd{i}", (object?)m.Stdev);
            }
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertQdsWaitStats(long runId, IReadOnlyList<QdsWaitStatRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_wait_stats VALUES ($run,$id,$plan,$iid,$cat,$etype,$cnt,$tot,$avg,$min,$max,$last,$sd)";
            Add(c, "$run", runId); Add(c, "$id", r.WaitStatsId); Add(c, "$plan", r.PlanId); Add(c, "$iid", r.IntervalId);
            Add(c, "$cat", (object?)r.WaitCategory); Add(c, "$etype", (object?)r.ExecutionType); Add(c, "$cnt", r.CountExecutions);
            Add(c, "$tot", r.TotalWaitTimeUs);
            Add(c, "$avg", (object?)r.WaitTimeUs.Avg); Add(c, "$min", (object?)r.WaitTimeUs.Min);
            Add(c, "$max", (object?)r.WaitTimeUs.Max); Add(c, "$last", (object?)r.WaitTimeUs.Last);
            Add(c, "$sd", (object?)r.WaitTimeUs.Stdev);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter QdsStorageTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Storage/DuckDbProject.QueryStore.cs tests/SqlFerret.Core.Tests/QdsStorageTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): qds runtime + wait stats inserts

InsertQdsRuntimeStats binds the 55 metric columns generated from
QdsSchema.RuntimeMetrics (NULL-safe). InsertQdsWaitStats stores microseconds.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: time-window parser

**Files:**
- Create: `src/SqlFerret.Core/Server/QueryStoreWindow.cs`
- Test: `tests/SqlFerret.Core.Tests/QueryStoreWindowTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct QueryStoreWindow(DateTime? From, DateTime? To) { bool IsBounded { get; } }`
  - `static QueryStoreWindow QueryStoreWindow.Parse(string? from, string? to, string? last, DateTime now)`

- [ ] **Step 1: Write the failing test**

Create `tests/SqlFerret.Core.Tests/QueryStoreWindowTests.cs`:

```csharp
using SqlFerret.Core.Server;
using Xunit;

public class QueryStoreWindowTests
{
    static readonly DateTime Now = new(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_flags_is_unbounded()
    {
        var w = QueryStoreWindow.Parse(null, null, null, Now);
        Assert.False(w.IsBounded);
        Assert.Null(w.From); Assert.Null(w.To);
    }

    [Fact]
    public void Last_hours_sets_from_to_now_minus_span()
    {
        var w = QueryStoreWindow.Parse(null, null, "24h", Now);
        Assert.True(w.IsBounded);
        Assert.Equal(Now.AddHours(-24), w.From);
        Assert.Equal(Now, w.To);
    }

    [Fact]
    public void Last_days_supported()
    {
        var w = QueryStoreWindow.Parse(null, null, "7d", Now);
        Assert.Equal(Now.AddDays(-7), w.From);
    }

    [Fact]
    public void Explicit_from_to_parsed()
    {
        var w = QueryStoreWindow.Parse("2026-06-01T00:00:00", "2026-06-02T00:00:00", null, Now);
        Assert.Equal(new DateTime(2026, 6, 1), w.From);
        Assert.Equal(new DateTime(2026, 6, 2), w.To);
    }

    [Fact]
    public void Last_with_from_or_to_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse("2026-06-01", null, "24h", Now));
        Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse(null, "2026-06-01", "24h", Now));
    }

    [Theory]
    [InlineData("24")]     // no unit
    [InlineData("24x")]    // bad unit
    [InlineData("abc")]    // not a number
    [InlineData("-3h")]    // negative
    public void Invalid_last_is_rejected(string bad)
        => Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse(null, null, bad, Now));

    [Fact]
    public void Invalid_datetime_is_rejected()
        => Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse("not-a-date", null, null, Now));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter QueryStoreWindowTests`
Expected: FAIL — `QueryStoreWindow` does not exist.

- [ ] **Step 3: Implement the parser**

Create `src/SqlFerret.Core/Server/QueryStoreWindow.cs`:

```csharp
using System.Globalization;

namespace SqlFerret.Core.Server;

/// <summary>A resolved Query Store time window. Both bounds null ⇒ extract everything.</summary>
public readonly record struct QueryStoreWindow(DateTime? From, DateTime? To)
{
    public bool IsBounded => From is not null || To is not null;

    /// <summary>
    /// Resolves the window from CLI flags. `last` (e.g. "24h"/"7d") is mutually exclusive with
    /// `from`/`to` and resolves to (now − span, now). Throws <see cref="ArgumentException"/> on a
    /// bad value or a mutual-exclusion violation.
    /// </summary>
    public static QueryStoreWindow Parse(string? from, string? to, string? last, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(last))
        {
            if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
                throw new ArgumentException("--last cannot be combined with --from/--to");
            return new QueryStoreWindow(now - ParseSpan(last), now);
        }
        return new QueryStoreWindow(ParseDate(from), ParseDate(to));
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            throw new ArgumentException($"invalid datetime: '{s}'");
        return dt;
    }

    private static TimeSpan ParseSpan(string last)
    {
        var unit = last[^1];
        if (!int.TryParse(last[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException($"invalid --last value: '{last}' (expected e.g. 24h or 7d)");
        return unit switch
        {
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            _ => throw new ArgumentException($"invalid --last unit in '{last}' (use h or d)"),
        };
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter QueryStoreWindowTests`
Expected: PASS (all cases).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Server/QueryStoreWindow.cs tests/SqlFerret.Core.Tests/QueryStoreWindowTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): QueryStoreWindow time-window parser

--from/--to or --last <N>{h|d} (mutually exclusive); invalid values rejected.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: QueryStoreImportService (the ETL)

**Files:**
- Create: `src/SqlFerret.Core/Server/QueryStoreImportService.cs`
- Test: `tests/SqlFerret.Core.Tests/QueryStoreImportServiceTests.cs`

**Interfaces:**
- Consumes: `DuckDbProject` qds inserts (Tasks 1-3), `QueryStoreWindow` (Task 4).
- Produces:
  - `record QueryStoreImportOptions(string? Database, bool WritePlans, QueryStoreWindow Window)`
  - `record QdsImportResult(long RunId, long QueriesCount, long QueryTextCount, long PlansCount, long RuntimeStatRows, long WaitStatRows, long PlanFilesWritten, long PlanWriteFailures)`
  - `class QueryStoreImportService(string connectionString, DuckDbProject db, string plansFolder)` with `const int Version = 1` and `QdsImportResult Import(QueryStoreImportOptions opts, IProgress<string>? progress = null)`.

**Notes for the implementer:**
- The `sys.query_store_*` column names below are the documented schema; the `[SkippableFact]` is the source of truth. If it fails on an unknown column, correct the SELECT only.
- ms→µs for wait stats: QS `*_query_wait_time` columns are **milliseconds**; multiply by 1000 when building `MetricAggregate`/`TotalWaitTimeUs`.
- Reads run under READ UNCOMMITTED. `--database` is bracket-escaped. Window bounds are bound parameters (`@from`/`@to`).
- `.sqlplan` files: write to `<plansFolder>/qds/<plan_id>.sqlplan`; store `sqlplan_path = "qds/<plan_id>.sqlplan"`. A per-plan write failure is caught and counted, never fatal.

- [ ] **Step 1: Write the failing test (validator + live integration)**

Create `tests/SqlFerret.Core.Tests/QueryStoreImportServiceTests.cs`:

```csharp
using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;
using Xunit;

public class QueryStoreImportServiceTests
{
    [Fact]
    public void Version_is_one()
        => Assert.Equal(1, QueryStoreImportService.Version);

    [SkippableFact]
    public void Import_loads_query_store_end_to_end()
    {
        var connStr = Environment.GetEnvironmentVariable("SQLFERRET_TEST_CONN");
        Skip.If(string.IsNullOrEmpty(connStr), "SQLFERRET_TEST_CONN not set — skipping integration test");

        var dbPath = Path.Combine(Path.GetTempPath(), $"qds_it_{Guid.NewGuid():N}.duckdb");
        var plansDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var db = DuckDbProject.Open(dbPath);
            var svc = new QueryStoreImportService(connStr!, db, plansDir);
            var result = svc.Import(new QueryStoreImportOptions(Database: null, WritePlans: true, Window: default));

            Assert.True(result.QueriesCount > 0, "expected at least one query in the target DB's Query Store");
            Assert.True(result.RuntimeStatRows > 0);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM qds_runs"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM qds_runtime_stats"; Assert.True(Convert.ToInt64(c.ExecuteScalar()) > 0);

            if (result.PlanFilesWritten > 0)
                Assert.True(Directory.GetFiles(Path.Combine(plansDir, "qds"), "*.sqlplan").Length > 0);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            Directory.Delete(plansDir, true);
        }
    }

    [SkippableFact]
    public void Import_clean_error_when_query_store_off()
    {
        var connStr = Environment.GetEnvironmentVariable("SQLFERRET_TEST_CONN_NOQDS");
        Skip.If(string.IsNullOrEmpty(connStr), "SQLFERRET_TEST_CONN_NOQDS not set — skipping");
        var dbPath = Path.Combine(Path.GetTempPath(), $"qds_off_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(dbPath);
            var svc = new QueryStoreImportService(connStr!, db, Directory.CreateTempSubdirectory().FullName);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                svc.Import(new QueryStoreImportOptions(null, true, default)));
            Assert.Contains("Query Store", ex.Message);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter QueryStoreImportServiceTests`
Expected: FAIL — `QueryStoreImportService` does not exist (the `Version_is_one` fact fails to compile; the `[SkippableFact]`s skip without the env vars).

- [ ] **Step 3: Implement the service**

Create `src/SqlFerret.Core/Server/QueryStoreImportService.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Server;

public record QueryStoreImportOptions(string? Database, bool WritePlans, QueryStoreWindow Window);

public record QdsImportResult(long RunId, long QueriesCount, long QueryTextCount, long PlansCount,
    long RuntimeStatRows, long WaitStatRows, long PlanFilesWritten, long PlanWriteFailures);

/// <summary>
/// Reads a database's Query Store (all of it, or a time window) into the project's qds_* tables and
/// writes each plan as a .sqlplan file. Read-only against sys.query_store_*, under READ UNCOMMITTED.
/// </summary>
public class QueryStoreImportService(string connectionString, DuckDbProject db, string plansFolder)
{
    public const int Version = 1;

    public QdsImportResult Import(QueryStoreImportOptions opts, IProgress<string>? progress = null)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        Exec(conn, "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;");
        if (!string.IsNullOrWhiteSpace(opts.Database))
            Exec(conn, $"USE [{opts.Database!.Replace("]", "]]")}];");

        var (actual, desired) = ReadQueryStoreState(conn);
        if (actual is null || actual is "OFF" or "ERROR")
            throw new InvalidOperationException(
                $"Query Store is not enabled on the target database (actual_state={actual ?? "absent"})");

        var version = ReadScalarString(conn, "SELECT CONVERT(sysname, SERVERPROPERTY('ProductVersion'))");
        var serverName = ReadScalarString(conn, "SELECT CONVERT(sysname, SERVERPROPERTY('ServerName'))");
        var databaseName = ReadScalarString(conn, "SELECT DB_NAME()");
        bool waitAvailable = ObjectExists(conn, "sys.query_store_wait_stats");

        var window = opts.Window;
        long runId = db.BeginQdsRun(new QdsRunInfo(serverName, databaseName, window.From, window.To,
            version, actual, desired, waitAvailable, opts.WritePlans));

        // 1. query text
        var qtCount = StreamInsert(conn, QueryTextSql(window), window, progress, "query text",
            rows => db.InsertQdsQueryText(runId, rows), ReadQueryText);

        // 2. queries
        var qCount = StreamInsert(conn, QueriesSql(window), window, progress, "queries",
            rows => db.InsertQdsQueries(runId, rows), ReadQuery);

        // 3. plans (+ optional .sqlplan write). Counters are mutated inside the reader lambda
        // (locals are captured by reference; a `ref` parameter could not be).
        long plansWritten = 0, planFailures = 0;
        var planRows = ReadAll(conn, PlansSql(window), window, r =>
        {
            long planId = Convert.ToInt64(r.GetValue(0));
            string? path = null; bool ok = false;
            if (opts.WritePlans)
            {
                try { path = WritePlan(planId, S(r, 12) ?? ""); ok = true; plansWritten++; }
                catch { planFailures++; }
            }
            return new QdsPlanRow(planId, Convert.ToInt64(r.GetValue(1)), S(r, 2), S(r, 3),
                r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4)),
                Convert.ToBoolean(r.GetValue(5)), Convert.ToBoolean(r.GetValue(6)), Convert.ToBoolean(r.GetValue(7)),
                Convert.ToInt32(r.GetValue(8)), S(r, 9), Convert.ToInt64(r.GetValue(10)), Dt(r, 11), path, ok);
        });
        db.InsertQdsPlans(runId, planRows);
        progress?.Report($"plans {planRows.Count}");

        // 4. runtime stats
        var rtCount = StreamInsert(conn, RuntimeStatsSql(window, version), window, progress, "runtime stats",
            rows => db.InsertQdsRuntimeStats(runId, rows), ReadRuntimeStat);

        // 5. wait stats (when available)
        long waitCount = 0;
        if (waitAvailable)
            waitCount = StreamInsert(conn, WaitStatsSql(window), window, progress, "wait stats",
                rows => db.InsertQdsWaitStats(runId, rows), ReadWaitStat);

        var counters = new QdsRunCounters(qCount, qtCount, planRows.Count, rtCount, waitCount, plansWritten, planFailures);
        db.FinishQdsRun(runId, counters);
        return new QdsImportResult(runId, qCount, qtCount, planRows.Count, rtCount, waitCount, plansWritten, planFailures);
    }

    // ---- plan file writing ----------------------------------------------------------------------

    private string? WritePlan(long planId, string xml)
    {
        var dir = Path.Combine(plansFolder, "qds");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{planId}.sqlplan"), xml);
        return $"qds/{planId}.sqlplan";
    }

    // ---- source SQL (window-filtered; see note about exact column names) ------------------------

    private static string Where(QueryStoreWindow w, string intervalAlias) =>
        w.IsBounded
            ? $" WHERE {intervalAlias}.start_time < @to AND {intervalAlias}.end_time > @from "
            : " ";

    // query text used by queries active in window (or all)
    private static string QueryTextSql(QueryStoreWindow w) => w.IsBounded
        ? """
          SELECT DISTINCT qt.query_text_id, qt.query_sql_text, qt.is_part_of_encrypted_module, qt.has_restricted_text
          FROM sys.query_store_query_text qt
          JOIN sys.query_store_query q ON q.query_text_id = qt.query_text_id
          JOIN sys.query_store_plan p ON p.query_id = q.query_id
          JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          WHERE i.start_time < @to AND i.end_time > @from
          """
        : "SELECT query_text_id, query_sql_text, is_part_of_encrypted_module, has_restricted_text FROM sys.query_store_query_text";

    private static string QueriesSql(QueryStoreWindow w) => w.IsBounded
        ? """
          SELECT DISTINCT q.query_id, q.query_text_id, q.object_id,
            CASE WHEN q.object_id IS NULL THEN NULL ELSE QUOTENAME(OBJECT_SCHEMA_NAME(q.object_id)) + '.' + QUOTENAME(OBJECT_NAME(q.object_id)) END AS object_name,
            CONVERT(varchar(34), q.query_hash, 1) AS query_hash, q.query_parameterization_type_desc, q.is_internal_query,
            q.count_compiles, q.last_execution_time
          FROM sys.query_store_query q
          JOIN sys.query_store_plan p ON p.query_id = q.query_id
          JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          WHERE i.start_time < @to AND i.end_time > @from
          """
        : """
          SELECT q.query_id, q.query_text_id, q.object_id,
            CASE WHEN q.object_id IS NULL THEN NULL ELSE QUOTENAME(OBJECT_SCHEMA_NAME(q.object_id)) + '.' + QUOTENAME(OBJECT_NAME(q.object_id)) END AS object_name,
            CONVERT(varchar(34), q.query_hash, 1) AS query_hash, q.query_parameterization_type_desc, q.is_internal_query,
            q.count_compiles, q.last_execution_time
          FROM sys.query_store_query q
          """;

    private static string PlansSql(QueryStoreWindow w) => w.IsBounded
        ? """
          SELECT DISTINCT p.plan_id, p.query_id, CONVERT(varchar(34), p.query_plan_hash, 1) AS query_plan_hash,
            p.engine_version, p.compatibility_level, p.is_forced_plan, p.is_trivial_plan, p.is_parallel_plan,
            p.force_failure_count, p.last_force_failure_reason_desc, p.count_compiles, p.last_execution_time, p.query_plan
          FROM sys.query_store_plan p
          JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          WHERE i.start_time < @to AND i.end_time > @from
          """
        : """
          SELECT p.plan_id, p.query_id, CONVERT(varchar(34), p.query_plan_hash, 1) AS query_plan_hash,
            p.engine_version, p.compatibility_level, p.is_forced_plan, p.is_trivial_plan, p.is_parallel_plan,
            p.force_failure_count, p.last_force_failure_reason_desc, p.count_compiles, p.last_execution_time, p.query_plan
          FROM sys.query_store_plan p
          """;

    private static string RuntimeStatsSql(QueryStoreWindow w, string? version)
    {
        bool v2017 = SupportsV2017(version);
        var cols = string.Join(", ", QdsSchema.RuntimeMetrics.SelectMany(m =>
        {
            if (!v2017 && QdsSchema.V2017Plus.Contains(m.Qs))
                return new[] { $"CAST(NULL AS BIGINT) AS avg_{m.Qs}", $"CAST(NULL AS BIGINT) AS min_{m.Qs}",
                               $"CAST(NULL AS BIGINT) AS max_{m.Qs}", $"CAST(NULL AS BIGINT) AS last_{m.Qs}",
                               $"CAST(NULL AS FLOAT) AS stdev_{m.Qs}" };
            return new[] { $"rs.avg_{m.Qs}", $"rs.min_{m.Qs}", $"rs.max_{m.Qs}", $"rs.last_{m.Qs}", $"rs.stdev_{m.Qs}" };
        }));
        return $"""
          SELECT rs.runtime_stats_id, rs.plan_id, rs.runtime_stats_interval_id,
            i.start_time, i.end_time, rs.execution_type_desc, rs.count_executions, {cols}
          FROM sys.query_store_runtime_stats rs
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          {Where(w, "i")}
          """;
    }

    private static string WaitStatsSql(QueryStoreWindow w) => $"""
          SELECT ws.wait_stats_id, ws.plan_id, ws.runtime_stats_interval_id, ws.wait_category_desc,
            ws.execution_type_desc, ws.count_executions, ws.total_query_wait_time_ms,
            ws.avg_query_wait_time_ms, ws.min_query_wait_time_ms, ws.max_query_wait_time_ms,
            ws.last_query_wait_time_ms, ws.stdev_query_wait_time_ms
          FROM sys.query_store_wait_stats ws
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = ws.runtime_stats_interval_id
          {Where(w, "i")}
          """;

    // ---- readers (SqlDataReader → DTO) ----------------------------------------------------------

    private static long? L(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static double? D(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private static DateTime? Dt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDateTime(r.GetValue(i));

    private static QdsQueryTextRow ReadQueryText(SqlDataReader r) =>
        new(Convert.ToInt64(r.GetValue(0)), S(r, 1), Convert.ToBoolean(r.GetValue(2)), Convert.ToBoolean(r.GetValue(3)));

    private static QdsQueryRow ReadQuery(SqlDataReader r) =>
        new(Convert.ToInt64(r.GetValue(0)), L(r, 1), L(r, 2), S(r, 3), S(r, 4), S(r, 5),
            Convert.ToBoolean(r.GetValue(6)), Convert.ToInt64(r.GetValue(7)), Dt(r, 8));

    private static QdsRuntimeStatRow ReadRuntimeStat(SqlDataReader r)
    {
        const int baseCols = 7; // runtime_stats_id, plan_id, interval_id, start, end, exec_type, count
        var metrics = new List<MetricAggregate>(QdsSchema.RuntimeMetrics.Length);
        for (int m = 0; m < QdsSchema.RuntimeMetrics.Length; m++)
        {
            int b = baseCols + m * 5;
            metrics.Add(new MetricAggregate(L(r, b), L(r, b + 1), L(r, b + 2), L(r, b + 3), D(r, b + 4)));
        }
        return new QdsRuntimeStatRow(Convert.ToInt64(r.GetValue(0)), Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
            Convert.ToDateTime(r.GetValue(3)), Convert.ToDateTime(r.GetValue(4)), S(r, 5), Convert.ToInt64(r.GetValue(6)), metrics);
    }

    private static QdsWaitStatRow ReadWaitStat(SqlDataReader r)
    {
        // ms → µs at the ingestion boundary
        static long? Us(long? ms) => ms is null ? null : ms * 1000;
        static double? UsD(double? ms) => ms is null ? null : ms * 1000;
        long totalUs = (L(r, 6) ?? 0) * 1000;
        var wait = new MetricAggregate(Us(L(r, 7)), Us(L(r, 8)), Us(L(r, 9)), Us(L(r, 10)), UsD(D(r, 11)));
        return new QdsWaitStatRow(Convert.ToInt64(r.GetValue(0)), Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
            S(r, 3), S(r, 4), Convert.ToInt64(r.GetValue(5)), wait, totalUs);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private long StreamInsert<T>(SqlConnection conn, string sql, QueryStoreWindow window,
        IProgress<string>? progress, string label, Action<IReadOnlyList<T>> insert, Func<SqlDataReader, T> read)
    {
        var rows = ReadAll(conn, sql, window, read);
        insert(rows);
        progress?.Report($"{label} {rows.Count}");
        return rows.Count;
    }

    private List<T> ReadAll<T>(SqlConnection conn, string sql, QueryStoreWindow window, Func<SqlDataReader, T> read)
    {
        var list = new List<T>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0; // Query Store reads can be large; no client timeout
        if (window.IsBounded) // windowed SQL references @from/@to; bind their values
        {
            cmd.Parameters.Add(new SqlParameter("@from", System.Data.SqlDbType.DateTime2) { Value = window.From });
            cmd.Parameters.Add(new SqlParameter("@to", System.Data.SqlDbType.DateTime2) { Value = window.To });
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(read(reader));
        return list;
    }

    private (string? actual, string? desired) ReadQueryStoreState(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actual_state_desc, desired_state_desc FROM sys.database_query_store_options";
        try
        {
            using var r = cmd.ExecuteReader();
            return r.Read() ? (S(r, 0), S(r, 1)) : (null, null);
        }
        catch (SqlException) { return (null, null); } // view absent on very old servers
    }

    private static bool ObjectExists(SqlConnection conn, string viewName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@n) IS NULL THEN 0 ELSE 1 END";
        cmd.Parameters.AddWithValue("@n", viewName);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static string? ReadScalarString(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    private static void Exec(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static bool SupportsV2017(string? productVersion)
    {
        // ProductVersion like "16.0.1000.6"; major >= 14 ⇒ SQL Server 2017+.
        if (string.IsNullOrEmpty(productVersion)) return true; // assume modern when unknown
        var dot = productVersion.IndexOf('.');
        var majorStr = dot > 0 ? productVersion[..dot] : productVersion;
        return int.TryParse(majorStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) && major >= 14;
    }
}
```

> **Implementer note:** `ReadAll`/`StreamInsert` take the `QueryStoreWindow` and bind `@from`/`@to`
> only when `window.IsBounded` (windowed SQL is the only SQL that references them). The live test
> exercises the unbounded path; add a windowed assertion if a populated test DB is available.

- [ ] **Step 4: Build and run the test**

Run: `dotnet build` then `dotnet test --filter QueryStoreImportServiceTests`
Expected: build 0 warnings; `Version_is_one` PASSES; the two `[SkippableFact]`s SKIP (env vars unset) — or PASS if `SQLFERRET_TEST_CONN` points at a Query-Store-enabled DB.

- [ ] **Step 5: If a live DB is available, run the integration test**

If you have a Query-Store-enabled SQL Server: `SQLFERRET_TEST_CONN="Server=...;Database=...;..." dotnet test --filter Import_loads_query_store_end_to_end`
Expected: PASS; `qds_runtime_stats` populated; a `qds/*.sqlplan` written. Fix any `sys.query_store_*` column-name mismatch in the SELECTs (schema/DTOs unchanged).

- [ ] **Step 6: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Server/QueryStoreImportService.cs tests/SqlFerret.Core.Tests/QueryStoreImportServiceTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): QueryStoreImportService ETL

Reads sys.query_store_* under READ UNCOMMITTED (full or windowed) into qds_*;
writes plans/qds/<id>.sqlplan (counted, never fatal); ms→µs for wait stats;
version-aware runtime columns; clean error when Query Store is disabled.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: CLI `query-store-import` command

**Files:**
- Modify: `src/SqlFerret.Cli/Program.cs`
- Test: manual verification (the command path is server-gated; logic is covered by Tasks 4-5).

**Interfaces:**
- Consumes: `AuditProject` (`OpenProject()`), `QueryStoreImportService`, `QueryStoreImportOptions`, `QueryStoreWindow` (Tasks 4-5).

- [ ] **Step 1: Add the SqlClient using and the command**

In `src/SqlFerret.Cli/Program.cs`, add to the `using` block at the top:

```csharp
using Microsoft.Data.SqlClient;
using SqlFerret.Core.Server;
```

Add a new `case` to the command `switch` (after the `export-blocking` case, before `default:`):

```csharp
    case "query-store-import":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var connOverride = Arg("--conn", "");
            var connStr = connOverride.Length > 0 ? connOverride : project.Config.ConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                Console.Error.WriteLine("query-store-import: no connection string (set server.connectionString in config/.env, or pass --conn)");
                return 1;
            }

            var database = Arg("--database", "");
            var noPlans = Array.IndexOf(args, "--no-plans") >= 0;

            QueryStoreWindow window;
            try
            {
                window = QueryStoreWindow.Parse(
                    NullIfEmpty(Arg("--from", "")), NullIfEmpty(Arg("--to", "")), NullIfEmpty(Arg("--last", "")),
                    DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine($"query-store-import: {ex.Message}"); return 1; }

            if (!noPlans && !string.Equals(project.Config.RedactionPolicy, "off", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(
                    $"warning: --plans writes raw showplan XML; .sqlplan files may contain literal values not " +
                    $"covered by redaction (policy={project.Config.RedactionPolicy}). Use --no-plans to skip.");

            using var db = project.OpenDb();
            var svc = new QueryStoreImportService(connStr!, db, project.PlansFolder);
            var opts = new QueryStoreImportOptions(NullIfEmpty(database), WritePlans: !noPlans, Window: window);

            var showGauge = !Console.IsErrorRedirected;
            var progress = new SyncProgress<string>(s => { if (showGauge) Console.Error.Write("\r" + s.PadRight(60)); });

            QdsImportResult result;
            try { result = svc.Import(opts, progress); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
            {
                if (showGauge) Console.Error.WriteLine();
                Console.Error.WriteLine($"query-store-import: {ex.Message}");
                return 1;
            }
            if (showGauge) Console.Error.WriteLine();

            Console.WriteLine(
                $"qds run {result.RunId}: queries={result.QueriesCount} queryText={result.QueryTextCount} " +
                $"plans={result.PlansCount} runtimeRows={result.RuntimeStatRows} waitRows={result.WaitStatRows} " +
                $"plansWritten={result.PlanFilesWritten} planFailures={result.PlanWriteFailures}");
            return 0;
        }
```

Add this helper near the other top-level local functions (e.g. after `Arg`):

```csharp
static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
```

Update the usage line (the `args.Length == 0` branch) to mention the new command:

```csharp
    Console.Error.WriteLine("usage: import <path> --project <dir> | top-slow --project <dir> | export-blocking --project <dir> [...] | query-store-import --project <dir> [--conn <s>] [--database <db>] [--no-plans] [--from <dt> --to <dt> | --last <N>{h|d}]");
```

- [ ] **Step 2: Build (0 warnings)**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings. (The new `SyncProgress<string>` reuses the existing `file sealed class SyncProgress<T>` at the bottom of `Program.cs`.)

- [ ] **Step 3: Run the full suite**

Run: `dotnet test`
Expected: PASS, same skipped count plus the new `[SkippableFact]`s skipping (env-gated). No new failures.

- [ ] **Step 4: Manual verification**

If no live Query-Store DB is available, verify the guard paths (no server needed):

```bash
# missing connection string → clean error, exit 1
dotnet run --project src/SqlFerret.Cli -- query-store-import --project /tmp/qds-demo
# invalid window → clean error, exit 1
dotnet run --project src/SqlFerret.Cli -- query-store-import --project /tmp/qds-demo --conn "Server=x;" --last 24
```
Expected: first prints `query-store-import: no connection string ...`; second prints `query-store-import: invalid --last value ...`; both exit non-zero, no stack trace.

If a live DB is available:

```bash
dotnet run --project src/SqlFerret.Cli -- query-store-import --project /tmp/qds-demo --conn "Server=...;Database=...;..." --last 7d
```
Expected: prints `qds run N: queries=… plans=… runtimeRows=…`; `/tmp/qds-demo/plans/qds/*.sqlplan` written; reopening with `top-slow` still works (shared project).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Cli/Program.cs
rtk git commit -m "$(cat <<'EOF'
feat(cli): query-store-import command

Drives QueryStoreImportService from AuditProject: --conn/--database/--no-plans
and --from/--to|--last window. Clean errors for missing conn, bad window, QS off;
redaction warning when writing plans. Progress on stderr.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**Spec coverage:**
- Scope everything + optional window → Tasks 4 (parser), 5 (windowed SQL), 6 (flags). ✓
- One `.sqlplan` per plan under `plans/qds/`, path index `sqlplan_path` + `plan_written`; `--plans/--no-plans` → Task 2 (columns), Task 5 (`WritePlan`, counted failures), Task 6 (`--no-plans` + redaction warning). ✓
- Full per-interval runtime fidelity → Task 3 (`qds_runtime_stats` per interval). ✓
- All `qds_*` tables + exhaustive `qds_runs` counters → Tasks 1-3; `FinishQdsRun` counters; `QdsImportResult`. ✓
- Units: duration/cpu/clr µs native; wait ms→µs at boundary → Task 3 columns, Task 5 `ReadWaitStat`. ✓
- READ UNCOMMITTED, bracket-escaped `--database`, bound window params, clean errors → Task 5. ✓
- Connection from config/`.env` + `--conn` override → Task 6. ✓
- Wait-stats availability gate (2017+) → Task 5 `ObjectExists`; runtime 2017+ columns NULL on 2016 → Task 5 `SupportsV2017`/`RuntimeStatsSql`. ✓
- Tests: `qds_*` insert units, ms→µs, window parsing, live `[SkippableFact]` → Tasks 1-5. ✓
- Redaction warning + caveat → Task 6. ✓ (Plan XML written raw is the accepted, documented behavior.)

**Placeholder scan:** No TBD/TODO. Every code step has complete, compiling code: `ReadAll`/`StreamInsert` thread the `QueryStoreWindow` and bind `@from`/`@to` when bounded; the plan reader is inlined so its counters are captured locals (not an uncapturable `ref`). The one prose note in Task 5 is guidance, not a deferred fix.

**Type consistency:** `BeginQdsRun(QdsRunInfo)`, `FinishQdsRun(long, QdsRunCounters)`, `InsertQds{QueryText,Queries,Plans,RuntimeStats,WaitStats}`, `QdsSchema.RuntimeMetrics`, `MetricAggregate`, `QueryStoreWindow.Parse(...)`, `QueryStoreImportService(string, DuckDbProject, string)` / `.Import(QueryStoreImportOptions, IProgress<string>?)` / `.Version` are used identically across Tasks 1-6. `QdsRuntimeStatRow.Metrics` length 11 in `QdsSchema.RuntimeMetrics` order is honored by both the insert (Task 3) and the reader (Task 5).

## Out of scope (per spec)

Plan-XML feature parsing; AI export pack; MCP host; top-N/regression selection; incremental/delta; TUI surface; executable-query reconstruction from captured `ParameterCompiledValue` (a later iteration — the data is preserved in the `.sqlplan` files).
