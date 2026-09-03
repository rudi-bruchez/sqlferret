# System Health diagnostics ingestion — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `sqlferret import` extract the four `sp_server_diagnostics_component_result` payloads from a `system_health` capture, store them relationally, and expose a coverage-first `export-health` digest.

**Architecture:** One new parser in `Ingestion` beside `BlockingReportParser`, eight new DuckDB tables, one new `HealthQueries`/`HealthDigest` pair in `Analysis` beside the blocking ones, one new CLI command. The `blocked-process-report` documents embedded in `QUERY_PROCESSING` reuse the existing blocking parser and tables, which costs a `source` column and the read/write changes Tasks 1–2 make first.

**Tech Stack:** .NET 10 / C# 14, DuckDB.NET.Data.Full 1.5.3, `System.Xml.Linq`, xUnit. No new package.

**Spec:** `docs/superpowers/specs/2026-09-03-system-health-diagnostics-design.md` (revision 2). Read it. This plan argues from it and does not repeat its reasoning.

**Revision 2 of this plan**, after an external review. Ten defects, six of them mine and invisible
to my own self-review because it checked spec coverage a whole section at a time — §9 was ticked as
"covered by Tasks 8–11" while four of its seven digest sections had no query at all, and two tables
were written by Task 6 and read by nobody. What changed:

- Task 2's test asserted the wrong chain depth: `heads` starts at 1 and the recursive term adds 1,
  so an isolated blocked/blocking pair is depth 2. The test failed both before *and* after the fix,
  which makes it worthless as a TDD gate.
- Task 6 incremented `blocking`, i.e. `events_blocking`, once per embedded report, while the event
  was already counted in `serverDiagnostics`. That breaks the mutual-exclusivity invariant and makes
  the published reconciliation query exceed `events_read`.
- Task 9 dropped two spec requirements: the restart interval is *dropped and counted*, not clamped
  to zero, and the ranking is `byCount` only.
- Task 10 (new) adds the four §9 sections that had no query: non-clean cycles, memory movement,
  I/O, and blocking seen in diagnostics cycles.
- Task 12's CLI code named two helpers that do not exist (`ArgValue`, `IsSafeOutputPath`); the real
  ones are `Arg(name, fallback)` and `BlockingDigestMarkdown.HasTraversal`. It also dropped
  `--format both`.
- Task 3 gains the assertion §7 requires on `cpuIntensiveRequests/@command`; Task 6 gains the
  environment-gated integration test §13 requires.

## Global Constraints

- **Microseconds everywhere in Core.** Durations stored as `*_us` (`long`). The source reports milliseconds for `averageWaitTime`, `maxWaitTime`, `cpuTimeMs`, `pendingRequest/@duration` and `oldestPendingTaskWaitingTime`. Convert at parse time. Never convert units outside the parser.
- **Nothing silently dropped.** Every event is counted in exactly one counter. A new event type means a new counter.
- **DuckDB.NET 1.5.3 parameter names carry no `$`.** SQL uses `$name`; the parameter is `name`. `DuckDbProject.Add(cmd, "$name", value)` does the `TrimStart('$')` — use it.
- **KISS.** No repository, no `IXxxService`, no DI container, no AutoMapper/MediatR. Static utility classes and primary-constructor services newed at the call site. Aggregation in DuckDB SQL, never C# reduction loops.
- **Test fixtures are anonymous.** `SampleApp`, `AppSchema`, `WidgetRecalc`, `WidgetScaling`, `@WidgetId`, `@GadgetCode`, `@TenantId`, `@Code`. Never a real schema, table, procedure, column, login, host or file path. Element and attribute names are SQL Server's and are safe.
- **NULL means `'event'`** for `blocking_reports.source`. Every filter is written `coalesce(source, 'event') = '…'`. A bare `WHERE source = 'event'` is a defect: it drops pre-migration rows.
- **0 warnings.** `dotnet build` must stay clean; CI runs `-warnaserror`.
- Run `dotnet format sqlferret.sln --include <files you touched>` before each commit.

---

### Task 1: `blocking_reports.source` and the write path

The migration breaks `InsertBlockingBatch`, which inserts positionally. Fix both together — the column is useless and the build is red in between.

**Files:**
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (migration block ~line 84; `InsertBlockingBatch` ~line 265)
- Modify: `src/SqlFerret.Core/Storage/PreparedBlocking.cs:7`
- Test: `tests/SqlFerret.Core.Tests/BlockingWritePathTests.cs` (create)

**Interfaces:**
- Produces: `PreparedBlockingReport(BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking, string? RawXml = null, string Source = "event")`
- Produces: `blocking_reports.source TEXT`, NULL on pre-migration rows.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingWritePathTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class BlockingWritePathTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static BlockingProcess Proc(int spid) =>
        new(spid, 0, "suspended", "KEY: 5:1 (x)", WaitResourceType.Key, null, null, 3_200_000L,
            "S", "read committed (2)", 1, "SampleApp", "WS1", "svc",
            "exec AppSchema.WidgetRecalc @WidgetId=1", "fp_" + spid);

    private static PreparedBlockingReport Report(string source)
    {
        var rep = new BlockingReport(new DateTime(2026, 2, 24), 42, 5, Proc(201), Proc(118));
        return new PreparedBlockingReport(rep,
            new PreparedBlockingProcess(rep.Blocked, null, "exec AppSchema.WidgetRecalc @WidgetId=1"),
            new PreparedBlockingProcess(rep.Blocking, null, "update AppSchema.Widget"),
            RawXml: null, Source: source);
    }

    [Fact]
    public void Insert_survives_the_source_migration_and_records_the_source()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");

            db.InsertBlockingBatch(runId, [Report("event"), Report("diagnostics")]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT source, count(*) FROM blocking_reports GROUP BY 1 ORDER BY 1";
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("diagnostics", r.GetString(0));
            Assert.Equal(1L, r.GetInt64(1));
            Assert.True(r.Read());
            Assert.Equal("event", r.GetString(0));
            Assert.Equal(1L, r.GetInt64(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter BlockingWritePathTests`
Expected: FAIL — `PreparedBlockingReport` has no `Source` parameter (compile error). That is the correct first failure.

- [ ] **Step 3: Add the column and the record parameter**

In `PreparedBlocking.cs`:

```csharp
public record PreparedBlockingReport(
    BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking,
    string? RawXml = null, string Source = "event");
```

In `DuckDbProject.CreateSchema`'s migration block, add one line beside the existing `ALTER TABLE`s:

```sql
ALTER TABLE blocking_reports ADD COLUMN IF NOT EXISTS source TEXT;
```

- [ ] **Step 4: Run test to verify it now fails differently**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter BlockingWritePathTests`
Expected: FAIL with `DuckDBException: Binder Error: table blocking_reports has 7 columns but 6 values were supplied`. This is the defect the panel found; seeing it is the point of the task.

- [ ] **Step 5: Convert the insert to a named column list**

In `InsertBlockingBatch`, replace the statement and add the parameter:

```csharp
c.CommandText = """
  INSERT INTO blocking_reports
        (report_id, run_id, captured_at, monitor_loop, database_id, raw_xml, source)
  VALUES ($id,$run,$ts,$loop,$db,$raw,$src)
  """;
Add(c, "$id", id); Add(c, "$run", runId); Add(c, "$ts", pr.Report.CapturedAt);
Add(c, "$loop", (object?)pr.Report.MonitorLoop); Add(c, "$db", (object?)pr.Report.DatabaseId);
Add(c, "$raw", (object?)pr.RawXml); Add(c, "$src", pr.Source);
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet build && dotnet test`
Expected: PASS. 568 + 1 pass, 10 skip. Every existing blocking test must still be green — they exercise the path this task rewrote.

- [ ] **Step 7: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Storage/DuckDbProject.cs src/SqlFerret.Core/Storage/PreparedBlocking.cs tests/SqlFerret.Core.Tests/BlockingWritePathTests.cs
git add -A
git commit -m "feat(storage): blocking_reports.source, and a named column list to survive it"
```

---

### Task 2: Isolate the two blocking sources in every reader

`source` is on `blocking_reports`; seven of ten `BlockingQueries` methods read only `blocking_processes`. `Chains()` additionally fabricates chains across reports sharing a `monitor_loop`, which a filter does not fix.

**Files:**
- Modify: `src/SqlFerret.Core/Analysis/BlockingQueries.cs` (all ten methods)
- Modify: `src/SqlFerret.Core/Analysis/EventExport.cs` (~line 80)
- Test: `tests/SqlFerret.Core.Tests/BlockingSourceIsolationTests.cs` (create)

**Interfaces:**
- Consumes: `blocking_reports.source` from Task 1.
- Produces: every blocking read filters `coalesce(r.source,'event') = 'event'`; `Chains()` keys its CTE on `(source, monitor_loop)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingSourceIsolationTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class BlockingSourceIsolationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static BlockingProcess Proc(int spid, long? waitUs) =>
        new(spid, 0, "suspended", "KEY: 5:1 (x)", WaitResourceType.Key, null, null, waitUs,
            "S", "read committed (2)", 1, "SampleApp", "WS1", "svc",
            "exec AppSchema.WidgetRecalc @WidgetId=1", "fp_" + spid);

    private static PreparedBlockingReport Report(int loop, int blocked, int blocking, string source)
    {
        var rep = new BlockingReport(new DateTime(2026, 2, 24), loop, 5,
            Proc(blocked, 3_000_000L), Proc(blocking, null));
        return new PreparedBlockingReport(rep,
            new PreparedBlockingProcess(rep.Blocked, null, "exec AppSchema.WidgetRecalc @WidgetId=1"),
            new PreparedBlockingProcess(rep.Blocking, null, "update AppSchema.Widget"),
            RawXml: null, Source: source);
    }

    /// <summary>Une incidence reelle, trois instantanes de la meme : les compteurs doivent dire 1.</summary>
    [Fact]
    public void Diagnostics_snapshots_do_not_inflate_the_event_counts()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),
                Report(43, 61, 72, "diagnostics"),
                Report(44, 61, 72, "diagnostics"),
                Report(45, 61, 72, "diagnostics"),
            ]);

            var q = new BlockingQueries(db.Connection);
            Assert.Equal(1L, q.Overview().ReportCount);
            Assert.Equal(1L, q.TopBlockers(10)[0].Count);
            Assert.Equal(1, q.LockModes().Sum(x => (int)x.Count));
            Assert.Equal(1, q.Locality().Sum(x => (int)x.Count));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// Deux incidences sans rapport partageant un monitor_loop ne forment pas une chaine.
    /// Un filtre sur `source` ne corrige pas ce cas : la fabrication a lieu dans la CTE.
    /// </summary>
    [Fact]
    public void Chains_does_not_fabricate_a_chain_across_unrelated_reports()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),
                Report(42, 72, 83, "event"),
            ]);

            var chains = new BlockingQueries(db.Connection).Chains();
            // Depth 2, pas 1 : `heads` initialise a 1 et le terme recursif ajoute 1, donc une paire
            // isolee bloque/bloquant vaut 2. Une chaine fabriquee a travers les deux rapports
            // vaudrait 3 — c'est cette valeur-la que le test doit exclure.
            Assert.NotEmpty(chains);
            Assert.All(chains, ch => Assert.Equal(2, ch.Depth));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter BlockingSourceIsolationTests`
Expected: FAIL. The first test reports 4 where 1 is expected. The second reports a depth-3 chain that never existed — the fabrication — where 2 is expected.

- [ ] **Step 3: Add the join and the filter to the seven process-only methods**

`Locality`, `TopObjects`, `LockModes`, `IsolationLevels`, `TopBlockers`, `TopBlocked`, `WaitTimes` each read `blocking_processes` alone. Add to each:

```sql
JOIN blocking_reports r ON r.report_id = b.report_id
WHERE coalesce(r.source, 'event') = 'event'
```

using whatever alias the method already gives `blocking_processes`, and merging into an existing `WHERE` with `AND` where one exists.

`SampleReports` already reads `blocking_reports`; add the predicate to its existing `WHERE`.
`Overview` has **no** `WHERE` — it is a single `SELECT` of scalar subqueries. The predicate goes
*inside each subquery*, not appended to the statement.

- [ ] **Step 4: Re-key the `Chains()` CTE**

Both the fabrication and the source mixing are fixed by keying on the pair. In `BlockingQueries.Chains()`:

```sql
WITH RECURSIVE edges AS (
  SELECT r.report_id AS grp, r.monitor_loop AS loop,
         b.spid AS blocked_spid, k.spid AS blocking_spid
  FROM blocking_reports r
  JOIN blocking_processes b ON b.report_id=r.report_id AND b.role='blocked'
  JOIN blocking_processes k ON k.report_id=r.report_id AND k.role='blocking'
  WHERE coalesce(r.source, 'event') = 'event'
),
heads AS (
  SELECT DISTINCT grp, loop, blocking_spid AS spid FROM edges e
  WHERE NOT EXISTS (SELECT 1 FROM edges x WHERE x.grp=e.grp AND x.blocked_spid=e.blocking_spid)
),
walk AS (
  SELECT grp, loop, spid AS head, spid AS cur, 1 AS depth FROM heads
  UNION ALL
  SELECT w.grp, w.loop, w.head, e.blocked_spid, w.depth+1
  FROM walk w JOIN edges e ON e.grp=w.grp AND e.blocking_spid=w.cur
  WHERE w.depth < 64
)
SELECT loop, max(depth) AS depth, head,
       (SELECT count(*) FROM edges e WHERE e.grp=walk.grp) AS edges
FROM walk GROUP BY grp, loop, head ORDER BY depth DESC
```

`grp` is `report_id`: a chain lives inside one report. This also removes the `NULL = NULL` failure a missing `monitorLoop` caused, since `report_id` is never null. `loop` is still selected because `ChainStat` reports it.

- [ ] **Step 5: Filter `EventExport`**

`export-events --kind blocking` hands back original XE documents, so it exports events only. In `EventExport.cs`, add to the `FROM blocking_reports r` query's `WHERE`:

```sql
AND coalesce(r.source, 'event') = 'event'
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet build && dotnet test`
Expected: PASS, including every pre-existing blocking and export test.

- [ ] **Step 7: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Analysis/BlockingQueries.cs src/SqlFerret.Core/Analysis/EventExport.cs tests/SqlFerret.Core.Tests/BlockingSourceIsolationTests.cs
git add -A
git commit -m "fix(blocking): isolate event-sourced reports, and stop Chains fabricating across reports"
```

