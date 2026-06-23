# Blocking Ingestion + AI Digest Export — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ingest SQL Server `blocked_process_report` (and, lightly, `xml_deadlock_report`) `.xel` events into DuckDB and expose a host-agnostic blocking digest via a CLI `export-blocking` command (JSON + markdown, with a `--full` NDJSON escape hatch).

**Architecture:** New `Model` records for blocking (not `ExecutionEvent`); a pure `WaitResourceParser` (the locality classifier) + `BlockingReportParser`/`DeadlockReportParser`; routing in `IngestionService` into new DuckDB tables; aggregation in DuckDB SQL (`BlockingQueries`); a reusable `BlockingDigest` assembler producing `BlockingDigestResult`; CLI serializes the record (the MCP server in Spec 2 reuses the same record). Formatting lives in hosts.

**Tech Stack:** .NET 10 / C# 14, DuckDB.NET.Data.Full 1.5.3, `System.Xml.Linq` (XML parse — no new dependency), `System.Text.Json`, xUnit + Xunit.SkippableFact.

## Global Constraints

Copied verbatim from the project's hard invariants (CLAUDE.md) — every task implicitly includes these:

- **Microseconds everywhere in Core.** Durations/waits stored as `*_us` (`long`); formatting to ms/s happens only in hosts. The blocked-process-report `waittime` attribute is **milliseconds** → multiply by 1000 at the parse boundary to store `WaitTimeUs`.
- **Redaction before any value is written to disk.** Reuse `RedactionPolicy`. For blocking, the `inputbuf` is free SQL with inline literals (patient NIR / birth date): store the **normalized** SQL by default; store raw `inputbuf` only when `--redaction off`.
- **Nothing silently dropped.** New counters (`Blocking`, `Deadlocks`, `BlockingParseFailures`) are mutually exclusive and exhaustive with the existing ones; a `blocked_process_report` is no longer counted `unmapped`.
- **`QueryNormalizer.Version = 1`** reused for `inputbuf` normalization; `inputbuf_fingerprint` joins `normalized_queries`.
- **SQL safety.** User free-text bound as `$name`; only allow-listed identifiers interpolated; `--out` path rejects traversal.
- **KISS / no-DI.** Plain `record`/POCO, static parsers, primary-constructor services; aggregation in DuckDB SQL (never C# reduction loops). The only Core abstraction stays `IXeEventData`.
- **DuckDB.NET 1.5.3 param convention:** SQL placeholder is `$name`; `ParameterName` is set **without** the leading `$` (use the existing `Add` helper pattern).
- **Build/test:** `dotnet build` (0 warnings expected); `dotnet test` (1 skipped = env-gated live-SQL test). Focused: `dotnet test --filter <TestClassName>`.

## File Structure

**Create (Core):**
- `src/SqlFerret.Core/Model/Blocking.cs` — `WaitResourceType` enum, `BlockingProcess`, `BlockingReport`, `DeadlockReport` records.
- `src/SqlFerret.Core/Ingestion/WaitResourceParser.cs` — `WaitResourceInfo` + `WaitResourceParser.Parse`.
- `src/SqlFerret.Core/Ingestion/BlockingReportParser.cs` — `BlockingReportParser.Parse`, `DeadlockReportParser.Parse`.
- `src/SqlFerret.Core/Storage/PreparedBlocking.cs` — `PreparedBlockingProcess`, `PreparedBlockingReport`.
- `src/SqlFerret.Core/Analysis/BlockingQueries.cs` — DuckDB rollups.
- `src/SqlFerret.Core/Analysis/BlockingResults.cs` — result records + `BlockingDigestResult` + `BlockingDigestEnvelope`.
- `src/SqlFerret.Core/Analysis/BlockingDigest.cs` — assembler.

**Modify (Core):**
- `src/SqlFerret.Core/Ingestion/EventMapper.cs` — blocking classification + XML extraction.
- `src/SqlFerret.Core/Ingestion/IngestionResult.cs` — add counters.
- `src/SqlFerret.Core/Ingestion/IngestionService.cs` — route blocking events.
- `src/SqlFerret.Core/Storage/DuckDbProject.cs` — new tables + insert/finish methods.

**Modify (Cli):**
- `src/SqlFerret.Cli/Program.cs` — `export-blocking` command.
- `src/SqlFerret.Cli/BlockingDigestMarkdown.cs` (Create) — markdown renderer (host formatting).

**Tests (Create):**
- `tests/SqlFerret.Core.Tests/WaitResourceParserTests.cs`
- `tests/SqlFerret.Core.Tests/BlockingReportParserTests.cs`
- `tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs`
- `tests/SqlFerret.Core.Tests/BlockingQueriesTests.cs`
- `tests/SqlFerret.Core.Tests/BlockingDigestTests.cs`

---

### Task 1: Blocking model + WaitResourceParser

**Files:**
- Create: `src/SqlFerret.Core/Model/Blocking.cs`
- Create: `src/SqlFerret.Core/Ingestion/WaitResourceParser.cs`
- Test: `tests/SqlFerret.Core.Tests/WaitResourceParserTests.cs`

**Interfaces:**
- Produces: `enum WaitResourceType { Key, Object, Page, Rid, Database, PageLatch, AppLock, Other }`;
  `record BlockingProcess(int? Spid, int? Ecid, string? Status, string? WaitResourceRaw, WaitResourceType WaitResourceType, long? ObjectId, long? HobtId, long? WaitTimeUs, string? LockMode, string? IsolationLevel, int? TranCount, string? ClientApp, string? HostName, string? LoginName, string? InputBufRaw, string? InputBufFingerprint)`;
  `record BlockingReport(DateTime CapturedAt, int? MonitorLoop, int? DatabaseId, BlockingProcess Blocked, BlockingProcess Blocking)`;
  `record DeadlockReport(DateTime CapturedAt, IReadOnlyList<int> VictimSpids, IReadOnlyList<int> ParticipantSpids, string GraphXmlRedacted)`;
  `record WaitResourceInfo(WaitResourceType Type, int? DatabaseId, long? ObjectId, long? HobtId)`;
  `static class WaitResourceParser { static WaitResourceInfo Parse(string? raw); }`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/WaitResourceParserTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;

public class WaitResourceParserTests
{
    [Theory]
    [InlineData("OBJECT: 5:1977058079:0", WaitResourceType.Object, 5, 1977058079L)]
    [InlineData("KEY: 5:72057594041204736 (8194443284a0)", WaitResourceType.Key, 5, null)]
    [InlineData("PAGE: 6:1:70ableau", WaitResourceType.Page, 6, null)]
    [InlineData("RID: 5:1:8956:0", WaitResourceType.Rid, 5, null)]
    [InlineData("DATABASE: 2:38 ", WaitResourceType.Database, 2, null)]
    public void Parse_classifies_resource_and_extracts_ids(string raw, WaitResourceType type, int? db, long? objId)
    {
        var info = WaitResourceParser.Parse(raw);
        Assert.Equal(type, info.Type);
        Assert.Equal(db, info.DatabaseId);
        Assert.Equal(objId, info.ObjectId);
    }

    [Theory]
    [InlineData("PAGELATCH_EX: 2:1:128", WaitResourceType.PageLatch)]
    [InlineData("APPLICATION: 5:0:[Form]", WaitResourceType.AppLock)]
    [InlineData(null, WaitResourceType.Other)]
    [InlineData("", WaitResourceType.Other)]
    public void Parse_handles_latch_applock_and_empty(string? raw, WaitResourceType type)
        => Assert.Equal(type, WaitResourceParser.Parse(raw).Type);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter WaitResourceParserTests`
Expected: FAIL — `WaitResourceParser`/`WaitResourceType` do not exist (compile error).

- [ ] **Step 3: Write the model and the parser**

```csharp
// src/SqlFerret.Core/Model/Blocking.cs
namespace SqlFerret.Core.Model;

public enum WaitResourceType { Key, Object, Page, Rid, Database, PageLatch, AppLock, Other }

public record BlockingProcess(
    int? Spid, int? Ecid, string? Status,
    string? WaitResourceRaw, WaitResourceType WaitResourceType,
    long? ObjectId, long? HobtId,
    long? WaitTimeUs, string? LockMode, string? IsolationLevel, int? TranCount,
    string? ClientApp, string? HostName, string? LoginName,
    string? InputBufRaw, string? InputBufFingerprint);

public record BlockingReport(
    DateTime CapturedAt, int? MonitorLoop, int? DatabaseId,
    BlockingProcess Blocked, BlockingProcess Blocking);

public record DeadlockReport(
    DateTime CapturedAt, IReadOnlyList<int> VictimSpids,
    IReadOnlyList<int> ParticipantSpids, string GraphXmlRedacted);
```

```csharp
// src/SqlFerret.Core/Ingestion/WaitResourceParser.cs
using System.Globalization;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

public record WaitResourceInfo(WaitResourceType Type, int? DatabaseId, long? ObjectId, long? HobtId);

/// <summary>Classifies a blocked-process waitresource string. The resource TYPE is the locality signal:
/// OBJECT/KEY/PAGE/RID = a user object (potentially tenant-local); DATABASE/PAGELATCH/APPLICATION = shared.</summary>
public static class WaitResourceParser
{
    public static WaitResourceInfo Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(WaitResourceType.Other, null, null, null);
        var s = raw.Trim();
        int colon = s.IndexOf(':');
        string head = (colon < 0 ? s : s[..colon]).Trim().ToUpperInvariant();
        string rest = colon < 0 ? "" : s[(colon + 1)..].Trim();

        var type = head switch
        {
            "OBJECT" => WaitResourceType.Object,
            "KEY"    => WaitResourceType.Key,
            "PAGE"   => WaitResourceType.Page,
            "RID"    => WaitResourceType.Rid,
            "DATABASE" => WaitResourceType.Database,
            "APPLICATION" => WaitResourceType.AppLock,
            _ => head.StartsWith("PAGELATCH") || head.StartsWith("PAGEIOLATCH") ? WaitResourceType.PageLatch
               : head.StartsWith("APPLICATION") ? WaitResourceType.AppLock
               : WaitResourceType.Other
        };

        // tokens after the head are colon-separated numerics: db[:object|hobt[:index]]
        var parts = rest.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries);
        int? db = parts.Length > 0 && int.TryParse(parts[0], out var d) ? d : null;
        long? objId = type == WaitResourceType.Object && parts.Length > 1 && long.TryParse(parts[1], out var o) ? o : null;
        long? hobt = type == WaitResourceType.Key && parts.Length > 1 && long.TryParse(parts[1], out var h) ? h : null;
        return new(type, db, objId, hobt);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter WaitResourceParserTests`
Expected: PASS (8 cases).

- [ ] **Step 5: Commit**

```bash
git add src/SqlFerret.Core/Model/Blocking.cs src/SqlFerret.Core/Ingestion/WaitResourceParser.cs tests/SqlFerret.Core.Tests/WaitResourceParserTests.cs
git commit -m "feat(core): blocking model + waitresource locality parser"
```

---

### Task 2: BlockingReportParser + DeadlockReportParser

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/BlockingReportParser.cs`
- Test: `tests/SqlFerret.Core.Tests/BlockingReportParserTests.cs`

**Interfaces:**
- Consumes: `BlockingReport`, `BlockingProcess`, `DeadlockReport`, `WaitResourceParser.Parse` (Task 1).
- Produces: `static class BlockingReportParser { static BlockingReport? Parse(string reportXml, DateTime capturedAt); }`;
  `static class DeadlockReportParser { static DeadlockReport? Parse(string deadlockXml, DateTime capturedAt); }`.
  Both return `null` on malformed/empty XML (caller counts it). `InputBufRaw` is set; `InputBufFingerprint` stays `null` (normalization/redaction happens in `IngestionService`, Task 5). `WaitTimeUs = waittime_ms * 1000`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingReportParserTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;

public class BlockingReportParserTests
{
    // synthetic, no PII — shape mirrors a real blocked_process_report
    private const string ReportXml = """
    <blocked-process-report monitorLoop="42">
      <blocked-process>
        <process id="p1" waitresource="KEY: 5:72057594041204736 (x)" waittime="5972"
                 spid="201" status="suspended" trancount="2" lockMode="S"
                 isolationlevel="read committed (2)" clientapp="WedaApp" hostname="WS1" loginname="svc">
          <inputbuf>exec dbo.ASP_Select_FSE @CabinetID=897,@Nir='2921225462283'</inputbuf>
        </process>
      </blocked-process>
      <blocking-process>
        <process id="p2" spid="118" status="sleeping" trancount="1" clientapp="WedaApp" hostname="WS2" loginname="svc">
          <inputbuf>UPDATE dbo.T_FeuilleSoinsElectronique_Fse SET Fse_TM=0 WHERE Fse_ID=42</inputbuf>
        </process>
      </blocking-process>
    </blocked-process-report>
    """;

    [Fact]
    public void Parse_extracts_both_processes_and_units()
    {
        var r = BlockingReportParser.Parse(ReportXml, new DateTime(2026, 2, 24));
        Assert.NotNull(r);
        Assert.Equal(42, r!.MonitorLoop);
        Assert.Equal(201, r.Blocked.Spid);
        Assert.Equal(WaitResourceType.Key, r.Blocked.WaitResourceType);
        Assert.Equal(5_972_000L, r.Blocked.WaitTimeUs);            // ms -> us
        Assert.Equal("S", r.Blocked.LockMode);
        Assert.Equal(2, r.Blocked.TranCount);
        Assert.Contains("ASP_Select_FSE", r.Blocked.InputBufRaw);
        Assert.Null(r.Blocked.InputBufFingerprint);                // set later, in IngestionService
        Assert.Equal(118, r.Blocking.Spid);
        Assert.Contains("UPDATE", r.Blocking.InputBufRaw);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("<blocked-process-report></blocked-process-report>")]   // no processes
    public void Parse_returns_null_on_malformed_or_empty(string xml)
        => Assert.Null(BlockingReportParser.Parse(xml, DateTime.UnixEpoch));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingReportParserTests`
Expected: FAIL — `BlockingReportParser` does not exist.

- [ ] **Step 3: Write the parsers**

```csharp
// src/SqlFerret.Core/Ingestion/BlockingReportParser.cs
using System.Globalization;
using System.Xml.Linq;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

public static class BlockingReportParser
{
    public static BlockingReport? Parse(string reportXml, DateTime capturedAt)
    {
        if (string.IsNullOrWhiteSpace(reportXml)) return null;
        XElement root;
        try { root = XElement.Parse(reportXml); }
        catch { return null; }   // malformed → caller counts a parse failure

        var blocked = ParseProcess(root.Element("blocked-process")?.Element("process"));
        var blocking = ParseProcess(root.Element("blocking-process")?.Element("process"));
        if (blocked is null || blocking is null) return null;

        int? loop = IntAttr(root, "monitorLoop");
        return new BlockingReport(capturedAt, loop, blocked.DatabaseHint, blocked.Process, blocking.Process);
    }

    private sealed record Parsed(BlockingProcess Process, int? DatabaseHint);

    private static Parsed? ParseProcess(XElement? p)
    {
        if (p is null) return null;
        string? waitRaw = (string?)p.Attribute("waitresource");
        var wr = WaitResourceParser.Parse(waitRaw);
        long? waitUs = IntAttr(p, "waittime") is int ms ? ms * 1000L : null;
        var proc = new BlockingProcess(
            Spid: IntAttr(p, "spid"), Ecid: IntAttr(p, "ecid"), Status: (string?)p.Attribute("status"),
            WaitResourceRaw: waitRaw, WaitResourceType: wr.Type, ObjectId: wr.ObjectId, HobtId: wr.HobtId,
            WaitTimeUs: waitUs, LockMode: (string?)p.Attribute("lockMode"),
            IsolationLevel: (string?)p.Attribute("isolationlevel"), TranCount: IntAttr(p, "trancount"),
            ClientApp: (string?)p.Attribute("clientapp"), HostName: (string?)p.Attribute("hostname"),
            LoginName: (string?)p.Attribute("loginname"),
            InputBufRaw: p.Element("inputbuf")?.Value.Trim(), InputBufFingerprint: null);
        return new Parsed(proc, wr.DatabaseId);
    }

    private static int? IntAttr(XElement e, string name) =>
        int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public static class DeadlockReportParser
{
    public static DeadlockReport? Parse(string deadlockXml, DateTime capturedAt)
    {
        if (string.IsNullOrWhiteSpace(deadlockXml)) return null;
        XElement root;
        try { root = XElement.Parse(deadlockXml); }
        catch { return null; }

        var victims = root.Descendants("victim-list").Elements("victimProcess")
            .Select(v => Spid((string?)v.Attribute("id"))).OfType<int>().ToList();
        var participants = root.Descendants("process")
            .Select(pr => ParseInt((string?)pr.Attribute("spid"))).OfType<int>().Distinct().ToList();
        if (participants.Count == 0) return null;
        return new DeadlockReport(capturedAt, victims, participants, deadlockXml);
    }

    // victim id is a process token, not a spid; participants carry the real spid. Victim spids are
    // resolved by matching ids in a full implementation; for the light version we keep participant spids
    // and an empty victim list when ids don't resolve to spids.
    private static int? Spid(string? _) => null;
    private static int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter BlockingReportParserTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlFerret.Core/Ingestion/BlockingReportParser.cs tests/SqlFerret.Core.Tests/BlockingReportParserTests.cs
git commit -m "feat(core): parse blocked-process and deadlock report XML"
```

---

### Task 3: EventMapper blocking classification

**Files:**
- Modify: `src/SqlFerret.Core/Ingestion/EventMapper.cs`
- Test: `tests/SqlFerret.Core.Tests/BlockingReportParserTests.cs` (add a class) or new — use `EventMapperTests.cs` additions; here a focused new test in `BlockingIngestionTests.cs` Step is deferred to Task 5. Add a small test inline.

**Interfaces:**
- Produces: `enum BlockingEventKind { None, Blocked, Deadlock }`;
  `static BlockingEventKind EventMapper.ClassifyBlocking(string name)`;
  `static string? EventMapper.ExtractBlockingXml(IXeEventData ev)` (field `blocked_process`);
  `static string? EventMapper.ExtractDeadlockXml(IXeEventData ev)` (field `xml_report`).
- NOTE (spec §9): confirm field names `blocked_process` / `xml_report` against XELite on the real sample in Task 6's integration test; they are isolated here for a one-line fix.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingClassifyTests.cs
using SqlFerret.Core.Ingestion;

public class BlockingClassifyTests
{
    [Theory]
    [InlineData("blocked_process_report", BlockingEventKind.Blocked)]
    [InlineData("xml_deadlock_report", BlockingEventKind.Deadlock)]
    [InlineData("rpc_completed", BlockingEventKind.None)]
    public void ClassifyBlocking_recognizes_report_events(string name, BlockingEventKind kind)
        => Assert.Equal(kind, EventMapper.ClassifyBlocking(name));

    [Fact]
    public void ExtractBlockingXml_reads_blocked_process_field()
    {
        var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
            new Dictionary<string, object?> { ["blocked_process"] = "<blocked-process-report/>" },
            new Dictionary<string, object?>());
        Assert.Equal("<blocked-process-report/>", EventMapper.ExtractBlockingXml(ev));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingClassifyTests`
Expected: FAIL — `ClassifyBlocking` / `BlockingEventKind` do not exist.

- [ ] **Step 3: Add to EventMapper**

Add this enum and these methods to `src/SqlFerret.Core/Ingestion/EventMapper.cs` (the `Str` helper already exists in the class):

```csharp
public enum BlockingEventKind { None, Blocked, Deadlock }
```

```csharp
    public static BlockingEventKind ClassifyBlocking(string name) =>
        name.Equals("blocked_process_report", StringComparison.OrdinalIgnoreCase) ? BlockingEventKind.Blocked :
        name.Equals("xml_deadlock_report", StringComparison.OrdinalIgnoreCase) ? BlockingEventKind.Deadlock :
        BlockingEventKind.None;

    public static string? ExtractBlockingXml(IXeEventData ev) => Str(ev.Fields, "blocked_process");
    public static string? ExtractDeadlockXml(IXeEventData ev) => Str(ev.Fields, "xml_report");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter BlockingClassifyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlFerret.Core/Ingestion/EventMapper.cs tests/SqlFerret.Core.Tests/BlockingClassifyTests.cs
git commit -m "feat(core): classify and extract blocking/deadlock report events"
```

---

### Task 4: DuckDB storage for blocking

**Files:**
- Create: `src/SqlFerret.Core/Storage/PreparedBlocking.cs`
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs`
- Test: `tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs` (schema + insert portion)

**Interfaces:**
- Consumes: `BlockingReport`, `BlockingProcess`, `DeadlockReport` (Task 1), `NormalizedQuery` (existing).
- Produces:
  `record PreparedBlockingProcess(BlockingProcess Process, NormalizedQuery? Normalized, string? StoredInputBuf)`;
  `record PreparedBlockingReport(BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking)`;
  `long DuckDbProject.NextBlockingReportId()`;
  `void DuckDbProject.InsertBlockingBatch(long runId, IReadOnlyList<PreparedBlockingReport> reports)`;
  `void DuckDbProject.InsertDeadlockBatch(long runId, IReadOnlyList<DeadlockReport> reports)`.
  Tables: `blocking_reports`, `blocking_processes`, `deadlock_reports`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;

public class BlockingIngestionTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static BlockingProcess Proc(int spid, WaitResourceType t, long? waitUs, string sql) =>
        new(spid, 0, "suspended", "KEY: 5:1 (x)", t, null, null, waitUs, "S", "read committed (2)", 1,
            "WedaApp", "WS", "svc", sql, "fp_" + spid);

    [Fact]
    public void InsertBlockingBatch_persists_reports_and_processes()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            var report = new BlockingReport(new DateTime(2026, 2, 24), 42, 5,
                Proc(201, WaitResourceType.Key, 5_972_000L, "exec dbo.X"),
                Proc(118, WaitResourceType.Other, null, "update dbo.Y"));
            var prepared = new PreparedBlockingReport(report,
                new PreparedBlockingProcess(report.Blocked, null, "exec dbo.X"),
                new PreparedBlockingProcess(report.Blocking, null, "update dbo.Y"));

            db.InsertBlockingBatch(runId, [prepared]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM blocking_reports";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM blocking_processes";
            Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT wait_time_us FROM blocking_processes WHERE role='blocked'";
            Assert.Equal(5_972_000L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingIngestionTests`
Expected: FAIL — `InsertBlockingBatch`/tables do not exist.

- [ ] **Step 3: Add the records and storage methods**

```csharp
// src/SqlFerret.Core/Storage/PreparedBlocking.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Storage;

public record PreparedBlockingProcess(BlockingProcess Process, NormalizedQuery? Normalized, string? StoredInputBuf);
public record PreparedBlockingReport(BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking);
```

In `DuckDbProject.CreateSchema`, append to the `cmd.CommandText` (before the closing `"""`):

```sql
        CREATE TABLE IF NOT EXISTS blocking_reports (
          report_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP,
          monitor_loop INTEGER, database_id INTEGER);

        CREATE TABLE IF NOT EXISTS blocking_processes (
          report_id BIGINT, role TEXT, spid INTEGER, ecid INTEGER, status TEXT,
          wait_resource_raw TEXT, wait_resource_type TEXT, object_id BIGINT, hobt_id BIGINT,
          wait_time_us BIGINT, lock_mode TEXT, isolation_level TEXT, trancount INTEGER,
          client_app TEXT, host_name TEXT, login_name TEXT,
          inputbuf TEXT, inputbuf_fingerprint TEXT);

        CREATE TABLE IF NOT EXISTS deadlock_reports (
          report_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP,
          victim_spids TEXT, participant_spids TEXT, graph_xml TEXT);
```

Add fields + methods to `DuckDbProject` (mirror `NextExecutionId`/`InsertBatch` patterns; reuse the existing `Add` helper and `UpsertSignature` for normalized inputbufs):

```csharp
    private long _nextBlockingReportId = -1;

    public long NextBlockingReportId()
    {
        if (_nextBlockingReportId < 0)
            _nextBlockingReportId = Scalar("SELECT COALESCE(MAX(report_id),0) FROM blocking_reports");
        return ++_nextBlockingReportId;
    }

    public void InsertBlockingBatch(long runId, IReadOnlyList<PreparedBlockingReport> reports)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var pr in reports)
        {
            long id = NextBlockingReportId();
            using (var c = Connection.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT INTO blocking_reports VALUES ($id,$run,$ts,$loop,$db)";
                Add(c, "$id", id); Add(c, "$run", runId); Add(c, "$ts", pr.Report.CapturedAt);
                Add(c, "$loop", (object?)pr.Report.MonitorLoop); Add(c, "$db", (object?)pr.Report.DatabaseId);
                c.ExecuteNonQuery();
            }
            InsertBlockingProcess(tx, id, "blocked", pr.Blocked);
            InsertBlockingProcess(tx, id, "blocking", pr.Blocking);
        }
        tx.Commit();
    }

    private void InsertBlockingProcess(DuckDBTransaction tx, long reportId, string role, PreparedBlockingProcess p)
    {
        var proc = p.Process;
        using var c = Connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = """
          INSERT INTO blocking_processes VALUES ($rid,$role,$spid,$ecid,$status,$wrr,$wrt,$oid,$hobt,
            $wus,$lm,$iso,$tc,$app,$host,$login,$buf,$fp)
          """;
        Add(c, "$rid", reportId); Add(c, "$role", role); Add(c, "$spid", (object?)proc.Spid);
        Add(c, "$ecid", (object?)proc.Ecid); Add(c, "$status", (object?)proc.Status);
        Add(c, "$wrr", (object?)proc.WaitResourceRaw); Add(c, "$wrt", proc.WaitResourceType.ToString());
        Add(c, "$oid", (object?)proc.ObjectId); Add(c, "$hobt", (object?)proc.HobtId);
        Add(c, "$wus", (object?)proc.WaitTimeUs); Add(c, "$lm", (object?)proc.LockMode);
        Add(c, "$iso", (object?)proc.IsolationLevel); Add(c, "$tc", (object?)proc.TranCount);
        Add(c, "$app", (object?)proc.ClientApp); Add(c, "$host", (object?)proc.HostName);
        Add(c, "$login", (object?)proc.LoginName); Add(c, "$buf", (object?)p.StoredInputBuf);
        Add(c, "$fp", (object?)proc.InputBufFingerprint);
        c.ExecuteNonQuery();
        if (p.Normalized is { } n)
        {
            using var u = Connection.CreateCommand(); u.Transaction = tx;
            u.CommandText = """
              INSERT INTO normalized_queries VALUES ($h,$sql,$kind,$tbl,$ver,$ts,$ts)
              ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at = $ts
              """;
            Add(u, "$h", n.NormalizedHash); Add(u, "$sql", n.NormalizedSql); Add(u, "$kind", n.StatementKind);
            Add(u, "$tbl", (object?)n.PrimaryTable); Add(u, "$ver", QueryNormalizer.Version); Add(u, "$ts", p.Process is not null ? reportId : reportId);
            u.ExecuteNonQuery();
        }
    }

    public void InsertDeadlockBatch(long runId, IReadOnlyList<DeadlockReport> reports)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var d in reports)
        {
            long id = NextBlockingReportId();
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO deadlock_reports VALUES ($id,$run,$ts,$v,$p,$g)";
            Add(c, "$id", id); Add(c, "$run", runId); Add(c, "$ts", d.CapturedAt);
            Add(c, "$v", string.Join(',', d.VictimSpids)); Add(c, "$p", string.Join(',', d.ParticipantSpids));
            Add(c, "$g", d.GraphXmlRedacted);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }
```

NOTE: the `Add(u, "$ts", ...)` for `normalized_queries` needs a timestamp; use the report's `CapturedAt`. Fix the line to `Add(u, "$ts", pr_capturedAt)` by passing `p.Process` carrier — simplest: change `InsertBlockingProcess` to also take `DateTime capturedAt` and use it for `$ts`. Update the two call sites to pass `pr.Report.CapturedAt`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter BlockingIngestionTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlFerret.Core/Storage/PreparedBlocking.cs src/SqlFerret.Core/Storage/DuckDbProject.cs tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs
git commit -m "feat(core): DuckDB tables + inserts for blocking/deadlock reports"
```

---

### Task 5: IngestionService routing + counters

**Files:**
- Modify: `src/SqlFerret.Core/Ingestion/IngestionResult.cs`
- Modify: `src/SqlFerret.Core/Ingestion/IngestionService.cs`
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (extend `ingestion_runs` + `FinishRun`)
- Modify: `src/SqlFerret.Cli/Program.cs` (import summary line)
- Test: `tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs` (add end-to-end ingest test)

**Interfaces:**
- Consumes: `EventMapper.ClassifyBlocking/ExtractBlockingXml/ExtractDeadlockXml` (Task 3), `BlockingReportParser`/`DeadlockReportParser` (Task 2), `DuckDbProject.InsertBlockingBatch/InsertDeadlockBatch` (Task 4), `QueryNormalizer`/`RedactionPolicy` (existing).
- Produces: `IngestionResult` gains `long Blocking, long Deadlocks, long BlockingParseFailures`;
  `DuckDbProject.FinishRun` gains `long blocking, long deadlocks, long blockingParseFailures`.

- [ ] **Step 1: Write the failing test**

```csharp
// add to tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs
    [Fact]
    public void Ingest_routes_blocking_report_and_counts_it()
    {
        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var xml = "<blocked-process-report monitorLoop=\"1\">" +
                      "<blocked-process><process spid=\"201\" waitresource=\"OBJECT: 5:99:0\" waittime=\"5000\">" +
                      "<inputbuf>exec dbo.X @Nir='2921225462283'</inputbuf></process></blocked-process>" +
                      "<blocking-process><process spid=\"118\"><inputbuf>update dbo.Y set a=1 where id=2</inputbuf></process></blocking-process>" +
                      "</blocked-process-report>";
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = xml },
                new Dictionary<string, object?>());

            var result = new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Masked, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            Assert.Equal(1, result.Blocking);
            Assert.Equal(0, result.Unmapped);     // not counted as unmapped

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT inputbuf FROM blocking_processes WHERE role='blocked'";
            var stored = (string)c.ExecuteScalar()!;
            Assert.DoesNotContain("2921225462283", stored);   // literal redacted via normalization
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingIngestionTests`
Expected: FAIL — `result.Blocking` does not exist / blocking not routed.

- [ ] **Step 3: Extend the record, schema, FinishRun, and the ingest loop**

`IngestionResult.cs`:

```csharp
public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned,
    long TokenizeFailures, long Blocking, long Deadlocks, long BlockingParseFailures);
```

`DuckDbProject` — add columns to `ingestion_runs` in `CreateSchema` (new projects only; project files are disposable):

```sql
          , events_blocking BIGINT, events_deadlocks BIGINT, blocking_parse_failures BIGINT
```
(append inside the `ingestion_runs` column list, before the closing `)`). Update the `BeginRun` INSERT to set the three new columns to `0`, and extend `FinishRun`:

```csharp
    public void FinishRun(long runId, long read, long mapped, long unmapped, long cleaned,
        long tokenizeFailures, long blocking, long deadlocks, long blockingParseFailures)
    {
        using var c = Connection.CreateCommand();
        c.CommandText = """
          UPDATE ingestion_runs SET finished_at = now(), events_read=$r, events_mapped=$m,
            events_unmapped=$u, events_cleaned=$cl, tokenize_failures=$tf,
            events_blocking=$bl, events_deadlocks=$dl, blocking_parse_failures=$bpf WHERE run_id=$id
          """;
        Add(c, "$r", read); Add(c, "$m", mapped); Add(c, "$u", unmapped); Add(c, "$cl", cleaned);
        Add(c, "$tf", tokenizeFailures); Add(c, "$bl", blocking); Add(c, "$dl", deadlocks);
        Add(c, "$bpf", blockingParseFailures); Add(c, "$id", runId);
        c.ExecuteNonQuery();
    }
```
Update `BeginRun`'s INSERT column list + VALUES to include `events_blocking, events_deadlocks, blocking_parse_failures` = `0,0,0`.

`IngestionService.Ingest` — at the top of the `foreach`, before `EventMapper.Map`, branch on blocking:

```csharp
            var bkind = EventMapper.ClassifyBlocking(ev.Name);
            if (bkind != BlockingEventKind.None)
            {
                if (bkind == BlockingEventKind.Blocked)
                {
                    var xml = EventMapper.ExtractBlockingXml(ev);
                    var rep = xml is null ? null : BlockingReportParser.Parse(xml, ev.Timestamp);
                    if (rep is null) { blockingParseFailures++; continue; }
                    project.InsertBlockingBatch(runId, [Prepare(rep)]);
                    blocking++;
                }
                else
                {
                    var xml = EventMapper.ExtractDeadlockXml(ev);
                    var dl = xml is null ? null : DeadlockReportParser.Parse(xml, ev.Timestamp);
                    if (dl is null) { blockingParseFailures++; continue; }
                    project.InsertDeadlockBatch(runId, [dl with { GraphXmlRedacted = options.Redaction == RedactionMode.Off ? dl.GraphXmlRedacted : "<redacted/>" }]);
                    deadlocks++;
                }
                continue;
            }
```

Declare the new counters next to the others (`long blocking = 0, deadlocks = 0, blockingParseFailures = 0;`), and add a private helper that normalizes + redacts each inputbuf:

```csharp
    private PreparedBlockingReport Prepare(BlockingReport rep)
    {
        return new PreparedBlockingReport(rep, PrepareProc(rep.Blocked), PrepareProc(rep.Blocking));
    }

    private PreparedBlockingProcess PrepareProc(BlockingProcess p)
    {
        if (string.IsNullOrEmpty(p.InputBufRaw))
            return new PreparedBlockingProcess(p, null, null);
        var nq = QueryNormalizer.Normalize(p.InputBufRaw);
        // store normalized SQL by default; raw only when redaction is explicitly off
        string stored = options.Redaction == RedactionMode.Off ? p.InputBufRaw : nq.NormalizedSql;
        return new PreparedBlockingProcess(p with { InputBufFingerprint = nq.NormalizedHash }, nq, stored);
    }
```

Update the `FinishRun` call and the returned `IngestionResult` to pass the three new counters. Add `using SqlFerret.Core.Storage;` if not already present (it is).

`Program.cs` import summary — extend the line:

```csharp
            Console.WriteLine(
                $"run {result.RunId}: read={result.Read} mapped={result.Mapped} " +
                $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures} " +
                $"blocking={result.Blocking} deadlocks={result.Deadlocks} blockingParseFailures={result.BlockingParseFailures}");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter BlockingIngestionTests`
Expected: PASS. Then full: `dotnet test` — fix any call sites of `FinishRun`/`IngestionResult` constructor in existing tests (search: `new IngestionResult(`). Expected: all green (1 skipped).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): route blocking/deadlock events into storage with exhaustive counters"
```

---

### Task 6: BlockingQueries (DuckDB rollups) + integration test on real sample

**Files:**
- Create: `src/SqlFerret.Core/Analysis/BlockingResults.cs`
- Create: `src/SqlFerret.Core/Analysis/BlockingQueries.cs`
- Test: `tests/SqlFerret.Core.Tests/BlockingQueriesTests.cs`

**Interfaces:**
- Consumes: the `blocking_reports`/`blocking_processes` tables (Task 4), `DuckDBConnection`.
- Produces (records in `BlockingResults.cs`):
  `record BlockingOverview(long ReportCount, long DeadlockCount, DateTime? FirstAt, DateTime? LastAt)`;
  `record LocalityStat(string WaitResourceType, long Count, double Pct)`;
  `record ContentionStat(string Key, long Count)`;
  `record BlockingStat(string Fingerprint, string NormalizedSql, long Count)`;
  `record WaitTimeDist(long P50Us, long P95Us, long MaxUs)`;
  `record ChainStat(int? MonitorLoop, int Depth, int? HeadSpid, long EdgeCount)`;
  Methods on `class BlockingQueries(DuckDBConnection conn)`:
  `BlockingOverview Overview()`, `IReadOnlyList<LocalityStat> Locality()`, `IReadOnlyList<ContentionStat> TopObjects(int limit)`,
  `IReadOnlyList<BlockingStat> TopBlockers(int limit)`, `IReadOnlyList<BlockingStat> TopBlocked(int limit)`,
  `IReadOnlyList<ContentionStat> LockModes()`, `IReadOnlyList<ContentionStat> IsolationLevels()`,
  `WaitTimeDist WaitTimes()`, `IReadOnlyList<ChainStat> Chains()`,
  `IReadOnlyList<BlockingReport> SampleReports(string fingerprint, int limit)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingQueriesTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;

public class BlockingQueriesTests
{
    private static DuckDbProject Seed()
    {
        var db = DuckDbProject.Open(Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb"));
        long run = db.BeginRun("logs/", 1, 0, "masked");
        PreparedBlockingProcess P(int spid, WaitResourceType t, long? wus, string role, string fp) =>
            new(new BlockingProcess(spid, 0, "s", "X", t, t == WaitResourceType.Object ? 99 : null, null, wus, "S", "rc", 1, "app", "h", "l", "sql", fp), null, "sql");
        // two reports: both blocked on OBJECT (locality = Object dominant), blocker fingerprint fp_block
        for (int i = 0; i < 2; i++)
            db.InsertBlockingBatch(run, [ new PreparedBlockingReport(
                new BlockingReport(new DateTime(2026,2,24), 1, 5, default!, default!),
                P(200+i, WaitResourceType.Object, 5_000_000L, "blocked", "fp_blocked"),
                P(118, WaitResourceType.Other, null, "blocking", "fp_block")) ]);
        return db;
    }

    [Fact]
    public void Locality_and_top_blockers_aggregate()
    {
        using var db = Seed();
        var q = new BlockingQueries(db.Connection);
        Assert.Equal(2, q.Overview().ReportCount);
        var loc = q.Locality();
        Assert.Equal("Object", loc[0].WaitResourceType);   // dominant blocked wait-resource type
        Assert.Equal(2, loc[0].Count);
        var blockers = q.TopBlockers(10);
        Assert.Equal("fp_block", blockers[0].Fingerprint);
        Assert.Equal(2, blockers[0].Count);
        Assert.Equal(5_000_000L, q.WaitTimes().MaxUs);
    }
}
```

NOTE: the test seeds processes directly; `BlockingReport.Blocked/Blocking` in the row are unused by the queries (queries read `blocking_processes`), so `default!` is acceptable in the seed.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingQueriesTests`
Expected: FAIL — `BlockingQueries` does not exist.

- [ ] **Step 3: Write the result records and queries**

```csharp
// src/SqlFerret.Core/Analysis/BlockingResults.cs
namespace SqlFerret.Core.Analysis;

public record BlockingOverview(long ReportCount, long DeadlockCount, DateTime? FirstAt, DateTime? LastAt);
public record LocalityStat(string WaitResourceType, long Count, double Pct);
public record ContentionStat(string Key, long Count);
public record BlockingStat(string Fingerprint, string NormalizedSql, long Count);
public record WaitTimeDist(long P50Us, long P95Us, long MaxUs);
public record ChainStat(int? MonitorLoop, int Depth, int? HeadSpid, long EdgeCount);
```

```csharp
// src/SqlFerret.Core/Analysis/BlockingQueries.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Analysis;

public class BlockingQueries(DuckDBConnection conn)
{
    public BlockingOverview Overview()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT (SELECT count(*) FROM blocking_reports),
                 (SELECT count(*) FROM deadlock_reports),
                 (SELECT min(captured_at) FROM blocking_reports),
                 (SELECT max(captured_at) FROM blocking_reports)
          """;
        using var r = c.ExecuteReader(); r.Read();
        return new BlockingOverview(r.GetInt64(0), r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3));
    }

    public IReadOnlyList<LocalityStat> Locality()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT wait_resource_type, count(*) AS cnt,
                 100.0 * count(*) / NULLIF(sum(count(*)) OVER (), 0) AS pct
          FROM blocking_processes WHERE role='blocked'
          GROUP BY wait_resource_type ORDER BY cnt DESC
          """;
        using var r = c.ExecuteReader();
        var list = new List<LocalityStat>();
        while (r.Read()) list.Add(new LocalityStat(r.GetString(0), r.GetInt64(1), r.GetDouble(2)));
        return list;
    }

    public IReadOnlyList<ContentionStat> TopObjects(int limit)
        => CountBy($"SELECT CAST(object_id AS TEXT), count(*) FROM blocking_processes WHERE role='blocked' AND object_id IS NOT NULL GROUP BY object_id ORDER BY 2 DESC LIMIT {limit}");

    public IReadOnlyList<ContentionStat> LockModes()
        => CountBy("SELECT COALESCE(lock_mode,'(none)'), count(*) FROM blocking_processes WHERE role='blocked' GROUP BY 1 ORDER BY 2 DESC");

    public IReadOnlyList<ContentionStat> IsolationLevels()
        => CountBy("SELECT COALESCE(isolation_level,'(none)'), count(*) FROM blocking_processes WHERE role='blocked' GROUP BY 1 ORDER BY 2 DESC");

    public IReadOnlyList<BlockingStat> TopBlockers(int limit) => Top("blocking", limit);
    public IReadOnlyList<BlockingStat> TopBlocked(int limit) => Top("blocked", limit);

    private IReadOnlyList<BlockingStat> Top(string role, int limit)
    {
        using var c = conn.CreateCommand();
        // role is a hard-coded literal ('blocked'|'blocking'), never user input; limit is an int
        c.CommandText = $"""
          SELECT bp.inputbuf_fingerprint,
                 COALESCE(nq.normalized_sql, bp.inputbuf, '(none)') AS sql,
                 count(*) AS cnt
          FROM blocking_processes bp
          LEFT JOIN normalized_queries nq ON nq.normalized_hash = bp.inputbuf_fingerprint
          WHERE bp.role = '{role}' AND bp.inputbuf_fingerprint IS NOT NULL
          GROUP BY bp.inputbuf_fingerprint, sql ORDER BY cnt DESC LIMIT {limit}
          """;
        using var r = c.ExecuteReader();
        var list = new List<BlockingStat>();
        while (r.Read()) list.Add(new BlockingStat(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    public WaitTimeDist WaitTimes()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT COALESCE(quantile_cont(wait_time_us, 0.5),0),
                 COALESCE(quantile_cont(wait_time_us, 0.95),0),
                 COALESCE(max(wait_time_us),0)
          FROM blocking_processes WHERE role='blocked' AND wait_time_us IS NOT NULL
          """;
        using var r = c.ExecuteReader(); r.Read();
        return new WaitTimeDist((long)r.GetDouble(0), (long)r.GetDouble(1), r.GetInt64(2));
    }

    public IReadOnlyList<ChainStat> Chains()
    {
        // Edges: within a report, blocked.spid waits on blocking.spid. Head = a blocking spid that is
        // never itself blocked in the same monitor_loop. Depth via recursive CTE over (loop, from->to).
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH edges AS (
            SELECT r.monitor_loop AS loop, b.spid AS blocked_spid, k.spid AS blocking_spid
            FROM blocking_reports r
            JOIN blocking_processes b ON b.report_id=r.report_id AND b.role='blocked'
            JOIN blocking_processes k ON k.report_id=r.report_id AND k.role='blocking'
          ),
          heads AS (
            SELECT DISTINCT loop, blocking_spid AS spid FROM edges e
            WHERE NOT EXISTS (SELECT 1 FROM edges x WHERE x.loop=e.loop AND x.blocked_spid=e.blocking_spid)
          ),
          walk AS (
            SELECT loop, spid AS head, spid AS cur, 1 AS depth FROM heads
            UNION ALL
            SELECT w.loop, w.head, e.blocked_spid, w.depth+1
            FROM walk w JOIN edges e ON e.loop=w.loop AND e.blocking_spid=w.cur
          )
          SELECT loop, max(depth) AS depth, head, (SELECT count(*) FROM edges e WHERE e.loop=walk.loop) AS edges
          FROM walk GROUP BY loop, head ORDER BY depth DESC
          """;
        using var r = c.ExecuteReader();
        var list = new List<ChainStat>();
        while (r.Read())
            list.Add(new ChainStat(r.IsDBNull(0) ? null : r.GetInt32(0), (int)r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt64(3)));
        return list;
    }

    public IReadOnlyList<BlockingReport> SampleReports(string fingerprint, int limit)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT r.report_id, r.captured_at, r.monitor_loop, r.database_id
          FROM blocking_reports r
          JOIN blocking_processes bp ON bp.report_id=r.report_id AND bp.role='blocking'
          WHERE bp.inputbuf_fingerprint = $fp ORDER BY r.captured_at LIMIT $l
          """;
        Add(c, "$fp", fingerprint); Add(c, "$l", limit);
        var ids = new List<(long id, DateTime ts, int? loop, int? db)>();
        using (var r = c.ExecuteReader())
            while (r.Read()) ids.Add((r.GetInt64(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3)));
        var list = new List<BlockingReport>();
        foreach (var (id, ts, loop, db) in ids)
            list.Add(new BlockingReport(ts, loop, db, LoadProc(id, "blocked"), LoadProc(id, "blocking")));
        return list;
    }

    private BlockingProcess LoadProc(long reportId, string role)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT spid, ecid, status, wait_resource_raw, wait_resource_type, object_id, hobt_id,
                 wait_time_us, lock_mode, isolation_level, trancount, client_app, host_name, login_name,
                 inputbuf, inputbuf_fingerprint
          FROM blocking_processes WHERE report_id=$id AND role=$role
          """;
        Add(c, "$id", reportId); Add(c, "$role", role);
        using var r = c.ExecuteReader();
        if (!r.Read()) return new BlockingProcess(null,null,null,null,WaitResourceType.Other,null,null,null,null,null,null,null,null,null,null,null);
        return new BlockingProcess(
            r.IsDBNull(0)?null:r.GetInt32(0), r.IsDBNull(1)?null:r.GetInt32(1), r.IsDBNull(2)?null:r.GetString(2),
            r.IsDBNull(3)?null:r.GetString(3), Enum.Parse<WaitResourceType>(r.GetString(4)),
            r.IsDBNull(5)?null:r.GetInt64(5), r.IsDBNull(6)?null:r.GetInt64(6), r.IsDBNull(7)?null:r.GetInt64(7),
            r.IsDBNull(8)?null:r.GetString(8), r.IsDBNull(9)?null:r.GetString(9), r.IsDBNull(10)?null:r.GetInt32(10),
            r.IsDBNull(11)?null:r.GetString(11), r.IsDBNull(12)?null:r.GetString(12), r.IsDBNull(13)?null:r.GetString(13),
            r.IsDBNull(14)?null:r.GetString(14), r.IsDBNull(15)?null:r.GetString(15));
    }

    private IReadOnlyList<ContentionStat> CountBy(string sql)
    {
        using var c = conn.CreateCommand(); c.CommandText = sql;
        using var r = c.ExecuteReader();
        var list = new List<ContentionStat>();
        while (r.Read()) list.Add(new ContentionStat(r.IsDBNull(0) ? "(none)" : r.GetString(0), r.GetInt64(1)));
        return list;
    }

    private static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter(); p.ParameterName = name.TrimStart('$'); p.Value = value ?? DBNull.Value; c.Parameters.Add(p);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter BlockingQueriesTests`
Expected: PASS.

- [ ] **Step 5: Add a SkippableFact integration test on the real sample**

```csharp
// add to tests/SqlFerret.Core.Tests/BlockingQueriesTests.cs
    [SkippableFact]
    public void Real_blocking_xel_ingests_and_aggregates()
    {
        var sample = Directory.Exists("sample")
            ? Directory.GetFiles("sample", "*.xel").FirstOrDefault(f => f.Contains("block", StringComparison.OrdinalIgnoreCase))
            : null;
        Skip.If(sample is null, "no blocking sample .xel present");

        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = SqlFerret.Core.Ingestion.ImportRunner.Run(db,
                new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Masked, []), sample!);
            Assert.True(res.Blocking + res.BlockingParseFailures > 0, "expected blocking events in the sample");
            var q = new BlockingQueries(db.Connection);
            Assert.NotEmpty(q.Locality());   // proves the field name 'blocked_process' is correct (spec §9)
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

Run: `dotnet test --filter BlockingQueriesTests`. With a real blocking `.xel` in `sample/`, this confirms the `blocked_process` field name (spec §9). If it ingests 0 blocking but >0 parse failures, fix the field name in `EventMapper.ExtractBlockingXml` (try `"blocked_process"` vs the actual XELite field) and re-run. Skips cleanly when absent.

- [ ] **Step 6: Commit**

```bash
git add src/SqlFerret.Core/Analysis/BlockingResults.cs src/SqlFerret.Core/Analysis/BlockingQueries.cs tests/SqlFerret.Core.Tests/BlockingQueriesTests.cs
git commit -m "feat(core): blocking rollups (locality, top blockers, chains) + real-sample integration test"
```

---

### Task 7: BlockingDigest assembler

**Files:**
- Create: `src/SqlFerret.Core/Analysis/BlockingDigest.cs`
- Modify: `src/SqlFerret.Core/Analysis/BlockingResults.cs` (add `BlockingDigestResult` + `BlockingDigestEnvelope`)
- Test: `tests/SqlFerret.Core.Tests/BlockingDigestTests.cs`

**Interfaces:**
- Consumes: `BlockingQueries` (Task 6) and its result records.
- Produces:
  `record BlockingDigestResult(BlockingOverview Overview, IReadOnlyList<LocalityStat> Locality, IReadOnlyList<ContentionStat> TopObjects, IReadOnlyList<BlockingStat> TopBlockers, IReadOnlyList<BlockingStat> TopBlocked, IReadOnlyList<ContentionStat> LockModes, IReadOnlyList<ContentionStat> IsolationLevels, WaitTimeDist WaitTimes, IReadOnlyList<ChainStat> Chains, IReadOnlyList<BlockingSample> Samples)`;
  `record BlockingSample(string Fingerprint, IReadOnlyList<BlockingReport> Reports)`;
  `record BlockingDigestEnvelope(int SchemaVersion, BlockingDigestResult Digest)`;
  `class BlockingDigest(DuckDBConnection conn) { BlockingDigestResult Build(int samplesPerPattern = 5, int topK = 10); }`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/BlockingDigestTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;

public class BlockingDigestTests
{
    [Fact]
    public void Build_assembles_rollups_and_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked");
            BlockingProcess B(int spid, string role, string fp) =>
                new(spid,0,"s","OBJECT: 5:99:0",WaitResourceType.Object,99,null,5_000_000L,"S","rc",1,"app","h","l","sql",fp);
            db.InsertBlockingBatch(run, [ new PreparedBlockingReport(
                new BlockingReport(new DateTime(2026,2,24),1,5, default!, default!),
                new PreparedBlockingProcess(B(201,"blocked","fp_bd"), null, "exec X"),
                new PreparedBlockingProcess(B(118,"blocking","fp_bk"), null, "update Y")) ]);

            var digest = new BlockingDigest(db.Connection).Build(samplesPerPattern: 3, topK: 5);
            Assert.Equal(1, digest.Overview.ReportCount);
            Assert.Equal("Object", digest.Locality[0].WaitResourceType);
            Assert.Equal("fp_bk", digest.TopBlockers[0].Fingerprint);
            Assert.Single(digest.Samples);                       // one dominant blocker pattern
            Assert.Equal("fp_bk", digest.Samples[0].Fingerprint);
            Assert.Single(digest.Samples[0].Reports);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingDigestTests`
Expected: FAIL — `BlockingDigest` does not exist.

- [ ] **Step 3: Add the records and the assembler**

Append to `BlockingResults.cs`:

```csharp
public record BlockingSample(string Fingerprint, IReadOnlyList<SqlFerret.Core.Model.BlockingReport> Reports);

public record BlockingDigestResult(
    BlockingOverview Overview, IReadOnlyList<LocalityStat> Locality,
    IReadOnlyList<ContentionStat> TopObjects, IReadOnlyList<BlockingStat> TopBlockers,
    IReadOnlyList<BlockingStat> TopBlocked, IReadOnlyList<ContentionStat> LockModes,
    IReadOnlyList<ContentionStat> IsolationLevels, WaitTimeDist WaitTimes,
    IReadOnlyList<ChainStat> Chains, IReadOnlyList<BlockingSample> Samples);

public record BlockingDigestEnvelope(int SchemaVersion, BlockingDigestResult Digest);
```

```csharp
// src/SqlFerret.Core/Analysis/BlockingDigest.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>The reusable digest engine. CLI exposes it now; the Spec 2 MCP server reuses it verbatim.
/// Pure data out (BlockingDigestResult) — no formatting.</summary>
public class BlockingDigest(DuckDBConnection conn)
{
    public const int SchemaVersion = 1;

    public BlockingDigestResult Build(int samplesPerPattern = 5, int topK = 10)
    {
        var q = new BlockingQueries(conn);
        var topBlockers = q.TopBlockers(topK);
        var samples = topBlockers
            .Select(b => new BlockingSample(b.Fingerprint, q.SampleReports(b.Fingerprint, samplesPerPattern)))
            .Where(s => s.Reports.Count > 0)
            .ToList();
        return new BlockingDigestResult(
            q.Overview(), q.Locality(), q.TopObjects(topK), topBlockers, q.TopBlocked(topK),
            q.LockModes(), q.IsolationLevels(), q.WaitTimes(), q.Chains(), samples);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter BlockingDigestTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlFerret.Core/Analysis/BlockingDigest.cs src/SqlFerret.Core/Analysis/BlockingResults.cs tests/SqlFerret.Core.Tests/BlockingDigestTests.cs
git commit -m "feat(core): BlockingDigest assembler (rollups + representative samples)"
```

---

### Task 8: CLI `export-blocking` (JSON + markdown + --full)

**Files:**
- Create: `src/SqlFerret.Cli/BlockingDigestMarkdown.cs`
- Modify: `src/SqlFerret.Cli/Program.cs`
- Test: `tests/SqlFerret.Core.Tests/BlockingDigestTests.cs` (add a markdown + JSON render test — the renderer is static and host-agnostic to test, but lives in Cli; reference the Cli project from tests is already set for CliSmokeTests).

**Interfaces:**
- Consumes: `BlockingDigest.Build` + `BlockingDigestResult`/`BlockingDigestEnvelope` (Task 7), `DuckDbProject` (existing).
- Produces: CLI command `export-blocking`; `static class BlockingDigestMarkdown { static string Render(BlockingDigestResult d); }`.

- [ ] **Step 1: Write the failing test**

```csharp
// add to tests/SqlFerret.Core.Tests/BlockingDigestTests.cs
    [Fact]
    public void Markdown_render_includes_locality_and_top_blockers()
    {
        var digest = new BlockingDigestResult(
            new BlockingOverview(2, 0, new DateTime(2026,2,24), new DateTime(2026,2,24)),
            [new LocalityStat("Object", 2, 100.0)], [new ContentionStat("99", 2)],
            [new BlockingStat("fp_bk", "UPDATE dbo.Y SET ...", 2)], [],
            [new ContentionStat("S", 2)], [new ContentionStat("read committed (2)", 2)],
            new WaitTimeDist(5_000_000, 5_000_000, 5_000_000), [], []);
        var md = BlockingDigestMarkdown.Render(digest);
        Assert.Contains("Object", md);
        Assert.Contains("UPDATE dbo.Y", md);
        Assert.Contains("100", md);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockingDigestTests`
Expected: FAIL — `BlockingDigestMarkdown` does not exist.

- [ ] **Step 3: Write the markdown renderer (host formatting)**

```csharp
// src/SqlFerret.Cli/BlockingDigestMarkdown.cs
using System.Text;
using SqlFerret.Core.Analysis;

namespace SqlFerret.Cli;

public static class BlockingDigestMarkdown
{
    public static string Render(BlockingDigestResult d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Blocking digest").AppendLine();
        sb.AppendLine($"- reports: {d.Overview.ReportCount}  deadlocks: {d.Overview.DeadlockCount}");
        sb.AppendLine($"- window: {d.Overview.FirstAt:u} → {d.Overview.LastAt:u}");
        sb.AppendLine($"- wait (us): p50={d.WaitTimes.P50Us} p95={d.WaitTimes.P95Us} max={d.WaitTimes.MaxUs}").AppendLine();
        sb.AppendLine("## Locality (blocked wait-resource type)");
        foreach (var l in d.Locality) sb.AppendLine($"- {l.WaitResourceType}: {l.Count} ({l.Pct:0.0}%)");
        sb.AppendLine().AppendLine("## Top blockers");
        foreach (var b in d.TopBlockers) sb.AppendLine($"- [{b.Count}] `{Trim(b.NormalizedSql)}` ({b.Fingerprint})");
        sb.AppendLine().AppendLine("## Top blocked");
        foreach (var b in d.TopBlocked) sb.AppendLine($"- [{b.Count}] `{Trim(b.NormalizedSql)}`");
        sb.AppendLine().AppendLine("## Chains");
        foreach (var ch in d.Chains) sb.AppendLine($"- loop {ch.MonitorLoop}: depth={ch.Depth} head_spid={ch.HeadSpid} edges={ch.EdgeCount}");
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length <= 100 ? s : s[..97] + "...";
}
```

- [ ] **Step 4: Add the CLI command to `Program.cs`**

Add a `case "export-blocking":` to the `switch (args[0])`:

```csharp
    case "export-blocking":
        {
            var project = Arg("--project", "workload.duckdb");
            var format = Arg("--format", "both");           // json | md | both
            var samples = int.TryParse(Arg("--samples", "5"), out var sv) ? sv : 5;
            var full = Array.IndexOf(args, "--full") >= 0;
            var outPath = Arg("--out", "");
            if (outPath.Length > 0 && (outPath.Contains("..") || Path.IsPathRooted(outPath) && outPath.Contains("..")))
            { Console.Error.WriteLine("export-blocking: invalid --out path"); return 1; }

            using var db = DuckDbProject.Open(project);

            if (full)
            {
                // NDJSON dump of every report (bounded by file, not context)
                var q = new SqlFerret.Core.Analysis.BlockingQueries(db.Connection);
                using var w = outPath.Length > 0 ? new StreamWriter(outPath) : null;
                TextWriter o = w ?? Console.Out;
                foreach (var b in q.TopBlockers(int.MaxValue))
                    foreach (var rep in q.SampleReports(b.Fingerprint, int.MaxValue))
                        o.WriteLine(System.Text.Json.JsonSerializer.Serialize(rep));
                return 0;
            }

            var digest = new SqlFerret.Core.Analysis.BlockingDigest(db.Connection).Build(samplesPerPattern: samples);
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

            string json = System.Text.Json.JsonSerializer.Serialize(
                new SqlFerret.Core.Analysis.BlockingDigestEnvelope(
                    SqlFerret.Core.Analysis.BlockingDigest.SchemaVersion, digest), jsonOpts);
            string md = BlockingDigestMarkdown.Render(digest);

            string payload = format switch
            {
                "json" => json,
                "md" => md,
                _ => md + "\n\n```json\n" + json + "\n```\n"
            };
            if (outPath.Length > 0) File.WriteAllText(outPath, payload); else Console.WriteLine(payload);
            return 0;
        }
```

Update the usage line at the top of `Program.cs` to mention `export-blocking`.

- [ ] **Step 5: Run tests + a manual smoke**

Run: `dotnet test --filter BlockingDigestTests` → PASS.
Run full: `dotnet test` → all green (1 skipped).
Manual (if a blocking sample exists):
```bash
dotnet run --project src/SqlFerret.Cli -- import sample/<blocking>.xel --project /tmp/bl.duckdb
dotnet run --project src/SqlFerret.Cli -- export-blocking --project /tmp/bl.duckdb --format md
```
Expected: a markdown digest with locality + top blockers.

- [ ] **Step 6: Commit**

```bash
git add src/SqlFerret.Cli/BlockingDigestMarkdown.cs src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/BlockingDigestTests.cs
git commit -m "feat(cli): export-blocking command (digest JSON + markdown, --full NDJSON)"
```

---

## Self-Review

**Spec coverage:** §3.1 model → Task 1; §3.2 parsers → Tasks 1–2; §3.3 routing → Tasks 3,5; §3.4 counters → Task 5; §3.5 storage → Task 4; §3.6 BlockingQueries + BlockingDigest + `--full` → Tasks 6,7,8; §3.7 CLI → Task 8; §4 JSON schema/envelope → Tasks 7,8; §5 redaction (normalize-by-default, raw only on `off`) → Task 5; §6 error handling (parse-failure counter, `--out` traversal, `--full` streaming) → Tasks 5,8; §7 testing (unit fixtures + SkippableFact on real sample) → all tasks + Task 6 Step 5; §8 invariants → Global Constraints. Deadlocks light (ingest + count + redacted graph) → Tasks 2,5. **No uncovered requirement.**

**Placeholder scan:** No "TBD"/"handle edge cases". Two NOTEs are deliberate implementation cautions with the exact fix location (Task 3 field name; Task 4 `$ts` carrier) — not deferred work.

**Type consistency:** `BlockingProcess`/`BlockingReport`/`DeadlockReport` (Task 1) used unchanged in Tasks 2,4,6,7. `PreparedBlockingReport`/`PreparedBlockingProcess` (Task 4) consumed in Tasks 5,6,7 tests. `BlockingQueries` method names (`Overview/Locality/TopBlockers/TopBlocked/LockModes/IsolationLevels/WaitTimes/Chains/SampleReports`) match between Task 6 definition and Task 7 use. `BlockingDigestResult`/`BlockingDigestEnvelope`/`BlockingDigest.SchemaVersion` consistent across Tasks 7–8. `IngestionResult` 9-arg shape (Task 5) — existing `new IngestionResult(...)` call sites must be updated (called out in Task 5 Step 4).