---

### Task 3: `ServerDiagnosticsParser` — model and the three outcomes

**Files:**
- Create: `src/SqlFerret.Core/Model/ServerDiagnostics.cs`
- Create: `src/SqlFerret.Core/Ingestion/ServerDiagnosticsParser.cs`
- Test: `tests/SqlFerret.Core.Tests/ServerDiagnosticsParserTests.cs` (create)

**Interfaces:**
- Produces:
  ```csharp
  public enum DiagnosticsOutcome { Parsed, Unhandled, Failed }
  public record HealthMetric(string Name, double? ValueNum, long? ValueBig, string? ValueText);
  public record HealthWait(bool Preemptive, string Ranking, string WaitType, long Waits, long AvgWaitUs, long MaxWaitUs);
  public record HealthCpuRequest(int? SessionId, int? RequestId, string? Command, long? CpuTimeUs, double? CpuUtilization, string? TaskAddress);
  public record HealthPendingTask(string EntryPoint, long TaskCount);
  public record HealthPendingIo(long? DurationUs, string? FilePath, string? Handle, long? OffsetBytes);
  public record HealthMemoryEntry(string ReportName, string? Unit, string Description, double? ValueNum, string? ValueText);
  public record ServerDiagnosticsSample(
      DateTime CapturedAt, string Component, string? State, DiagnosticsOutcome Outcome,
      IReadOnlyList<HealthMetric> Metrics, IReadOnlyList<HealthWait> Waits,
      IReadOnlyList<HealthCpuRequest> CpuRequests, IReadOnlyList<HealthPendingTask> PendingTasks,
      IReadOnlyList<HealthPendingIo> PendingIo, IReadOnlyList<HealthMemoryEntry> MemoryEntries,
      IReadOnlyList<string> EmbeddedBlockingXml);
  public static class ServerDiagnosticsParser {
      public static ServerDiagnosticsSample TryParse(string? component, string? state, string? xml, DateTime capturedAt);
  }
  ```
  `TryParse` never returns null and never throws; the outcome says what happened.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/ServerDiagnosticsParserTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class ServerDiagnosticsParserTests
{
    private static readonly DateTime Ts = new(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);

    [Fact]
    public void Query_processing_reads_scalars_waits_cpu_and_pending_tasks()
    {
        const string xml = """
        <queryProcessing maxWorkers="9600" workersCreated="2574" workersIdle="1883"
                         pendingTasks="3" oldestPendingTaskWaitingTime="1500"
                         trackingNonYieldingScheduler="0x0">
          <topWaits>
            <nonPreemptive>
              <byCount>
                <wait waitType="CXPACKET" waits="1000" averageWaitTime="2" maxWaitTime="5026"/>
              </byCount>
            </nonPreemptive>
          </topWaits>
          <cpuIntensiveRequests>
            <request sessionId="53" requestId="0" command="SELECT" cpuTimeMs="1200"
                     cpuUtilization="12.5" taskAddress="0x1F"/>
          </cpuIntensiveRequests>
          <pendingTasks><entryPoint name="WidgetRecalc" count="3"/></pendingTasks>
          <blockingTasks/>
        </queryProcessing>
        """;
        var s = ServerDiagnosticsParser.TryParse("QUERY_PROCESSING", "CLEAN", xml, Ts);

        Assert.Equal(DiagnosticsOutcome.Parsed, s.Outcome);
        Assert.Equal(9600L, s.Metrics.Single(m => m.Name == "maxWorkers").ValueBig);
        // Duration scalar: converted, and renamed so the unit travels with the value.
        Assert.Equal(1_500_000L, s.Metrics.Single(m => m.Name == "oldestPendingTaskWaitingTimeUs").ValueBig);
        Assert.Null(s.Metrics.FirstOrDefault(m => m.Name == "oldestPendingTaskWaitingTime"));
        // Non-numeric scalar survives as text rather than being dropped.
        Assert.Equal("0x0", s.Metrics.Single(m => m.Name == "trackingNonYieldingScheduler").ValueText);

        var w = Assert.Single(s.Waits);
        Assert.Equal(("CXPACKET", true, "byCount"), (w.WaitType, !w.Preemptive, w.Ranking));
        Assert.Equal(2_000L, w.AvgWaitUs);      // ms -> us
        Assert.Equal(5_026_000L, w.MaxWaitUs);

        Assert.Equal(1_200_000L, Assert.Single(s.CpuRequests).CpuTimeUs);
        Assert.Equal("0x1F", s.CpuRequests[0].TaskAddress);
        // Spec §7 : `command` est une CLASSE de commande, pas du texte d'instruction, et c'est la
        // raison pour laquelle il est stocke tel quel sans passer par le sanitizer. Epingler la
        // forme ici : si une version future de SQL Server y met du SQL, le test tombe et la
        // decision est reexaminee au lieu d'etre absorbee en silence.
        Assert.Equal("SELECT", s.CpuRequests[0].Command);
        Assert.DoesNotContain(" FROM ", s.CpuRequests[0].Command!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("WidgetRecalc", 3L), (s.PendingTasks[0].EntryPoint, s.PendingTasks[0].TaskCount));
        Assert.Empty(s.EmbeddedBlockingXml);
    }

    [Fact]
    public void Io_subsystem_converts_the_pending_request_duration()
    {
        const string xml = """
        <ioSubsystem totalLongIos="7" intervalLongIos="2" ioLatchTimeouts="0">
          <longestPendingRequests>
            <pendingRequest duration="900" filePath="X:\AppDb\AppDb.mdf" handle="0x5" offset="4096"/>
          </longestPendingRequests>
        </ioSubsystem>
        """;
        var s = ServerDiagnosticsParser.TryParse("IO_SUBSYSTEM", "CLEAN", xml, Ts);
        Assert.Equal(DiagnosticsOutcome.Parsed, s.Outcome);
        Assert.Equal(900_000L, Assert.Single(s.PendingIo).DurationUs);
        Assert.Equal(4096L, s.PendingIo[0].OffsetBytes);
    }

    [Fact]
    public void Resource_reads_memory_report_entries()
    {
        const string xml = """
        <resource isAnyPoolOutOfMemory="0" outOfMemoryExceptions="0" processOutOfMemoryPeriod="0">
          <memoryReport name="Process/System Counts" unit="Value">
            <entry description="Available Physical Memory" value="8589934592"/>
          </memoryReport>
        </resource>
        """;
        var s = ServerDiagnosticsParser.TryParse("RESOURCE", "CLEAN", xml, Ts);
        var e = Assert.Single(s.MemoryEntries);
        Assert.Equal(("Process/System Counts", "Value", "Available Physical Memory"),
                     (e.ReportName, e.Unit, e.Description));
    }

    [Fact]
    public void Blocking_tasks_are_handed_back_as_raw_documents()
    {
        const string xml = """
        <queryProcessing maxWorkers="9600">
          <blockingTasks>
            <blocked-process-report monitorLoop="11881">
              <blocked-process><process spid="61"><inputbuf>exec AppSchema.WidgetRecalc</inputbuf></process></blocked-process>
              <blocking-process><process spid="72"><inputbuf>update AppSchema.Widget</inputbuf></process></blocking-process>
            </blocked-process-report>
          </blockingTasks>
        </queryProcessing>
        """;
        var s = ServerDiagnosticsParser.TryParse("QUERY_PROCESSING", "WARNING", xml, Ts);
        Assert.Single(s.EmbeddedBlockingXml);
        Assert.Contains("blocked-process-report", s.EmbeddedBlockingXml[0]);
    }

    // `events` is a documented fifth component, and an Always On instance emits one row per
    // availability group named after the group. Neither is a failure.
    [Theory]
    [InlineData("events")]
    [InlineData("AG_Widget_Prod")]
    public void An_unknown_component_is_unhandled_not_failed(string component)
    {
        var s = ServerDiagnosticsParser.TryParse(component, "UNKNOWN", "<whatever/>", Ts);
        Assert.Equal(DiagnosticsOutcome.Unhandled, s.Outcome);
        Assert.Equal(component, s.Component);
        Assert.Empty(s.Metrics);
    }

    [Theory]
    [InlineData("QUERY_PROCESSING", null)]
    [InlineData("QUERY_PROCESSING", "")]
    [InlineData("QUERY_PROCESSING", "<queryProcessing")]
    [InlineData("SYSTEM", "<queryProcessing maxWorkers=\"1\"/>")]   // root does not match the component
    public void A_known_component_with_unusable_xml_is_a_failure(string component, string? xml)
    {
        var s = ServerDiagnosticsParser.TryParse(component, "CLEAN", xml, Ts);
        Assert.Equal(DiagnosticsOutcome.Failed, s.Outcome);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ServerDiagnosticsParserTests`
Expected: FAIL — `ServerDiagnosticsParser` does not exist.

- [ ] **Step 3: Write the model**

```csharp
// src/SqlFerret.Core/Model/ServerDiagnostics.cs
namespace SqlFerret.Core.Model;

/// <summary>
/// Trois issues et non deux. Un composant inconnu n'est pas un echec : `events` est un cinquieme
/// composant documente, et une instance Always On emet une ligne par groupe de disponibilite,
/// nommee d'apres le groupe. Un compteur qui se declenche toujours n'apprend rien.
/// </summary>
public enum DiagnosticsOutcome { Parsed, Unhandled, Failed }

public record HealthMetric(string Name, double? ValueNum, long? ValueBig, string? ValueText);

public record HealthWait(
    bool Preemptive, string Ranking, string WaitType, long Waits, long AvgWaitUs, long MaxWaitUs);

public record HealthCpuRequest(
    int? SessionId, int? RequestId, string? Command, long? CpuTimeUs,
    double? CpuUtilization, string? TaskAddress);

public record HealthPendingTask(string EntryPoint, long TaskCount);

public record HealthPendingIo(long? DurationUs, string? FilePath, string? Handle, long? OffsetBytes);

public record HealthMemoryEntry(
    string ReportName, string? Unit, string Description, double? ValueNum, string? ValueText);

public record ServerDiagnosticsSample(
    DateTime CapturedAt, string Component, string? State, DiagnosticsOutcome Outcome,
    IReadOnlyList<HealthMetric> Metrics,
    IReadOnlyList<HealthWait> Waits,
    IReadOnlyList<HealthCpuRequest> CpuRequests,
    IReadOnlyList<HealthPendingTask> PendingTasks,
    IReadOnlyList<HealthPendingIo> PendingIo,
    IReadOnlyList<HealthMemoryEntry> MemoryEntries,
    IReadOnlyList<string> EmbeddedBlockingXml);
```

- [ ] **Step 4: Write the parser**

```csharp
// src/SqlFerret.Core/Ingestion/ServerDiagnosticsParser.cs
using System.Globalization;
using System.Xml.Linq;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

public static class ServerDiagnosticsParser
{
    private static readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QUERY_PROCESSING"] = "queryProcessing",
        ["RESOURCE"] = "resource",
        ["SYSTEM"] = "system",
        ["IO_SUBSYSTEM"] = "ioSubsystem",
    };

    /// <summary>
    /// Scalaires exprimes en millisecondes par la source. Convertis en microsecondes et renommes
    /// avec le suffixe Us : une ligne cle/valeur ne peut pas porter un nom de colonne, donc le
    /// mecanisme habituel de l'invariant (le suffixe `_us`) ne l'atteint pas. Liste explicite
    /// plutot qu'heuristique sur le nom : ajouter une entree doit etre une modification visible.
    /// </summary>
    private static readonly HashSet<string> MillisecondScalars =
        new(StringComparer.Ordinal) { "oldestPendingTaskWaitingTime", "processOutOfMemoryPeriod" };

    public static ServerDiagnosticsSample TryParse(
        string? component, string? state, string? xml, DateTime capturedAt)
    {
        var comp = component ?? "";
        if (!Roots.TryGetValue(comp, out var expectedRoot))
            return Empty(capturedAt, comp, state, DiagnosticsOutcome.Unhandled);

        if (string.IsNullOrWhiteSpace(xml))
            return Empty(capturedAt, comp, state, DiagnosticsOutcome.Failed);

        XElement root;
        try { root = XElement.Parse(xml); }
        catch { return Empty(capturedAt, comp, state, DiagnosticsOutcome.Failed); }  // XML malforme

        if (!string.Equals(root.Name.LocalName, expectedRoot, StringComparison.Ordinal))
            return Empty(capturedAt, comp, state, DiagnosticsOutcome.Failed);

        var metrics = new List<HealthMetric>();
        foreach (var a in root.Attributes())
        {
            var name = a.Name.LocalName;
            if (MillisecondScalars.Contains(name) && TryLong(a.Value, out var ms))
            {
                metrics.Add(new HealthMetric(name + "Us", null, ms * 1000L, null));
                continue;
            }
            if (TryLong(a.Value, out var big)) metrics.Add(new HealthMetric(name, null, big, null));
            else if (TryDouble(a.Value, out var d)) metrics.Add(new HealthMetric(name, d, null, null));
            else metrics.Add(new HealthMetric(name, null, null, a.Value));
        }

        var waits = new List<HealthWait>();
        foreach (var (kind, preemptive) in new[] { ("nonPreemptive", false), ("preemptive", true) })
            foreach (var ranking in new[] { "byCount", "byDuration" })
                foreach (var w in root.Descendants(kind).Descendants(ranking).Descendants("wait"))
                    waits.Add(new HealthWait(
                        preemptive, ranking, (string?)w.Attribute("waitType") ?? "",
                        Long(w, "waits") ?? 0, (Long(w, "averageWaitTime") ?? 0) * 1000L,
                        (Long(w, "maxWaitTime") ?? 0) * 1000L));

        var cpu = root.Descendants("cpuIntensiveRequests").Descendants("request")
            .Select(r => new HealthCpuRequest(
                Int(r, "sessionId"), Int(r, "requestId"), (string?)r.Attribute("command"),
                Long(r, "cpuTimeMs") is { } ms2 ? ms2 * 1000L : null,
                Dbl(r, "cpuUtilization"), (string?)r.Attribute("taskAddress")))
            .ToList();

        var pending = root.Descendants("pendingTasks").Descendants("entryPoint")
            .Select(e => new HealthPendingTask((string?)e.Attribute("name") ?? "", Long(e, "count") ?? 0))
            .ToList();

        var io = root.Descendants("longestPendingRequests").Descendants("pendingRequest")
            .Select(r => new HealthPendingIo(
                Long(r, "duration") is { } d2 ? d2 * 1000L : null,
                (string?)r.Attribute("filePath"), (string?)r.Attribute("handle"), Long(r, "offset")))
            .ToList();

        var mem = new List<HealthMemoryEntry>();
        foreach (var report in root.Descendants("memoryReport"))
        {
            var rn = (string?)report.Attribute("name") ?? "";
            var unit = (string?)report.Attribute("unit");
            foreach (var e in report.Elements("entry"))
            {
                var raw = (string?)e.Attribute("value");
                mem.Add(new HealthMemoryEntry(rn, unit, (string?)e.Attribute("description") ?? "",
                    raw is not null && TryDouble(raw, out var v) ? v : null,
                    raw is not null && TryDouble(raw, out _) ? null : raw));
            }
        }

        var embedded = root.Descendants("blockingTasks").Elements("blocked-process-report")
            .Select(x => x.ToString()).ToList();

        return new ServerDiagnosticsSample(capturedAt, comp, state, DiagnosticsOutcome.Parsed,
            metrics, waits, cpu, pending, io, mem, embedded);
    }

    private static ServerDiagnosticsSample Empty(
        DateTime ts, string comp, string? state, DiagnosticsOutcome outcome) =>
        new(ts, comp, state, outcome, [], [], [], [], [], [], []);

    private static bool TryLong(string s, out long v) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    private static bool TryDouble(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static long? Long(XElement e, string a) =>
        (string?)e.Attribute(a) is { } s && TryLong(s, out var v) ? v : null;
    private static int? Int(XElement e, string a) => (int?)Long(e, a);
    private static double? Dbl(XElement e, string a) =>
        (string?)e.Attribute(a) is { } s && TryDouble(s, out var v) ? v : null;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ServerDiagnosticsParserTests`
Expected: PASS, all 9.

- [ ] **Step 6: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Model/ServerDiagnostics.cs src/SqlFerret.Core/Ingestion/ServerDiagnosticsParser.cs tests/SqlFerret.Core.Tests/ServerDiagnosticsParserTests.cs
git add -A
git commit -m "feat(ingestion): ServerDiagnosticsParser for the four diagnostics components"
```

---

### Task 4: Cycle grouping and series detection

A cycle's four component events do **not** share a timestamp — measured: 1 344 events carry 1 344 distinct timestamps, and grouping to the second yields exactly 336 groups of four with a spread of 0.22–0.67 ms.

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/HealthCycleGrouper.cs`
- Test: `tests/SqlFerret.Core.Tests/HealthCycleGroupingTests.cs` (create)

**Interfaces:**
- Produces:
  ```csharp
  public record HealthCycle(DateTime CycleAt, string SeriesKey, IReadOnlyList<ServerDiagnosticsSample> Samples);
  public static class HealthCycleGrouper {
      public static IReadOnlyList<HealthCycle> Group(IReadOnlyList<ServerDiagnosticsSample> samples);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/HealthCycleGroupingTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class HealthCycleGroupingTests
{
    private static ServerDiagnosticsSample S(DateTime ts, string comp) =>
        new(ts, comp, "CLEAN", DiagnosticsOutcome.Parsed, [], [], [], [], [], [], []);

    private static IReadOnlyList<ServerDiagnosticsSample> Cycle(DateTime at) =>
    [
        S(at,                          "SYSTEM"),
        S(at.AddTicks(619),            "RESOURCE"),
        S(at.AddTicks(2352),           "QUERY_PROCESSING"),
        S(at.AddTicks(2393),           "IO_SUBSYSTEM"),
    ];

    [Fact]
    public void Four_components_under_a_millisecond_apart_are_one_cycle()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, 900, DateTimeKind.Utc);
        var cycles = HealthCycleGrouper.Group([.. Cycle(at)]);

        var c = Assert.Single(cycles);
        Assert.Equal(4, c.Samples.Count);
        Assert.Equal(at, c.CycleAt);            // le plus tot des quatre
    }

    [Fact]
    public void Two_interleaved_series_are_detected_as_two()
    {
        // Deux sessions echantillonnant le meme serveur, decalees, chacune toutes les 5 minutes.
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var b = new DateTime(2026, 9, 3, 0, 52, 06, DateTimeKind.Utc);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 0; i < 4; i++)
        {
            all.AddRange(Cycle(a.AddMinutes(5 * i)));
            all.AddRange(Cycle(b.AddMinutes(5 * i)));
        }

        var cycles = HealthCycleGrouper.Group(all);

        Assert.Equal(8, cycles.Count);
        Assert.Equal(2, cycles.Select(c => c.SeriesKey).Distinct().Count());
    }

    [Fact]
    public void A_single_series_yields_one_series_key()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 0; i < 5; i++) all.AddRange(Cycle(a.AddMinutes(5 * i)));

        Assert.Single(HealthCycleGrouper.Group(all).Select(c => c.SeriesKey).Distinct());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthCycleGroupingTests`
Expected: FAIL — `HealthCycleGrouper` does not exist.

- [ ] **Step 3: Write the grouper**

```csharp
// src/SqlFerret.Core/Ingestion/HealthCycleGrouper.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

public record HealthCycle(DateTime CycleAt, string SeriesKey, IReadOnlyList<ServerDiagnosticsSample> Samples);

/// <summary>
/// Les quatre evenements d'un cycle ne partagent pas leur horodatage : mesure, 1344 evenements
/// pour 1344 timestamps distincts, mais exactement 336 groupes a la seconde pres, l'ecart
/// intra-cycle allant de 0,22 a 0,67 ms. La troncature a la seconde est donc la cle de cycle.
/// <para><c>SeriesKey</c> repond a un cas observe dans une vraie capture : un dossier peut
/// contenir deux sessions echantillonnant le meme serveur en parallele, decalees. Les metriques a
/// portee d'intervalle ne s'additionnent pas a travers deux series. Le regroupement se fait sur la
/// seconde-dans-la-minute du cycle, qui est stable pour une session donnee et differe entre deux
/// sessions demarrees a des instants differents. C'est une heuristique, et l'hote doit annoncer
/// combien de series elle a trouvees plutot que de la laisser agir en silence.</para>
/// </summary>
public static class HealthCycleGrouper
{
    public static IReadOnlyList<HealthCycle> Group(IReadOnlyList<ServerDiagnosticsSample> samples) =>
        samples
            .GroupBy(s => new DateTime(s.CapturedAt.Ticks - s.CapturedAt.Ticks % TimeSpan.TicksPerSecond,
                                       s.CapturedAt.Kind))
            .OrderBy(g => g.Key)
            .Select(g => new HealthCycle(
                g.Min(s => s.CapturedAt),
                g.Key.Second.ToString("D2"),
                [.. g.OrderBy(s => s.CapturedAt)]))
            .ToList();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthCycleGroupingTests`
Expected: PASS, all 3.

- [ ] **Step 5: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Ingestion/HealthCycleGrouper.cs tests/SqlFerret.Core.Tests/HealthCycleGroupingTests.cs
git add -A
git commit -m "feat(ingestion): group diagnostics events into cycles and detect sampling series"
```

---

### Task 5: The eight tables and their writer

**Files:**
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (`CreateSchema`, plus a new `InsertHealthCycles`)
- Test: `tests/SqlFerret.Core.Tests/HealthStorageTests.cs` (create)

**Interfaces:**
- Consumes: `HealthCycle` (Task 4), `ServerDiagnosticsSample` (Task 3).
- Produces: `void DuckDbProject.InsertHealthCycles(long runId, IReadOnlyList<HealthCycle> cycles)`; `long NextHealthCycleId()`; `long NextHealthSampleId()`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/HealthStorageTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthStorageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static long Count(DuckDbProject db, string table)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt64(c.ExecuteScalar());
    }

    [Fact]
    public void A_cycle_and_its_children_are_persisted()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var qp = new ServerDiagnosticsSample(at, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed,
            [new HealthMetric("maxWorkers", null, 9600, null),
             new HealthMetric("trackingNonYieldingScheduler", null, null, "0x0")],
            [new HealthWait(false, "byCount", "CXPACKET", 1000, 2000, 5_026_000)],
            [new HealthCpuRequest(53, 0, "SELECT", 1_200_000, 12.5, "0x1F")],
            [new HealthPendingTask("WidgetRecalc", 3)], [], [], []);
        var io = new ServerDiagnosticsSample(at.AddTicks(2393), "IO_SUBSYSTEM", "CLEAN",
            DiagnosticsOutcome.Parsed, [], [], [], [],
            [new HealthPendingIo(900_000, @"X:\AppDb\AppDb.mdf", "0x5", 4096)], [], []);
        var unhandled = new ServerDiagnosticsSample(at.AddTicks(3000), "events", "UNKNOWN",
            DiagnosticsOutcome.Unhandled, [], [], [], [], [], [], []);

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [new HealthCycle(at, "34", [qp, io, unhandled])]);

            Assert.Equal(1L, Count(db, "health_cycles"));
            Assert.Equal(3L, Count(db, "health_samples"));
            Assert.Equal(2L, Count(db, "health_metrics"));
            Assert.Equal(1L, Count(db, "health_waits"));
            Assert.Equal(1L, Count(db, "health_cpu_requests"));
            Assert.Equal(1L, Count(db, "health_pending_tasks"));
            Assert.Equal(1L, Count(db, "health_pending_io"));

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT handled FROM health_samples WHERE component = 'events'";
            Assert.False((bool)c.ExecuteScalar()!);

            c.CommandText = "SELECT value_big FROM health_metrics WHERE name = 'maxWorkers'";
            Assert.Equal(9600L, Convert.ToInt64(c.ExecuteScalar()));

            c.CommandText = """
              SELECT count(*) FROM health_samples s
              JOIN health_cycles y ON y.cycle_id = s.cycle_id WHERE y.series_key = '34'
              """;
            Assert.Equal(3L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reopening_the_project_does_not_collide_on_ids()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        ServerDiagnosticsSample S(DateTime t) =>
            new(t, "SYSTEM", "CLEAN", DiagnosticsOutcome.Parsed, [], [], [], [], [], [], []);

        var path = TempDb();
        try
        {
            using (var db = DuckDbProject.Open(path))
                db.InsertHealthCycles(db.BeginRun("logs/", 1, 0, "masked"),
                    [new HealthCycle(at, "34", [S(at)])]);
            using (var db = DuckDbProject.Open(path))
                db.InsertHealthCycles(db.BeginRun("logs/", 1, 0, "masked"),
                    [new HealthCycle(at.AddMinutes(5), "34", [S(at.AddMinutes(5))])]);

            using (var db = DuckDbProject.Open(path))
            {
                Assert.Equal(2L, Count(db, "health_cycles"));
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(DISTINCT cycle_id) FROM health_cycles";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthStorageTests`
Expected: FAIL — `InsertHealthCycles` does not exist.

- [ ] **Step 3: Add the tables to `CreateSchema`**

Append to the `CreateSchema` command text, after the existing `CREATE TABLE`s. Copy verbatim from §6 of the spec; all eight were executed against DuckDB.NET 1.5.3 by four independent reviewers.

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

CREATE TABLE IF NOT EXISTS health_memory_entries (
  sample_id BIGINT, report_name TEXT, unit TEXT, description TEXT,
  value_num DOUBLE, value_text TEXT);
```

`offset_bytes`, not `offset`: `OFFSET` is reserved in DuckDB.

- [ ] **Step 4: Add the id helpers and the writer**

Beside `NextBlockingReportId`, following the same `COALESCE(MAX(...),0)` pattern:

```csharp
private long _nextHealthCycleId = -1;
private long _nextHealthSampleId = -1;

public long NextHealthCycleId()
{
    if (_nextHealthCycleId < 0)
        _nextHealthCycleId = Scalar("SELECT COALESCE(MAX(cycle_id),0) FROM health_cycles");
    return ++_nextHealthCycleId;
}

public long NextHealthSampleId()
{
    if (_nextHealthSampleId < 0)
        _nextHealthSampleId = Scalar("SELECT COALESCE(MAX(sample_id),0) FROM health_samples");
    return ++_nextHealthSampleId;
}

public void InsertHealthCycles(long runId, IReadOnlyList<HealthCycle> cycles)
{
    using var tx = Connection.BeginTransaction();
    foreach (var cycle in cycles)
    {
        long cycleId = NextHealthCycleId();
        Exec(tx, """
          INSERT INTO health_cycles (cycle_id, run_id, cycle_at, series_key)
          VALUES ($cid,$run,$at,$sk)
          """, ("$cid", cycleId), ("$run", runId), ("$at", cycle.CycleAt), ("$sk", cycle.SeriesKey));

        foreach (var s in cycle.Samples)
        {
            long sid = NextHealthSampleId();
            Exec(tx, """
              INSERT INTO health_samples
                    (sample_id, cycle_id, run_id, captured_at, component, state, handled)
              VALUES ($sid,$cid,$run,$ts,$comp,$state,$h)
              """,
              ("$sid", sid), ("$cid", cycleId), ("$run", runId), ("$ts", s.CapturedAt),
              ("$comp", s.Component), ("$state", (object?)s.State),
              ("$h", s.Outcome == DiagnosticsOutcome.Parsed));

            foreach (var m in s.Metrics)
                Exec(tx, "INSERT INTO health_metrics (sample_id,name,value_num,value_big,value_text) VALUES ($s,$n,$vn,$vb,$vt)",
                    ("$s", sid), ("$n", m.Name), ("$vn", (object?)m.ValueNum),
                    ("$vb", (object?)m.ValueBig), ("$vt", (object?)m.ValueText));

            foreach (var w in s.Waits)
                Exec(tx, "INSERT INTO health_waits (sample_id,preemptive,ranking,wait_type,waits,avg_wait_us,max_wait_us) VALUES ($s,$p,$r,$wt,$w,$a,$m)",
                    ("$s", sid), ("$p", w.Preemptive), ("$r", w.Ranking), ("$wt", w.WaitType),
                    ("$w", w.Waits), ("$a", w.AvgWaitUs), ("$m", w.MaxWaitUs));

            foreach (var r in s.CpuRequests)
                Exec(tx, "INSERT INTO health_cpu_requests (sample_id,session_id,request_id,command,cpu_time_us,cpu_utilization,task_address) VALUES ($s,$si,$ri,$c,$ct,$cu,$ta)",
                    ("$s", sid), ("$si", (object?)r.SessionId), ("$ri", (object?)r.RequestId),
                    ("$c", (object?)r.Command), ("$ct", (object?)r.CpuTimeUs),
                    ("$cu", (object?)r.CpuUtilization), ("$ta", (object?)r.TaskAddress));

            foreach (var t in s.PendingTasks)
                Exec(tx, "INSERT INTO health_pending_tasks (sample_id,entry_point,task_count) VALUES ($s,$e,$c)",
                    ("$s", sid), ("$e", t.EntryPoint), ("$c", t.TaskCount));

            foreach (var i in s.PendingIo)
                Exec(tx, "INSERT INTO health_pending_io (sample_id,duration_us,file_path,handle,offset_bytes) VALUES ($s,$d,$f,$h,$o)",
                    ("$s", sid), ("$d", (object?)i.DurationUs), ("$f", (object?)i.FilePath),
                    ("$h", (object?)i.Handle), ("$o", (object?)i.OffsetBytes));

            foreach (var e in s.MemoryEntries)
                Exec(tx, "INSERT INTO health_memory_entries (sample_id,report_name,unit,description,value_num,value_text) VALUES ($s,$rn,$u,$d,$vn,$vt)",
                    ("$s", sid), ("$rn", e.ReportName), ("$u", (object?)e.Unit),
                    ("$d", e.Description), ("$vn", (object?)e.ValueNum), ("$vt", (object?)e.ValueText));
        }
    }
    tx.Commit();
}

private void Exec(DuckDBTransaction tx, string sql, params (string Name, object? Value)[] ps)
{
    using var c = Connection.CreateCommand();
    c.Transaction = tx;
    c.CommandText = sql;
    foreach (var (n, v) in ps) Add(c, n, v);
    c.ExecuteNonQuery();
}
```

Add `using SqlFerret.Core.Ingestion;` to `DuckDbProject.cs` for `HealthCycle`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthStorageTests`
Expected: PASS, both.

- [ ] **Step 6: Run the full suite**

Run: `dotnet build && dotnet test`
Expected: PASS. The migration block now runs eight extra `CREATE TABLE IF NOT EXISTS` on every `Open`; no existing test may break.

- [ ] **Step 7: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Storage/DuckDbProject.cs tests/SqlFerret.Core.Tests/HealthStorageTests.cs
git add -A
git commit -m "feat(storage): eight health tables and their writer"
```

---

### Task 6: Wire ingestion, and carry the three counters everywhere

**Files:**
- Modify: `src/SqlFerret.Core/Ingestion/EventMapper.cs` (beside `IsPlanProfile`)
- Modify: `src/SqlFerret.Core/Ingestion/IngestionService.cs`
- Modify: `src/SqlFerret.Core/Ingestion/IngestionResult.cs`
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (`FinishRun`, migration block)
- Modify: `src/SqlFerret.Cli/Program.cs` (import summary line, ~line 130)
- Test: `tests/SqlFerret.Core.Tests/ServerDiagnosticsIngestionTests.cs` (create)

**Interfaces:**
- Consumes: `ServerDiagnosticsParser.TryParse`, `HealthCycleGrouper.Group`, `InsertHealthCycles`.
- Produces: `IngestionResult` gains `long ServerDiagnostics = 0, long ServerDiagnosticsUnhandled = 0, long ServerDiagnosticsParseFailures = 0`; `FinishRun` gains the same three optional parameters; `EventMapper.IsServerDiagnostics(string)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ServerDiagnosticsIngestionTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class ServerDiagnosticsIngestionTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Diag(string component, string? xml, DateTime ts) =>
        (new FakeEvent("sp_server_diagnostics_component_result", ts,
            new Dictionary<string, object?> { ["component"] = component, ["state"] = "CLEAN", ["data"] = xml },
            new Dictionary<string, object?>()),
         "system_health_0_1.xel", 0);

    private const string Qp = """<queryProcessing maxWorkers="9600" workersIdle="10"/>""";
    private const string Io = """<ioSubsystem totalLongIos="7"/>""";

    [Fact]
    public void The_three_counters_are_exclusive_and_account_for_every_event()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [
                    Diag("QUERY_PROCESSING", Qp, at),
                    Diag("IO_SUBSYSTEM", Io, at.AddTicks(2393)),
                    Diag("events", "<events/>", at.AddTicks(3000)),        // unhandled
                    Diag("QUERY_PROCESSING", null, at.AddMinutes(5)),      // failed
                ]);

            Assert.Equal(4, res.Read);
            Assert.Equal(2, res.ServerDiagnostics);
            Assert.Equal(1, res.ServerDiagnosticsUnhandled);
            Assert.Equal(1, res.ServerDiagnosticsParseFailures);
            Assert.Equal(0, res.Unmapped);   // plus rien ne tombe dans le fourre-tout

            // Les compteurs d'evenements se reconcilient EXACTEMENT avec events_read. Les
            // compteurs de sous-documents n'y entrent pas.
            Assert.Equal(res.Read,
                res.Mapped + res.Unmapped + res.Cleaned + res.Blocking + res.Deadlocks
                + res.PlanProfiles + res.ServerDiagnostics + res.ServerDiagnosticsUnhandled
                + res.ServerDiagnosticsParseFailures);
            Assert.Equal(0, res.Blocking);   // aucun blocked_process_report dans ce flux

            using var c = db.Connection.CreateCommand();
            c.CommandText = """
              SELECT events_server_diagnostics, events_server_diagnostics_unhandled,
                     server_diagnostics_parse_failures
              FROM ingestion_runs WHERE run_id = $r
              """;
            var p = c.CreateParameter(); p.ParameterName = "r"; p.Value = res.RunId; c.Parameters.Add(p);
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal((2L, 1L, 1L), (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Cycles_are_grouped_and_persisted()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Diag("QUERY_PROCESSING", Qp, at), Diag("IO_SUBSYSTEM", Io, at.AddTicks(2393))]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM health_cycles";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM health_samples";
            Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ServerDiagnosticsIngestionTests`
Expected: FAIL — `IngestionResult` has no `ServerDiagnostics`.

- [ ] **Step 3: Add the predicate and the columns**

`EventMapper.cs`, beside `IsPlanProfile`:

```csharp
/// <summary>
/// Égalité stricte, pas un Contains : même raison que pour IsPlanProfile — un événement voisin ne
/// doit pas être routé ici par accident.
/// </summary>
public static bool IsServerDiagnostics(string name) =>
    name.Equals("sp_server_diagnostics_component_result", StringComparison.OrdinalIgnoreCase);
```

`DuckDbProject` migration block:

```sql
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS events_server_diagnostics BIGINT;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS events_server_diagnostics_unhandled BIGINT;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS server_diagnostics_parse_failures BIGINT;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS server_diagnostics_embedded_blocking BIGINT;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS server_diagnostics_embedded_blocking_failures BIGINT;
```

`BeginRun`'s `INSERT` already lists columns; add the three names and `0,0,0` to its `VALUES`.

`FinishRun` — three more optional parameters and three more `SET` clauses:

```csharp
public void FinishRun(long runId, long read, long mapped, long unmapped, long cleaned,
    long tokenizeFailures, long blocking, long deadlocks, long blockingParseFailures,
    long planProfiles = 0, long planParseFailures = 0, long planWriteFailures = 0,
    long sqlTextSanitizeFailures = 0,
    long serverDiagnostics = 0, long serverDiagnosticsUnhandled = 0,
    long serverDiagnosticsParseFailures = 0,
    long embeddedBlocking = 0, long embeddedBlockingFailures = 0)
```

```sql
            sql_text_sanitize_failures=$stsf,
            events_server_diagnostics=$sd, events_server_diagnostics_unhandled=$sdu,
            server_diagnostics_parse_failures=$sdf
```

with `Add(c, "$sd", serverDiagnostics);` and the other two.

`IngestionResult.cs`:

```csharp
public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned,
    long TokenizeFailures, long Blocking, long Deadlocks, long BlockingParseFailures,
    long PlanProfiles = 0, long PlanParseFailures = 0, long PlanWriteFailures = 0,
    long SqlTextSanitizeFailures = 0,
    long ServerDiagnostics = 0, long ServerDiagnosticsUnhandled = 0,
    long ServerDiagnosticsParseFailures = 0,
    // Sous-documents, pas des evenements : hors de la somme de reconciliation.
    long EmbeddedBlocking = 0, long EmbeddedBlockingFailures = 0);
```

- [ ] **Step 4: Wire the branch in `IngestionService.Ingest`**

Declare beside the other counters:

```csharp
long serverDiagnostics = 0, serverDiagnosticsUnhandled = 0, serverDiagnosticsParseFailures = 0;
long embeddedBlocking = 0, embeddedBlockingFailures = 0;
var diagSamples = new List<ServerDiagnosticsSample>();
```

**The embedded reports get their own two counters, and they are not event counters.** An embedded
`<blocked-process-report>` is a sub-document of one XE event, not an event. Incrementing `blocking`
(which is `ingestion_runs.events_blocking`, the count of `blocked_process_report` *events*) would
count one `sp_server_diagnostics_component_result` event twice — once in `serverDiagnostics` and
again per embedded report — and the reconciliation query published in `docs/data-model.md` would
exceed `events_read`. Same for `blockingParseFailures`.

So: `server_diagnostics_embedded_blocking` and `server_diagnostics_embedded_blocking_failures`,
two more `BIGINT` columns on `ingestion_runs` added in the same migration block, carried on
`IngestionResult` and `FinishRun` alongside the other three, and **excluded from the event
reconciliation sum** — the documentation task must say so where it publishes that query, or the
next reader will add them and break it.

Add the branch immediately after the plan-profile branch and before `EventMapper.Map`:

```csharp
if (EventMapper.IsServerDiagnostics(ev.Name))
{
    var sample = ServerDiagnosticsParser.TryParse(
        Str(ev.Fields, "component"), Str(ev.Fields, "state"), Str(ev.Fields, "data"), ev.Timestamp);

    switch (sample.Outcome)
    {
        case DiagnosticsOutcome.Parsed: serverDiagnostics++; break;
        case DiagnosticsOutcome.Unhandled: serverDiagnosticsUnhandled++; break;
        default: serverDiagnosticsParseFailures++; break;
    }
    diagSamples.Add(sample);

    // Les rapports de blocage integres passent par PrepareProc comme les autres : meme porte de
    // confidentialite, meme empreinte d'inputbuf, aucun code nouveau sur ce chemin.
    foreach (var reportXml in sample.EmbeddedBlockingXml)
    {
        var rep = BlockingReportParser.Parse(reportXml, ev.Timestamp);
        if (rep is null) { embeddedBlockingFailures++; continue; }
        project.InsertBlockingBatch(runId, [Prepare(rep, null) with { Source = "diagnostics" }]);
        embeddedBlocking++;   // sous-document, pas un evenement : jamais `blocking++`
    }
    continue;
}
```

`Str` is the private helper `EventMapper` already uses; expose it as `internal static string? Str(IReadOnlyDictionary<string, object?> d, string k)` on `EventMapper` and call `EventMapper.Str(...)`.

Before `FinishRun`, flush the cycles:

```csharp
if (diagSamples.Count > 0)
    project.InsertHealthCycles(runId, HealthCycleGrouper.Group(diagSamples));
```

Pass the three counters to `FinishRun` and to the returned `IngestionResult`.

- [ ] **Step 5: Add them to the CLI summary line**

In `Program.cs`, append to the `run {runId}: …` line:

```csharp
$" serverDiagnostics={res.ServerDiagnostics} serverDiagnosticsUnhandled={res.ServerDiagnosticsUnhandled}" +
$" serverDiagnosticsParseFailures={res.ServerDiagnosticsParseFailures}"
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ServerDiagnosticsIngestionTests`
Expected: PASS, both.

- [ ] **Step 7: Run the full suite**

Run: `dotnet build && dotnet test`
Expected: PASS, 0 warnings.

- [ ] **Step 8: Add the environment-gated end-to-end test (spec §13)**

Append to `ServerDiagnosticsIngestionTests`. It skips when `sample/` is absent, like `XelReaderTests`.

```csharp
    private static string? FindSystemHealthCapture()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var sample = Path.Combine(dir, "sample");
            if (!Directory.Exists(sample)) continue;
            return Directory.GetFiles(sample, "system_health*.xel").OrderBy(f => f).FirstOrDefault();
        }
        return null;
    }

    [SkippableFact]
    public void A_real_system_health_capture_yields_cycles_and_no_unmapped_diagnostics()
    {
        var capture = FindSystemHealthCapture();
        Skip.If(capture is null, "no system_health capture under sample/");

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = new ImportRunner(db, new IngestionOptions(RedactionMode.Masked, []))
                .Run(capture!);

            Assert.True(res.ServerDiagnostics > 0, "the capture should contain diagnostics events");
            Assert.Equal(res.Read,
                res.Mapped + res.Unmapped + res.Cleaned + res.Blocking + res.Deadlocks
                + res.PlanProfiles + res.ServerDiagnostics + res.ServerDiagnosticsUnhandled
                + res.ServerDiagnosticsParseFailures);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM health_cycles";
            Assert.True(Convert.ToInt64(c.ExecuteScalar()) > 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

Adjust the `ImportRunner` call to whatever its actual entry point is — read
`src/SqlFerret.Core/Ingestion/ImportRunner.cs` first; if its shape does not fit, call
`XelReader().Read([capture])` and feed `IngestionService.Ingest` directly, as the other tests do.

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ServerDiagnosticsIngestionTests`
Expected: PASS, with this one SKIPPED (`sample/` is absent in a clean clone). The skip count rises
from 10 to 11; update `CLAUDE.md` in Task 13.

- [ ] **Step 9: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Ingestion/EventMapper.cs src/SqlFerret.Core/Ingestion/IngestionService.cs src/SqlFerret.Core/Ingestion/IngestionResult.cs src/SqlFerret.Core/Storage/DuckDbProject.cs src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/ServerDiagnosticsIngestionTests.cs
git add -A
git commit -m "feat(ingestion): route sp_server_diagnostics_component_result, with three exclusive counters"
```

---

### Task 7: Embedded blocking reports keep the privacy gate

Task 6 wired the path. This task proves it obeys both policies, which is the argument §5 rests on.

**Files:**
- Test: `tests/SqlFerret.Core.Tests/HealthBlockingReuseTests.cs` (create)

**Interfaces:** consumes everything from Task 6. No production code unless a test fails.

- [ ] **Step 1: Write the test**

```csharp
// tests/SqlFerret.Core.Tests/HealthBlockingReuseTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthBlockingReuseTests
{
    private const string Pii = "alice@example.com";

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Diag(string inputBuf) =>
        (new FakeEvent("sp_server_diagnostics_component_result", new DateTime(2026, 9, 3, 8, 26, 34),
            new Dictionary<string, object?>
            {
                ["component"] = "QUERY_PROCESSING",
                ["state"] = "WARNING",
                ["data"] = $"""
                <queryProcessing maxWorkers="9600">
                  <blockingTasks>
                    <blocked-process-report monitorLoop="11881">
                      <blocked-process><process spid="61" waitresource="KEY: 5:1 (x)" waittime="4100">
                        <inputbuf>{inputBuf}</inputbuf></process></blocked-process>
                      <blocking-process><process spid="72">
                        <inputbuf>update AppSchema.Widget set WidgetScaling=0</inputbuf></process></blocking-process>
                    </blocked-process-report>
                  </blockingTasks>
                </queryProcessing>
                """,
            },
            new Dictionary<string, object?>()),
         "system_health_0_1.xel", 0);

    private static string? Scalar(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = sql;
        var v = c.ExecuteScalar();
        return v is null or DBNull ? null : v.ToString();
    }

    [Fact]
    public void An_embedded_report_lands_as_a_diagnostics_source_report()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("logs/", [Diag("exec AppSchema.WidgetRecalc @Code='X1'")]);

            Assert.Equal("diagnostics", Scalar(db, "SELECT source FROM blocking_reports"));
            Assert.Equal("4100000", Scalar(db,
                "SELECT wait_time_us FROM blocking_processes WHERE role='blocked'"));
            Assert.NotNull(Scalar(db,
                "SELECT inputbuf_fingerprint FROM blocking_processes WHERE role='blocked'"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(RedactionMode.Off, SqlTextSanitization.Raw, true)]
    [InlineData(RedactionMode.Off, SqlTextSanitization.Literals, false)]
    [InlineData(RedactionMode.Masked, SqlTextSanitization.Raw, false)]
    [InlineData(RedactionMode.Masked, SqlTextSanitization.Literals, false)]
    public void The_input_buffer_obeys_both_policies(RedactionMode red, SqlTextSanitization txt, bool verbatim)
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(red, [], SqlText: txt))
                .Ingest("logs/", [Diag($"exec AppSchema.WidgetRecalc @Code='{Pii}'")]);

            var stored = Scalar(db, "SELECT inputbuf FROM blocking_processes WHERE role='blocked'") ?? "";
            if (verbatim) Assert.Contains(Pii, stored);
            else Assert.DoesNotContain(Pii, stored);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthBlockingReuseTests`
Expected: PASS, all 5. If the parameterised test fails on the `Off`/`Literals` row, the bug is in Task 6's wiring, not in `PrepareProc` — that gate was verified in 0.2.0. Fix the wiring.

- [ ] **Step 3: Commit**

```bash
dotnet format sqlferret.sln --include tests/SqlFerret.Core.Tests/HealthBlockingReuseTests.cs
git add -A
git commit -m "test(health): embedded blocking reports inherit the redaction and sanitization gate"
```

---

### Task 8: `HealthQueries` — coverage

**Files:**
- Create: `src/SqlFerret.Core/Analysis/HealthResults.cs`
- Create: `src/SqlFerret.Core/Analysis/HealthQueries.cs`
- Test: `tests/SqlFerret.Core.Tests/HealthQueriesCoverageTests.cs` (create)

**Interfaces:**
- Produces:
  ```csharp
  public record HealthSeries(string SeriesKey, long Cycles, DateTime First, DateTime Last, double MedianIntervalMin);
  public record HealthCoverage(long Cycles, DateTime? First, DateTime? Last, double SpanMinutes,
                               double MedianIntervalMin, double LargestGapMin, IReadOnlyList<HealthSeries> Series);
  public class HealthQueries(DuckDBConnection conn) { public HealthCoverage Coverage(); }
  ```

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/HealthQueriesCoverageTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthQueriesCoverageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample S(DateTime ts) =>
        new(ts, "SYSTEM", "CLEAN", DiagnosticsOutcome.Parsed, [], [], [], [], [], [], []);

    [Fact]
    public void Coverage_reports_cycles_span_median_and_the_largest_gap()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            // 0, 5, 10, puis un trou de 40 min, puis 50
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                    "34", [S(at)]),
                new HealthCycle(at.AddMinutes(5),      "34", [S(at.AddMinutes(5))]),
                new HealthCycle(at.AddMinutes(10),     "34", [S(at.AddMinutes(10))]),
                new HealthCycle(at.AddMinutes(50),     "34", [S(at.AddMinutes(50))]),
            ]);

            var cov = new HealthQueries(db.Connection).Coverage();

            Assert.Equal(4L, cov.Cycles);
            Assert.Equal(at, cov.First);
            Assert.Equal(50d, cov.SpanMinutes, 3);
            Assert.Equal(5d, cov.MedianIntervalMin, 3);
            Assert.Equal(40d, cov.LargestGapMin, 3);
            Assert.Single(cov.Series);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Two_series_are_reported_separately()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var b = new DateTime(2026, 9, 3, 0, 52, 06, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            List<HealthCycle> cycles = [];
            for (int i = 0; i < 3; i++)
            {
                cycles.Add(new HealthCycle(a.AddMinutes(5 * i), "34", [S(a.AddMinutes(5 * i))]));
                cycles.Add(new HealthCycle(b.AddMinutes(5 * i), "06", [S(b.AddMinutes(5 * i))]));
            }
            db.InsertHealthCycles(runId, cycles);

            var cov = new HealthQueries(db.Connection).Coverage();

            Assert.Equal(2, cov.Series.Count);
            Assert.All(cov.Series, s => Assert.Equal(3L, s.Cycles));
            Assert.All(cov.Series, s => Assert.Equal(5d, s.MedianIntervalMin, 3));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void An_empty_project_reports_zero_rather_than_throwing()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var cov = new HealthQueries(db.Connection).Coverage();
            Assert.Equal(0L, cov.Cycles);
            Assert.Null(cov.First);
            Assert.Empty(cov.Series);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthQueriesCoverageTests`
Expected: FAIL — `HealthQueries` does not exist.

- [ ] **Step 3: Write the results and the query**

```csharp
// src/SqlFerret.Core/Analysis/HealthResults.cs
namespace SqlFerret.Core.Analysis;

public record HealthSeries(
    string SeriesKey, long Cycles, DateTime First, DateTime Last, double MedianIntervalMin);

public record HealthCoverage(
    long Cycles, DateTime? First, DateTime? Last, double SpanMinutes,
    double MedianIntervalMin, double LargestGapMin, IReadOnlyList<HealthSeries> Series);
```

```csharp
// src/SqlFerret.Core/Analysis/HealthQueries.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>
/// L'agregation reste en SQL DuckDB. Le bloc de couverture vient en premier dans le digest parce
/// qu'une capture system_health est un anneau : sans lui, un classement se lit comme s'il portait
/// sur toute la periode alors qu'il ne porte que sur ce que l'anneau a garde.
/// </summary>
public class HealthQueries(DuckDBConnection conn)
{
    public HealthCoverage Coverage()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH g AS (
            SELECT series_key, cycle_at,
                   date_diff('millisecond', lag(cycle_at) OVER (ORDER BY cycle_at), cycle_at) / 60000.0 AS gap
            FROM health_cycles
          )
          SELECT count(*), min(cycle_at), max(cycle_at),
                 coalesce(date_diff('millisecond', min(cycle_at), max(cycle_at)) / 60000.0, 0),
                 coalesce(quantile_cont(gap, 0.5), 0), coalesce(max(gap), 0)
          FROM g
          """;
        long cycles = 0; DateTime? first = null, last = null;
        double span = 0, median = 0, largest = 0;
        using (var r = c.ExecuteReader())
            if (r.Read())
            {
                cycles = r.GetInt64(0);
                first = r.IsDBNull(1) ? null : r.GetDateTime(1);
                last = r.IsDBNull(2) ? null : r.GetDateTime(2);
                span = r.GetDouble(3); median = r.GetDouble(4); largest = r.GetDouble(5);
            }

        var series = new List<HealthSeries>();
        using (var s = conn.CreateCommand())
        {
            s.CommandText = """
              WITH g AS (
                SELECT series_key, cycle_at,
                       date_diff('millisecond',
                         lag(cycle_at) OVER (PARTITION BY series_key ORDER BY cycle_at),
                         cycle_at) / 60000.0 AS gap
                FROM health_cycles
              )
              SELECT series_key, count(*), min(cycle_at), max(cycle_at),
                     coalesce(quantile_cont(gap, 0.5), 0)
              FROM g GROUP BY series_key ORDER BY series_key
              """;
            using var r = s.ExecuteReader();
            while (r.Read())
                series.Add(new HealthSeries(r.GetString(0), r.GetInt64(1),
                    r.GetDateTime(2), r.GetDateTime(3), r.GetDouble(4)));
        }

        return new HealthCoverage(cycles, first, last, span, median, largest, series);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthQueriesCoverageTests`
Expected: PASS, all 3.

- [ ] **Step 5: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Analysis/HealthResults.cs src/SqlFerret.Core/Analysis/HealthQueries.cs tests/SqlFerret.Core.Tests/HealthQueriesCoverageTests.cs
git add -A
git commit -m "feat(analysis): HealthQueries.Coverage, series-aware"
```

---

### Task 9: Wait deltas, and the rest of the rankings

`topWaits` counters are cumulative since instance start — measured: 146 rising transitions and 0 falling across 147 cycles. Ranking by sum would rank uptime. `averageWaitTime` and `maxWaitTime` are a rounded running mean and a frozen running maximum, so no ranking orders on them.

**Files:**
- Modify: `src/SqlFerret.Core/Analysis/HealthResults.cs`
- Modify: `src/SqlFerret.Core/Analysis/HealthQueries.cs`
- Test: `tests/SqlFerret.Core.Tests/HealthWaitDeltaTests.cs` (create)

**Interfaces:**
- Produces:
  ```csharp
  public record WaitDelta(string WaitType, bool Preemptive, string Ranking, long WaitsDelta,
                          double SpanMinutes, double PerMinute, long LifetimeAvgWaitUs,
                          long LifetimeMaxWaitUs, long RestartIntervalsDropped);
  public record HealthScalar(string Name, double? Min, double? Median, double? P95, double? Max, long Samples);
  // on HealthQueries:
  public IReadOnlyList<WaitDelta> WaitDeltas(string seriesKey, int limit = 10);
  public IReadOnlyList<HealthScalar> ScalarGauges(IReadOnlyList<string> names);
  public IReadOnlyList<(string Name, long Delta)> ScalarDeltas(string seriesKey, IReadOnlyList<string> names);
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/HealthWaitDeltaTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthWaitDeltaTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample Qp(DateTime ts, params HealthWait[] waits) =>
        new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed, [], waits, [], [], [], [], []);

    private static HealthWait W(string type, long waits) =>
        new(false, "byCount", type, waits, 2_000, 5_026_000);

    [Fact]
    public void A_cumulative_counter_yields_a_delta_and_a_rate_not_a_sum()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "34", [Qp(at,                W("CXPACKET", 1_000_000))]),
                new HealthCycle(at.AddMinutes(5),  "34", [Qp(at.AddMinutes(5),  W("CXPACKET", 1_000_600))]),
                new HealthCycle(at.AddMinutes(10), "34", [Qp(at.AddMinutes(10), W("CXPACKET", 1_001_000))]),
            ]);

            var d = Assert.Single(new HealthQueries(db.Connection).WaitDeltas("34"));

            Assert.Equal("CXPACKET", d.WaitType);
            Assert.Equal(1_000L, d.WaitsDelta);          // 1_001_000 - 1_000_000, pas la somme
            Assert.Equal(10d, d.SpanMinutes, 3);
            Assert.Equal(100d, d.PerMinute, 3);
            // Les deux colonnes de temps sont des chiffres depuis le demarrage, transportes tels quels.
            Assert.Equal(5_026_000L, d.LifetimeMaxWaitUs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_wait_type_that_leaves_the_top_list_reports_the_span_it_actually_covers()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "34", [Qp(at,                W("LCK_M_IX", 500))]),
                new HealthCycle(at.AddMinutes(5),  "34", [Qp(at.AddMinutes(5),  W("LCK_M_IX", 700))]),
                new HealthCycle(at.AddMinutes(60), "34", [Qp(at.AddMinutes(60), W("CXPACKET", 10))]),
            ]);

            var lck = new HealthQueries(db.Connection).WaitDeltas("34")
                .Single(x => x.WaitType == "LCK_M_IX");

            Assert.Equal(5d, lck.SpanMinutes, 3);   // 5 min, pas les 60 de la fenetre
            Assert.Equal(200L, lck.WaitsDelta);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_falling_counter_means_a_restart_and_is_not_reported_as_negative_activity()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "34", [Qp(at,               W("CXPACKET", 5_000))]),
                new HealthCycle(at.AddMinutes(5), "34", [Qp(at.AddMinutes(5), W("CXPACKET", 10))]),
            ]);

            var d = Assert.Single(new HealthQueries(db.Connection).WaitDeltas("34"));
            Assert.Equal(0L, d.WaitsDelta);            // le pas negatif est ecarte, pas compense
            Assert.Equal(1L, d.RestartIntervalsDropped);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthWaitDeltaTests`
Expected: FAIL — `WaitDeltas` does not exist.

- [ ] **Step 3: Add the records**

Append to `HealthResults.cs`:

```csharp
/// <param name="SpanMinutes">
/// La duree que ce delta couvre reellement, qui n'est pas celle de la fenetre : seuls les N
/// premiers types d'attente apparaissent par cycle, donc un type qui entre ou sort de la liste a
/// un delta calcule sur une portion. La comparer a la fenetre serait la meme erreur d'un cran
/// plus bas, d'ou son transport jusqu'a l'affichage.
/// </param>
/// <param name="LifetimeAvgWaitUs">
/// Moyenne cumulee depuis le demarrage de l'instance, arrondie a la milliseconde par la source :
/// elle vaut 0 pour les attentes les plus frequentes. Transportee, jamais classee dessus.
/// </param>
/// <param name="RestartIntervalsDropped">
/// Nombre d'intervalles ou le compteur a recule, ce qui ne peut signifier qu'un redemarrage
/// d'instance. Ils sont ecartes du delta et comptes : les ramener a zero en silence ferait passer
/// un redemarrage pour une periode calme.
/// </param>
public record WaitDelta(
    string WaitType, bool Preemptive, string Ranking, long WaitsDelta,
    double SpanMinutes, double PerMinute, long LifetimeAvgWaitUs, long LifetimeMaxWaitUs,
    long RestartIntervalsDropped);

public record HealthScalar(string Name, double? Min, double? Median, double? P95, double? Max, long Samples);
```

- [ ] **Step 4: Add the queries**

Append to `HealthQueries`:

```csharp
    public IReadOnlyList<WaitDelta> WaitDeltas(string seriesKey, int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH w AS (
            SELECT hw.wait_type, hw.preemptive, hw.waits,
                   hw.avg_wait_us, hw.max_wait_us, y.cycle_at
            FROM health_waits hw
            JOIN health_samples s ON s.sample_id = hw.sample_id
            JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
            -- byCount uniquement : le classement porte sur un delta de COMPTE, et melanger les
            -- deux classements ferait apparaitre deux fois le meme type d'attente dans le top N.
            WHERE y.series_key = $sk AND hw.ranking = 'byCount'
          ),
          d AS (
            SELECT wait_type, preemptive, cycle_at, waits, avg_wait_us, max_wait_us,
                   waits - lag(waits) OVER (PARTITION BY wait_type, preemptive ORDER BY cycle_at) AS step
            FROM w
          ),
          b AS (
            SELECT wait_type, preemptive,
                   -- Somme des seuls pas croissants. Un pas negatif ne peut signifier qu'un
                   -- redemarrage d'instance : on l'ecarte et on le compte, au lieu de le
                   -- compenser, ce qui ferait passer un redemarrage pour une periode calme.
                   coalesce(sum(step) FILTER (WHERE step >= 0), 0) AS delta,
                   count(*) FILTER (WHERE step < 0)                AS restarts,
                   min(cycle_at) AS first_at, max(cycle_at) AS last_at,
                   arg_max(avg_wait_us, cycle_at) AS avg_us,
                   arg_max(max_wait_us, cycle_at) AS max_us
            FROM d GROUP BY wait_type, preemptive
          )
          SELECT wait_type, preemptive, 'byCount' AS ranking, delta,
                 date_diff('millisecond', first_at, last_at) / 60000.0 AS span_min,
                 avg_us, max_us, restarts
          FROM b
          ORDER BY delta DESC
          LIMIT $lim
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$lim", limit);

        var list = new List<WaitDelta>();
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            long delta = r.GetInt64(3);
            double span = r.GetDouble(4);
            list.Add(new WaitDelta(r.GetString(0), r.GetBoolean(1), r.GetString(2), delta, span,
                span > 0 ? delta / span : 0, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)));
        }
        return list;
    }

    /// <summary>Jauges instantanees : min / mediane / p95 / max, jamais une somme.</summary>
    public IReadOnlyList<HealthScalar> ScalarGauges(IReadOnlyList<string> names)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT name, min(v), quantile_cont(v, 0.5), quantile_cont(v, 0.95), max(v), count(*)
          FROM (SELECT name, coalesce(value_big::DOUBLE, value_num) AS v FROM health_metrics)
          WHERE v IS NOT NULL AND list_contains($names, name)
          GROUP BY name ORDER BY name
          """;
        Bind(c, "$names", names.ToList());

        var list = new List<HealthScalar>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new HealthScalar(r.GetString(0),
                r.IsDBNull(1) ? null : r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.GetInt64(5)));
        return list;
    }

    /// <summary>Compteurs cumulatifs : dernier moins premier, dans une seule serie.</summary>
    public IReadOnlyList<(string Name, long Delta)> ScalarDeltas(string seriesKey, IReadOnlyList<string> names)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT m.name,
                 greatest(arg_max(m.value_big, y.cycle_at) - arg_min(m.value_big, y.cycle_at), 0)
          FROM health_metrics m
          JOIN health_samples s ON s.sample_id = m.sample_id
          JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
          WHERE y.series_key = $sk AND m.value_big IS NOT NULL AND list_contains($names, m.name)
          GROUP BY m.name ORDER BY 2 DESC
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$names", names.ToList());

        var list = new List<(string, long)>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    private static void Bind(System.Data.Common.DbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthWaitDeltaTests`
Expected: PASS, all 3.

- [ ] **Step 6: Run the full suite**

Run: `dotnet build && dotnet test`

- [ ] **Step 7: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Analysis/HealthResults.cs src/SqlFerret.Core/Analysis/HealthQueries.cs tests/SqlFerret.Core.Tests/HealthWaitDeltaTests.cs
git add -A
git commit -m "feat(analysis): wait deltas over a stated span, and gauge/counter aggregation"
```

---

### Task 10: The four §9 sections that had no query

Revision 1 of this plan created `health_memory_entries` and `health_pending_io` in Task 5, wrote
them in Task 6, and never read them. Spec §9 items 1, 3, 6 and 7 had no implementation at all.

**Files:**
- Modify: `src/SqlFerret.Core/Analysis/HealthResults.cs`
- Modify: `src/SqlFerret.Core/Analysis/HealthQueries.cs`
- Modify: `tests/SqlFerret.Core.Tests/HealthQueriesCoverageTests.cs` (Step 5 changes `HealthCoverage`)
- Test: `tests/SqlFerret.Core.Tests/HealthQueriesSectionsTests.cs` (create)

**Interfaces:**
- Consumes: the tables from Task 5, the private `Bind` helper from Task 9.
- Produces, on `HealthQueries`:
  ```csharp
  public IReadOnlyList<StateCount> NonCleanStates();
  public IReadOnlyList<MemoryMovement> MemoryMovers(string seriesKey, int limit = 10);
  public IReadOnlyList<PendingIoRow> WorstPendingIo(int limit = 10);
  public IReadOnlyList<DiagnosticsBlockingRow> DiagnosticsBlocking(int limit = 10);
  ```
- Produces: `HealthCoverage` gains `double NonDiagnosticsShare` (Step 5).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/HealthQueriesSectionsTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthQueriesSectionsTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample Sample(
        DateTime ts, string comp, string state,
        IReadOnlyList<HealthMemoryEntry>? mem = null, IReadOnlyList<HealthPendingIo>? io = null) =>
        new(ts, comp, state, DiagnosticsOutcome.Parsed, [], [], [], [], io ?? [], mem ?? [], []);

    [Fact]
    public void Non_clean_states_are_counted_by_component()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at, "34", [
                    Sample(at, "QUERY_PROCESSING", "WARNING"),
                    Sample(at, "SYSTEM", "CLEAN"),
                    Sample(at, "RESOURCE", "CLEAN")]),
                new HealthCycle(at.AddMinutes(5), "34", [
                    Sample(at.AddMinutes(5), "QUERY_PROCESSING", "WARNING")]),
            ]);

            var rows = new HealthQueries(db.Connection).NonCleanStates();

            var r = Assert.Single(rows);
            Assert.Equal(("QUERY_PROCESSING", "WARNING", 2L), (r.Component, r.State, r.Cycles));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Memory_movers_report_the_change_across_the_window_not_the_last_value()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        HealthMemoryEntry E(string d, double v) => new("Process/System Counts", "Value", d, v, null);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at, "34", [Sample(at, "RESOURCE", "CLEAN",
                    mem: [E("Available Physical Memory", 8000), E("Stable", 100)])]),
                new HealthCycle(at.AddMinutes(5), "34", [Sample(at.AddMinutes(5), "RESOURCE", "CLEAN",
                    mem: [E("Available Physical Memory", 2000), E("Stable", 100)])]),
            ]);

            var rows = new HealthQueries(db.Connection).MemoryMovers("34");

            var top = rows[0];
            Assert.Equal("Available Physical Memory", top.Description);
            Assert.Equal(8000d, top.First, 3);
            Assert.Equal(2000d, top.Last, 3);
            Assert.Equal(-6000d, top.Change, 3);
            // Classement sur l'amplitude : une chute de memoire disponible est au moins aussi
            // interessante qu'une hausse, et ne garder que les hausses cacherait le cas cherche.
            Assert.True(Math.Abs(top.Change) >= Math.Abs(rows[^1].Change));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void The_worst_pending_io_is_reported_with_its_file()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at, "34", [Sample(at, "IO_SUBSYSTEM", "CLEAN",
                    io: [new HealthPendingIo(900_000, "X:/AppDb/AppDb.mdf", "0x5", 4096),
                         new HealthPendingIo(4_100_000, "X:/AppDb/AppDb_log.ldf", "0x6", 8192)])]),
            ]);

            var rows = new HealthQueries(db.Connection).WorstPendingIo();

            Assert.Equal(4_100_000L, rows[0].DurationUs);
            Assert.EndsWith("AppDb_log.ldf", rows[0].FilePath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Diagnostics_sourced_blocking_is_reported_and_event_sourced_is_not()
    {
        BlockingProcess Proc(int spid, long? waitUs) =>
            new(spid, 0, "suspended", "KEY: 5:1 (x)", WaitResourceType.Key, null, null, waitUs,
                "S", "read committed (2)", 1, "SampleApp", "WS1", "svc",
                "exec AppSchema.WidgetRecalc @WidgetId=1", "fp_" + spid);

        PreparedBlockingReport Rep(int loop, string source)
        {
            var rep = new BlockingReport(new DateTime(2026, 9, 3), loop, 5,
                Proc(61, 3_000_000L), Proc(72, null));
            return new PreparedBlockingReport(rep,
                new PreparedBlockingProcess(rep.Blocked, null, "exec AppSchema.WidgetRecalc @WidgetId=1"),
                new PreparedBlockingProcess(rep.Blocking, null, "update AppSchema.Widget"),
                RawXml: null, Source: source);
        }

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [Rep(42, "event"), Rep(43, "diagnostics")]);

            var rows = new HealthQueries(db.Connection).DiagnosticsBlocking();

            var r = Assert.Single(rows);
            Assert.Equal(61, r.BlockedSpid);
            Assert.Equal(72, r.BlockingSpid);
            Assert.Equal(3_000_000L, r.WaitTimeUs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthQueriesSectionsTests`
Expected: FAIL — `NonCleanStates` does not exist.

- [ ] **Step 3: Add the records**

Append to `HealthResults.cs`:

```csharp
public record StateCount(string Component, string State, long Cycles);

/// <param name="Change">
/// Dernier moins premier, signe. Le classement se fait sur la valeur absolue : une chute de
/// memoire disponible est au moins aussi interessante qu'une hausse.
/// </param>
public record MemoryMovement(
    string ReportName, string? Unit, string Description, double First, double Last, double Change);

public record PendingIoRow(DateTime CapturedAt, long? DurationUs, string? FilePath, string? Handle);

/// <summary>
/// Instantane pris pendant un cycle de diagnostics, jamais un rapport declenche par seuil : les
/// deux ne se comptent pas ensemble, et l'hote doit le dire quand il les affiche.
/// </summary>
public record DiagnosticsBlockingRow(
    DateTime CapturedAt, int? BlockedSpid, int? BlockingSpid, long? WaitTimeUs,
    string? WaitResourceType, string? BlockedInputBuf);
```

- [ ] **Step 4: Add the four queries**

Append to `HealthQueries`:

```csharp
    /// <summary>Spec §9 item 1. Un etat autre que CLEAN est le premier endroit ou regarder.</summary>
    public IReadOnlyList<StateCount> NonCleanStates()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT component, state, count(*) AS cycles
          FROM health_samples
          WHERE state IS NOT NULL AND upper(state) <> 'CLEAN' AND handled
          GROUP BY component, state
          ORDER BY cycles DESC, component
          """;
        var list = new List<StateCount>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new StateCount(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    /// <summary>
    /// Spec §9 item 3. Une seule serie : comparer un premier et un dernier a travers deux series
    /// entrelacees comparerait deux instants sans rapport.
    /// </summary>
    public IReadOnlyList<MemoryMovement> MemoryMovers(string seriesKey, int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH e AS (
            SELECT m.report_name, m.unit, m.description, m.value_num, y.cycle_at
            FROM health_memory_entries m
            JOIN health_samples s ON s.sample_id = m.sample_id
            JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
            WHERE y.series_key = $sk AND m.value_num IS NOT NULL
          )
          SELECT report_name, any_value(unit), description,
                 arg_min(value_num, cycle_at) AS first_v,
                 arg_max(value_num, cycle_at) AS last_v
          FROM e GROUP BY report_name, description
          ORDER BY abs(arg_max(value_num, cycle_at) - arg_min(value_num, cycle_at)) DESC
          LIMIT $lim
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$lim", limit);
        var list = new List<MemoryMovement>();
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            double first = r.GetDouble(3), last = r.GetDouble(4);
            list.Add(new MemoryMovement(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2), first, last, last - first));
        }
        return list;
    }

    /// <summary>
    /// Spec §9 item 6. file_path est stocke verbatim et n'est retire par aucune politique de
    /// redaction : l'hote qui l'affiche doit le dire.
    /// </summary>
    public IReadOnlyList<PendingIoRow> WorstPendingIo(int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT s.captured_at, i.duration_us, i.file_path, i.handle
          FROM health_pending_io i
          JOIN health_samples s ON s.sample_id = i.sample_id
          ORDER BY i.duration_us DESC NULLS LAST
          LIMIT $lim
          """;
        Bind(c, "$lim", limit);
        var list = new List<PendingIoRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new PendingIoRow(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>
    /// Spec §9 item 7. Les instantanes de diagnostics uniquement, jamais melanges aux rapports
    /// declenches par seuil.
    /// </summary>
    public IReadOnlyList<DiagnosticsBlockingRow> DiagnosticsBlocking(int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT r.captured_at, b.spid, k.spid, b.wait_time_us, b.wait_resource_type, b.inputbuf
          FROM blocking_reports r
          JOIN blocking_processes b ON b.report_id = r.report_id AND b.role = 'blocked'
          JOIN blocking_processes k ON k.report_id = r.report_id AND k.role = 'blocking'
          WHERE coalesce(r.source, 'event') = 'diagnostics'
          ORDER BY b.wait_time_us DESC NULLS LAST
          LIMIT $lim
          """;
        Bind(c, "$lim", limit);
        var list = new List<DiagnosticsBlockingRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new DiagnosticsBlockingRow(r.GetDateTime(0),
                r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }
```

- [ ] **Step 5: Add the non-diagnostics event share to `Coverage`**

Spec §9's coverage block asks for "the share of the capture's events that are not diagnostics" —
the number that told the reader 97.2 % of the measured capture was one flooded ring buffer.

Extend the record in `HealthResults.cs`:

```csharp
public record HealthCoverage(
    long Cycles, DateTime? First, DateTime? Last, double SpanMinutes,
    double MedianIntervalMin, double LargestGapMin, double NonDiagnosticsShare,
    IReadOnlyList<HealthSeries> Series);
```

In `Coverage()`, before the `return`:

```csharp
        double share = 0;
        using (var e = conn.CreateCommand())
        {
            e.CommandText = """
              SELECT coalesce(sum(events_read), 0)::DOUBLE,
                     (coalesce(sum(events_server_diagnostics), 0)
                    + coalesce(sum(events_server_diagnostics_unhandled), 0)
                    + coalesce(sum(server_diagnostics_parse_failures), 0))::DOUBLE
              FROM ingestion_runs
              """;
            using var r2 = e.ExecuteReader();
            if (r2.Read())
            {
                double read = r2.GetDouble(0), diag = r2.GetDouble(1);
                share = read > 0 ? (read - diag) / read : 0;
            }
        }
```

and pass `share` as the seventh argument of `HealthCoverage`.

`HealthQueriesCoverageTests` constructs nothing positionally, but add one assertion to
`Coverage_reports_cycles_span_median_and_the_largest_gap`:

```csharp
            Assert.Equal(0d, cov.NonDiagnosticsShare, 3);   // aucun run ingere dans ce test
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter "HealthQueriesSectionsTests|HealthQueriesCoverageTests"`
Expected: PASS.

- [ ] **Step 7: Run the full suite and commit**

```bash
dotnet build && dotnet test
dotnet format sqlferret.sln --include src/SqlFerret.Core/Analysis/HealthResults.cs src/SqlFerret.Core/Analysis/HealthQueries.cs tests/SqlFerret.Core.Tests/HealthQueriesSectionsTests.cs tests/SqlFerret.Core.Tests/HealthQueriesCoverageTests.cs
git add -A
git commit -m "feat(analysis): non-clean states, memory movement, worst pending I/O, diagnostics blocking"
```

---

### Task 11: `HealthDigest` and its versioned envelope

**Files:**
- Create: `src/SqlFerret.Core/Analysis/HealthDigest.cs`
- Test: `tests/SqlFerret.Core.Tests/HealthDigestTests.cs` (create)

**Interfaces:**
- Produces:
  ```csharp
  public record HealthDigestResult(HealthCoverage Coverage, IReadOnlyList<string> Notes,
      IReadOnlyList<StateCount> NonCleanStates, IReadOnlyList<WaitDelta> TopWaits,
      IReadOnlyList<MemoryMovement> MemoryMovers, IReadOnlyList<HealthScalar> WorkerPressure,
      IReadOnlyList<(string Name, long Delta)> StabilitySignals,
      IReadOnlyList<PendingIoRow> WorstPendingIo,
      IReadOnlyList<DiagnosticsBlockingRow> DiagnosticsBlocking);
  public record HealthDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, HealthDigestResult Digest);
  public class HealthDigest(DuckDBConnection conn) {
      public const int SchemaVersion = 1;
      public HealthDigestEnvelope Build(int limit = 10);
      public static string ToMarkdown(HealthDigestEnvelope e);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/HealthDigestTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthDigestTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample Qp(DateTime ts, long waits) =>
        new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed,
            [new HealthMetric("pendingTasks", null, 3, null),
             new HealthMetric("spinlockBackoffs", null, waits, null)],
            [new HealthWait(false, "byCount", "CXPACKET", waits, 2_000, 5_026_000)],
            [], [], [], [], []);

    [Fact]
    public void A_project_with_no_health_data_says_so_rather_than_reporting_an_empty_ranking()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var e = new HealthDigest(db.Connection).Build();

            Assert.Equal(HealthDigest.SchemaVersion, e.SchemaVersion);
            Assert.Equal(0L, e.Digest.Coverage.Cycles);
            Assert.Contains(e.Digest.Notes, n => n.Contains("no diagnostics", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(e.Digest.TopWaits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void More_than_one_series_is_announced_in_the_notes()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var b = new DateTime(2026, 9, 3, 0, 52, 06, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            List<HealthCycle> cycles = [];
            for (int i = 0; i < 3; i++)
            {
                cycles.Add(new HealthCycle(a.AddMinutes(5 * i), "34", [Qp(a.AddMinutes(5 * i), 100 + i)]));
                cycles.Add(new HealthCycle(b.AddMinutes(5 * i), "06", [Qp(b.AddMinutes(5 * i), 200 + i)]));
            }
            db.InsertHealthCycles(runId, cycles);

            var e = new HealthDigest(db.Connection).Build();

            Assert.Equal(2, e.Digest.Coverage.Series.Count);
            Assert.Contains(e.Digest.Notes, n => n.Contains("two sampling series", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("2 sampling series", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Markdown_leads_with_coverage()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "34", [Qp(at, 1_000)]),
                new HealthCycle(at.AddMinutes(5), "34", [Qp(at.AddMinutes(5), 1_500)]),
            ]);

            var md = HealthDigest.ToMarkdown(new HealthDigest(db.Connection).Build());

            var coverageAt = md.IndexOf("## Coverage", StringComparison.Ordinal);
            var waitsAt = md.IndexOf("## Waits", StringComparison.Ordinal);
            Assert.True(coverageAt >= 0 && waitsAt > coverageAt, "Coverage must come first");
            Assert.Contains("since instance start", md, StringComparison.OrdinalIgnoreCase);
            // Les sept sections de la spec §9 sont toutes rendues, pas seulement celles qui ont
            // des donnees : une section absente se lit comme une section sans probleme.
            foreach (var h in new[] { "## Memory", "## Worst pending I/O",
                                      "## Blocking seen in diagnostics cycles" })
                Assert.Contains(h, md, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthDigestTests`
Expected: FAIL — `HealthDigest` does not exist.

- [ ] **Step 3: Write the digest**

```csharp
// src/SqlFerret.Core/Analysis/HealthDigest.cs
using System.Globalization;
using System.Text;
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>Les sept sections de la spec §9, dans son ordre, la couverture en tete.</summary>
public record HealthDigestResult(
    HealthCoverage Coverage, IReadOnlyList<string> Notes,
    IReadOnlyList<StateCount> NonCleanStates, IReadOnlyList<WaitDelta> TopWaits,
    IReadOnlyList<MemoryMovement> MemoryMovers, IReadOnlyList<HealthScalar> WorkerPressure,
    IReadOnlyList<(string Name, long Delta)> StabilitySignals,
    IReadOnlyList<PendingIoRow> WorstPendingIo,
    IReadOnlyList<DiagnosticsBlockingRow> DiagnosticsBlocking);

public record HealthDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, HealthDigestResult Digest);

/// <summary>
/// Le bloc de couverture vient en premier, avant toute conclusion. Les compteurs de cette source
/// sont cumulatifs depuis le demarrage de l'instance : chaque chiffre presente comme une activite
/// est un delta sur une portee, et la portee est imprimee a cote.
/// </summary>
public class HealthDigest(DuckDBConnection conn)
{
    public const int SchemaVersion = 1;

    private static readonly string[] WorkerGauges =
        ["pendingTasks", "workersIdle", "maxWorkers", "oldestPendingTaskWaitingTimeUs"];

    private static readonly string[] StabilityCounters =
        ["spinlockBackoffs", "latchWarnings", "nonYieldingTasksReported", "pageFaults",
         "totalDumpRequests", "writeAccessViolationCount", "outOfMemoryExceptions"];

    public HealthDigestEnvelope Build(int limit = 10)
    {
        var q = new HealthQueries(conn);
        var coverage = q.Coverage();
        var notes = new List<string>();

        if (coverage.Cycles == 0)
        {
            notes.Add("This project holds no diagnostics samples. Import a system_health capture, "
                    + "or check that the capture actually contains sp_server_diagnostics_component_result.");
            return new HealthDigestEnvelope(SchemaVersion, DateTime.UtcNow,
                new HealthDigestResult(coverage, notes, [], [], [], [], [], [], []));
        }

        if (coverage.Series.Count > 1)
            notes.Add($"{coverage.Series.Count} sampling series detected. Interval-scoped metrics "
                    + "(intervalLongIos, tasksCompletedWithinInterval) are not combined across them: "
                    + "each is scoped to its own session's interval. Rankings below use the longest series.");

        // Une seule serie : combiner des deltas entre series entrelacees n'a pas de sens.
        var main = coverage.Series.OrderByDescending(s => s.Cycles).First().SeriesKey;

        var waits = q.WaitDeltas(main, limit);
        if (waits.Count > 0)
            notes.Add("Wait counters are cumulative since instance start. Figures are deltas over the "
                    + "span printed beside each row, not totals for the capture. Wait *times* are "
                    + "instance-lifetime values and nothing is ranked on them.");

        var workers = q.ScalarGauges(WorkerGauges);
        var stability = q.ScalarDeltas(main, StabilityCounters).Where(x => x.Delta > 0).ToList();
        if (stability.Count == 0)
            notes.Add("No stability signal moved during the window. On a healthy server this is the "
                    + "expected result, not missing data.");

        var nonClean = q.NonCleanStates();
        var memory = q.MemoryMovers(main, limit);
        var pendingIo = q.WorstPendingIo(limit);
        var diagBlocking = q.DiagnosticsBlocking(limit);

        if (pendingIo.Count > 0)
            notes.Add("Pending-I/O rows carry the server's own file paths. They are stored verbatim "
                    + "under every redaction mode and no flag removes them: treat this digest as "
                    + "disclosing instance and database file layout.");
        if (diagBlocking.Count > 0)
            notes.Add("Blocking below is what the server happened to be doing at a sampling instant, "
                    + "not a threshold-triggered report. It is not comparable with export-blocking.");

        return new HealthDigestEnvelope(SchemaVersion, DateTime.UtcNow,
            new HealthDigestResult(coverage, notes, nonClean, waits, memory, workers, stability,
                pendingIo, diagBlocking));
    }

    public static string ToMarkdown(HealthDigestEnvelope e)
    {
        var d = e.Digest;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Health digest").AppendLine();

        sb.AppendLine("## Coverage").AppendLine();
        if (d.Coverage.Cycles == 0) sb.AppendLine("No diagnostics samples.").AppendLine();
        else
        {
            sb.AppendLine($"- Cycles: {d.Coverage.Cycles}");
            sb.AppendLine($"- Window: {d.Coverage.First:u} → {d.Coverage.Last:u} "
                        + $"({d.Coverage.SpanMinutes.ToString("F1", inv)} min)");
            sb.AppendLine($"- Median interval: {d.Coverage.MedianIntervalMin.ToString("F2", inv)} min; "
                        + $"largest gap: {d.Coverage.LargestGapMin.ToString("F2", inv)} min");
            sb.AppendLine($"- Sampling series: {d.Coverage.Series.Count}");
            foreach (var s in d.Coverage.Series)
                sb.AppendLine($"  - `{s.SeriesKey}`: {s.Cycles} cycles, median "
                            + $"{s.MedianIntervalMin.ToString("F2", inv)} min");
            sb.AppendLine();
        }

        foreach (var n in d.Notes) sb.AppendLine($"> {n}").AppendLine();

        sb.AppendLine("## Waits").AppendLine();
        if (d.TopWaits.Count == 0) sb.AppendLine("None recorded.").AppendLine();
        else
        {
            sb.AppendLine("| Wait type | Δ waits | over (min) | per min | avg since instance start (ms) |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var w in d.TopWaits)
                sb.AppendLine($"| `{w.WaitType}` | {w.WaitsDelta} | {w.SpanMinutes.ToString("F1", inv)} "
                            + $"| {w.PerMinute.ToString("F1", inv)} | {(w.LifetimeAvgWaitUs / 1000.0).ToString("F1", inv)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Worker pressure").AppendLine();
        foreach (var s in d.WorkerPressure)
            sb.AppendLine($"- `{s.Name}`: min {s.Min}, median {s.Median}, p95 {s.P95}, max {s.Max} "
                        + $"({s.Samples} samples)");
        sb.AppendLine();

        sb.AppendLine("## Stability signals").AppendLine();
        if (d.StabilitySignals.Count == 0) sb.AppendLine("Nothing moved.");
        else foreach (var (name, delta) in d.StabilitySignals) sb.AppendLine($"- `{name}`: +{delta}");
        sb.AppendLine();

        sb.AppendLine("## Memory").AppendLine();
        if (d.MemoryMovers.Count == 0) sb.AppendLine("No memory report entries.");
        else foreach (var m in d.MemoryMovers)
            sb.AppendLine($"- `{m.Description}` ({m.ReportName}): {m.First.ToString("F0", inv)} → "
                        + $"{m.Last.ToString("F0", inv)} ({m.Change:+#;-#;0} {m.Unit})");
        sb.AppendLine();

        sb.AppendLine("## Worst pending I/O").AppendLine();
        if (d.WorstPendingIo.Count == 0) sb.AppendLine("None recorded.");
        else foreach (var i in d.WorstPendingIo)
            sb.AppendLine($"- {(i.DurationUs / 1000.0)?.ToString("F0", inv)} ms — `{i.FilePath}` "
                        + $"at {i.CapturedAt:u}");
        sb.AppendLine();

        sb.AppendLine("## Blocking seen in diagnostics cycles").AppendLine();
        if (d.DiagnosticsBlocking.Count == 0) sb.AppendLine("None.");
        else foreach (var b in d.DiagnosticsBlocking)
            sb.AppendLine($"- spid {b.BlockedSpid} blocked by {b.BlockingSpid} for "
                        + $"{(b.WaitTimeUs / 1000.0)?.ToString("F0", inv)} ms "
                        + $"({b.WaitResourceType}) at {b.CapturedAt:u}");

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter HealthDigestTests`
Expected: PASS, all 3.

- [ ] **Step 5: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Core/Analysis/HealthDigest.cs tests/SqlFerret.Core.Tests/HealthDigestTests.cs
git add -A
git commit -m "feat(analysis): HealthDigest, coverage first, deltas over a stated span"
```

---

### Task 12: `export-health`

**Files:**
- Modify: `src/SqlFerret.Cli/Program.cs` (new `case`, usage line)
- Test: `tests/SqlFerret.Core.Tests/CliExportHealthTests.cs` (create)

**Interfaces:** consumes `HealthDigest`. `--limit` (not `--top`); `--out` rejects traversal via the
same helper `export-blocking` uses.

The two helpers this task needs already exist and are **not** named what an earlier revision of this
plan guessed. Read them before writing the case:
- `string Arg(string name, string? fallback = null)` — a local function in `Program.cs:19`. It reads
  `args` from the enclosing scope; it does **not** take an `args` parameter.
- `SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(string path)` — used at `Program.cs:164` and
  `:258`. There is no `IsSafeOutputPath`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/CliExportHealthTests.cs
using System.Diagnostics;
using Xunit;

public class CliExportHealthTests
{
    private static string CliPath()
    {
        var dir = AppContext.BaseDirectory;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "SqlFerret.Cli.exe" : "SqlFerret.Cli");
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(CliPath()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    [Fact]
    public void Export_health_on_a_fresh_project_says_there_is_no_data_and_exits_zero()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var (code, output, _) = Run("export-health", "--project", dir, "--format", "md");
            Assert.Equal(0, code);
            Assert.Contains("Coverage", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no diagnostics", output, StringComparison.OrdinalIgnoreCase);

            // `both` est dans la spec §9 et doit produire les deux, pas retomber sur Markdown.
            var (code2, out2, _) = Run("export-health", "--project", dir, "--format", "both");
            Assert.Equal(0, code2);
            Assert.Contains("Coverage", out2, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("schemaVersion", out2, StringComparison.OrdinalIgnoreCase);

            var (code3, _, err3) = Run("export-health", "--project", dir, "--format", "xml");
            Assert.NotEqual(0, code3);
            Assert.Contains("--format", err3);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Out_rejects_path_traversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var (code, _, err) = Run("export-health", "--project", dir, "--out", "../escape.md");
            Assert.NotEqual(0, code);
            Assert.Contains("..", err);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter CliExportHealthTests`
Expected: FAIL — `unknown command: export-health`, exit code 1.

- [ ] **Step 3: Add the command**

In `Program.cs`, beside `export-blocking`:

```csharp
    case "export-health":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var format = Arg("--format", "md")!;
            if (format is not ("json" or "md" or "both"))
            {
                Console.Error.WriteLine("error: --format must be json, md or both");
                return 1;
            }
            var outPath = Arg("--out", "")!;
            if (outPath.Length > 0 && SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outPath))
            {
                Console.Error.WriteLine("error: --out must not contain '..'");
                return 1;
            }
            if (!int.TryParse(Arg("--limit"), out var limit) || limit <= 0) limit = 10;

            using var db = project.OpenDb();
            var envelope = new SqlFerret.Core.Analysis.HealthDigest(db.Connection).Build(limit);

            var json = System.Text.Json.JsonSerializer.Serialize(envelope,
                           new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var md = SqlFerret.Core.Analysis.HealthDigest.ToMarkdown(envelope);

            // `both` ecrit deux fichiers quand --out est donne, et concatene sur stdout sinon —
            // meme comportement qu'export-blocking, dont cette commande copie la forme.
            if (outPath.Length == 0)
            {
                Console.WriteLine(format switch { "json" => json, "md" => md, _ => md + "
" + json });
            }
            else if (format == "both")
            {
                var stem = Path.Combine(Path.GetDirectoryName(outPath) ?? "",
                                        Path.GetFileNameWithoutExtension(outPath));
                File.WriteAllText(stem + ".md", md);
                File.WriteAllText(stem + ".json", json);
                Console.WriteLine($"written: {stem}.md, {stem}.json");
            }
            else
            {
                File.WriteAllText(outPath, format == "json" ? json : md);
                Console.WriteLine($"written: {outPath}");
            }
            return 0;
        }
```

`Arg` is a local function in the top-level program, so the new `case` sees it directly. Do not add a
second traversal helper: `BlockingDigestMarkdown.HasTraversal` is the one the other two commands
already call, and the rule must live in one place.

- [ ] **Step 4: Extend the usage line**

Add to the `Console.Error.WriteLine("usage: …")` string, after the `obfuscate-plan` clause:

```
 | export-health --project <dir> [--format json|md|both] [--out <file>] [--limit <n>]
```

- [ ] **Step 5: Run tests and the full suite**

Run: `dotnet build && dotnet test`
Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
dotnet format sqlferret.sln --include src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/CliExportHealthTests.cs
git add -A
git commit -m "feat(cli): export-health, coverage-first health digest"
```

---

### Task 13: Documentation

**Files:**
- Modify: `docs/data-model.md`, `docs/cli-reference.md`, `docs/privacy.md`, `docs/blocking.md`,
  `docs/capture-session.md`, `docs/README.md`, `README.md`,
  `.agents/skills/analyzing-xel-workloads/SKILL.md`, `CLAUDE.md`

- [ ] **Step 1: `docs/data-model.md`**

Add the eight health tables with every column, `blocking_reports.source` with its NULL convention
(NULL means `'event'`), and the three new `ingestion_runs` counters. Update the ingestion-quality
"useful query": it sums the named counters and must keep reconciling to `events_read`, so add the
three new ones to it.

- [ ] **Step 2: `docs/cli-reference.md`**

Add the `export-health` section with the flag table and a sample output whose first block is
coverage. Update the usage line. In the `import` section, update the "Recognized events" table with
`sp_server_diagnostics_component_result` and correct the sentence saying anything else is counted
as unmapped. Add the three counters to the sample import summary line.

- [ ] **Step 3: `docs/privacy.md`**

Add a row for the health tables to the artifact table, saying plainly that
`health_pending_io.file_path`, `health_cpu_requests.session_id` and
`health_memory_entries.description` are stored verbatim under **every** redaction mode, `full`
included, and that no flag removes them. Add a line saying embedded blocking reports pass the same
gate as event-sourced ones.

- [ ] **Step 4: `docs/blocking.md`**

Document the `source` distinction wherever the blocking tables and their raw-SQL examples appear,
including the `coalesce(source,'event')` predicate shape, and note that `export-events` returns
event-sourced reports only.

- [ ] **Step 5: `docs/capture-session.md`**

`system_health` is always on and needs no session; its ring rotates and its history is short; a
capture folder may contain more than one sampling series, and what that costs.

- [ ] **Step 6: The command tables and the skill**

`docs/README.md` and the root `README.md` each carry a table of the command set — now nine.
`CLAUDE.md` section A says "eight of them"; section B's "nothing silently dropped" invariant needs
the three new counters. In `.agents/skills/analyzing-xel-workloads/SKILL.md` (French), add the trap:
never read a health digest without reading its coverage block first, and the counters are
cumulative since instance start, so only the deltas the digest prints are activity.

- [ ] **Step 7: Verify and commit**

Run: `dotnet build && dotnet test`
Then re-read `docs/data-model.md`'s ingestion-quality query and confirm by hand that its counter
list now accounts for every event an import can produce.

```bash
git add -A
git commit -m "docs: System Health tables, export-health, and the privacy and coverage caveats"
```

---

## Self-review

**Spec coverage, checked item by item rather than section by section.** Revision 1 ticked
"§9 → Tasks 8–11" and shipped four unimplemented digest sections. §9 has seven numbered items plus
a coverage block; each gets its own line.

| Spec | Task |
|---|---|
| §1 coverage, interleaved series | 4 (grouping), 8 (query), 11 (notes) |
| §2 scope, unhandled components | 3, 6 |
| §3 payload shapes | 3 |
| §4 routing, three outcomes, counters | 3, 6 |
| §5 reuse, `source`, write path, seven readers, `EventExport` | 1, 2, 7 |
| §6 schema, cycle key, name/value, units | 3, 4, 5 |
| §7 privacy: gate reuse, `@command` shape, `filePath` | 3 (assertion), 7, 13 (docs) |
| §8 no version constant, versioned envelope | 11 |
| §9 coverage block incl. non-diagnostics share | 8, 10 (step 5) |
| §9 item 1 non-clean cycles | **10** |
| §9 item 2 waits by count delta, span, restarts | 9 |
| §9 item 3 memory pressure and movers | **10** |
| §9 item 4 stability signals as deltas | 9, 11 |
| §9 item 5 worker pressure as gauges | 9, 11 |
| §9 item 6 I/O and worst pending requests | **10** |
| §9 item 7 blocking in diagnostics cycles | **10** |
| §9 `--limit`, `--out` traversal, `--format both`, empty project | 12 |
| §10 what it promises | 11 (notes), 13 (docs) |
| §11 architecture | satisfied by construction |
| §12 performance | no task needed |
| §13 tests, incl. the `sample/`-gated one | inside each task; the gated one is Task 6 step 8 |
| §14 documentation | 13 |

Every table created in Task 5 is read by a query in Task 8, 9 or 10 — the check revision 1 did not
make, and the one that would have caught its largest defect.

**Not covered, deliberately.** §15's open question 1 (the series-detection heuristic) is implemented
as the simplest rule that passes Task 4's tests, and the digest announces the series count rather
than hiding it. §15 question 4 (the other three positional inserts in `executions`,
`blocking_processes` and `deadlock_reports`) stays out of scope, and Task 1 says so at the site.

**Type consistency.** `ServerDiagnosticsSample`, `HealthCycle`, `HealthCoverage`, `WaitDelta`,
`HealthScalar`, `StateCount`, `MemoryMovement`, `PendingIoRow`, `DiagnosticsBlockingRow` and
`HealthDigestEnvelope` are defined once and used with the same member names later.
`PreparedBlockingReport.Source` (Task 1) is consumed in Task 6. `InsertHealthCycles` (Task 5) is
consumed in Task 6. `WaitDeltas` / `ScalarGauges` / `ScalarDeltas` (Task 9) and `NonCleanStates` /
`MemoryMovers` / `WorstPendingIo` / `DiagnosticsBlocking` (Task 10) are consumed in Task 11.
`HealthCoverage` gains `NonDiagnosticsShare` in Task 10 step 5, which is why that step also amends
the Task 8 test.

**Names verified against the codebase, not remembered.** `Arg(name, fallback)` and
`BlockingDigestMarkdown.HasTraversal` (Task 12), `NextBlockingReportId` and the `Add(c, "$n", v)`
helper (Tasks 1, 5), `IngestionService.Prepare(BlockingReport, string?)` (Task 6). Revision 1 named
two helpers that do not exist anywhere in the repository; every identifier this plan borrows from
existing code has now been grepped.

**Ordering.** 1 precedes 2 (the column must exist). 2 precedes 7 — the existing readers must be
isolated *before* the second source starts writing, or the first symptom is a contaminated digest
rather than a failing test. 3, 4, 5 precede 6. 6 precedes 7. 8, 9, 10 precede 11. 11 precedes 12.
13 last, because it documents what the others settled.

**Counter accounting, stated once so no task drifts from it.** The event counters —
`events_mapped`, `events_unmapped`, `events_cleaned`, `events_blocking`, `events_deadlocks`,
`events_plan_profiles`, `events_server_diagnostics`, `events_server_diagnostics_unhandled`,
`server_diagnostics_parse_failures` — sum to `events_read`, and Task 6 asserts it.
`server_diagnostics_embedded_blocking` and `server_diagnostics_embedded_blocking_failures` count
sub-documents of a single event and are **outside** that sum. Task 13 must say so where it
publishes the reconciliation query, or the next reader will add them and break it.
