# SQLFerret TUI (MVP vertical slice) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a keyboard-first Terminal.Gui v2 host over the already-merged `SqlFerret.Core` engine that proves the path *ingest → analyze → drill → copy*: a shell, an Import view with live progress, a Top Slow grid, and a signature drill-down (occurrences, parameter-impact, build-for-SSMS clipboard).

**Architecture:** A second thin host over Core (like `SqlFerret.Cli`). Each view is a **thin presenter** (plain class, no Terminal.Gui dependency — the headless test seam) plus a Terminal.Gui code-behind `View` that renders and wires keys. The TUI holds **no analysis logic**: every grid is a parametrized `WorkloadQueries` call; aggregation stays in DuckDB SQL. Two small backward-compatible Core additions support it: ingestion progress (`IProgress<IngestionProgress>`) and occurrence reconstruction (`WorkloadQueries.LoadExecution`).

**Tech Stack:** C# / .NET 10, Terminal.Gui **2.4.6** (TUI host), the existing Core (DuckDB.NET, XELite, ScriptDom, Microsoft.Data.SqlClient), xUnit + Xunit.SkippableFact.

## Global Constraints

- **Target framework:** `net10.0` for the new projects. **Terminal.Gui pinned to 2.4.6.**
- **KISS (spec §2):** no DI container, no `INotifyPropertyChanged`/MVVM, no repository/onion layering. Plain presenters constructed from concrete Core types. The only new interface is `IClipboard` (two genuine impls). The TUI holds no analysis logic — every grid is a `WorkloadQueries` call; durations render via `DisplayFormat` + config `durationUnit`.
- **Modern C# 14 / .NET 10 baseline:** primary constructors for presenters/services; collection expressions `[]`; `record` for multi-field values; `required`/`init`; switch/`is` pattern matching where it reads cleanly.
- **Durations stay microseconds in Core;** formatting happens only in the host via `DisplayFormat.Duration(microseconds, unit)`.
- **Secrets in `.env`** via `${ENV}`; the TUI loads `DotEnv.Load` + `SqlFerretConfig.Load` at startup, same as the CLI. Never write secrets to committed files.
- **Two Core additions only,** both backward-compatible and tested in `SqlFerret.Core.Tests`: `IngestionProgress` + optional `IProgress<IngestionProgress>` on `IngestionService.Ingest`; `WorkloadQueries.LoadExecution(long) → ExecutionEvent`.
- **Real `.xel` tests gate on the gitignored `sample/` folder** via `[SkippableFact]` (skip when absent); never commit `sample/` data or `.duckdb` files.
- **Threading:** the only background work is ingestion (`Task.Run`); UI updates marshal back via `Application.Invoke`. Presenters never reference Terminal.Gui types.
- **Solution layout:** add `src/SqlFerret.Tui` (host) and `tests/SqlFerret.Tui.Tests` (xUnit).

## Existing Core interfaces this plan consumes (verbatim — do not redefine)

```csharp
// Analysis
public record QueryStat(string NormalizedHash, string StatementKind, string? PrimaryTable,
    string NormalizedSql, long Count, double AvgDurationUs, long P95DurationUs,
    long MaxDurationUs, long TotalDurationUs);
public record Occurrence(long ExecutionId, DateTime CapturedAt, string? Database, string? Login,
    long? DurationUs, string SqlTextRaw);
public record ParamImpact(string ValueText, long Count, double AvgDurationUs, long P95DurationUs, long MaxDurationUs);
public class WorkloadQueries(DuckDBConnection conn) {
    IReadOnlyList<QueryStat> TopSlow(int limit, string sortColumn, IEnumerable<FilterRule> viewFilters); // sortColumn ∈ total_duration_us|p95_duration_us|max_duration_us|avg_duration_us
    IReadOnlyList<Occurrence> Occurrences(string normalizedHash, int limit);
    IReadOnlyList<ParamImpact> ParameterImpact(string normalizedHash, string paramName);
}
// Ingestion
public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures);
public record IngestionOptions(RedactionMode Redaction, IReadOnlyList<FilterRule> Filters, int BatchSize = 5000);
public class IngestionService(DuckDbProject project, IngestionOptions options) {
    IngestionResult Ingest(string sourcePath, IEnumerable<(IXeEventData ev, string fileName, long offset)> events);
}
public static class XelSource { static (IReadOnlyList<string> files, long bytesTotal) Resolve(string path); }
public class XelReader { IEnumerable<(IXeEventData ev, string fileName, long offset)> Read(IReadOnlyList<string> files); }
public enum RedactionMode { Off, Hash, Masked, Full }
// Replay / model
public static class ReplayBuilder { static ReplayScript Build(ExecutionEvent ev); }
public record ReplayScript(string Sql, ReplayKind Kind, double Confidence);
public enum ReplayKind { RawBatch, ExecProc, SpExecuteSql }
public record RawParameter(int Ordinal, string? Name, ParameterSourceKind SourceKind, string? SqlTypeGuess, string ValueText, double ParseConfidence);
public enum ParameterSourceKind { RpcParameter, Literal, OutputParameter }
// ExecutionEvent: record with required EventName, SqlTextRaw, XeFileName; init props incl.
//   EventClass EventClass; string? ObjectName; IReadOnlyList<RawParameter> Parameters (default []); long? DurationUs; ...
// Storage / config
public sealed class DuckDbProject : IDisposable { static DuckDbProject Open(string path); DuckDBConnection Connection { get; } }
public static class DisplayFormat { static string Duration(long microseconds, string unit); } // unit ∈ us|ms|s
public class SqlFerretConfig { static SqlFerretConfig Load(string path); string DurationUnit { get; } string RedactionPolicy { get; } }
public static class DotEnv { static void Load(string path); }
public class UiState { record ViewLayout(string[] Columns, string Sort);
    List<FilterRule> Filters {get;set;} Dictionary<string,ViewLayout> Views {get;set;}
    static UiState Load(string path); void Save(string path); }
```

---

## File Structure

```
src/SqlFerret.Tui/
  SqlFerret.Tui.csproj         net10.0, Terminal.Gui 2.4.6, ref SqlFerret.Core
  Program.cs                   bootstrap: DotEnv+config+UiState → DuckDbProject.Open → MainWindow → Run
  Shell/Keys.cs                keybinding constants
  Shell/MainWindow.cs          view-rail + content host + title + status bar
  Presenters/TopSlowPresenter.cs
  Presenters/DrillDownPresenter.cs
  Presenters/ImportPresenter.cs
  Views/TopSlowView.cs
  Views/DrillDownView.cs
  Views/ImportView.cs
  Views/ColumnChooserDialog.cs
  Clipboard/IClipboard.cs
  Clipboard/NativeClipboard.cs
  Clipboard/FileFallbackClipboard.cs
tests/SqlFerret.Tui.Tests/
  SqlFerret.Tui.Tests.csproj   net10.0, ref Core + Tui, xunit + Xunit.SkippableFact
  TestProject.cs               helper: seed a temp DuckDbProject with known rows
  TopSlowPresenterTests.cs
  DrillDownPresenterTests.cs
  ImportPresenterTests.cs
  ClipboardTests.cs
  ShellSmokeTests.cs
```

Core files modified (Tasks 2-3): `src/SqlFerret.Core/Ingestion/IngestionProgress.cs` (new), `IngestionService.cs` (add param), `src/SqlFerret.Core/Analysis/WorkloadQueries.cs` (add `LoadExecution`). Core tests added in `tests/SqlFerret.Core.Tests/`.

---

## Task 1: Scaffold SqlFerret.Tui + Tui.Tests; app boots to an empty shell

**Files:**
- Create: `src/SqlFerret.Tui/SqlFerret.Tui.csproj`, `src/SqlFerret.Tui/Program.cs`, `tests/SqlFerret.Tui.Tests/SqlFerret.Tui.Tests.csproj`, `tests/SqlFerret.Tui.Tests/SmokePlaceholderTests.cs`
- Modify: `sqlferret.sln`

**Interfaces:**
- Produces: two building `net10.0` projects; Terminal.Gui 2.4.6 referenced; a green placeholder test. `Program.cs` opens a window titled `SQLFerret` and quits on `q`/Ctrl-Q.

- [ ] **Step 1: Create projects, references, packages**

```bash
cd /home/rudi/Sources/Repos/sqlferret
dotnet new console -n SqlFerret.Tui -o src/SqlFerret.Tui -f net10.0
dotnet new xunit   -n SqlFerret.Tui.Tests -o tests/SqlFerret.Tui.Tests -f net10.0
rm -f tests/SqlFerret.Tui.Tests/UnitTest1.cs
dotnet sln add src/SqlFerret.Tui tests/SqlFerret.Tui.Tests
dotnet add src/SqlFerret.Tui reference src/SqlFerret.Core
dotnet add tests/SqlFerret.Tui.Tests reference src/SqlFerret.Core
dotnet add tests/SqlFerret.Tui.Tests reference src/SqlFerret.Tui
dotnet add src/SqlFerret.Tui package Terminal.Gui --version 2.4.6
dotnet add tests/SqlFerret.Tui.Tests package Xunit.SkippableFact
```

Ensure `src/SqlFerret.Tui/SqlFerret.Tui.csproj` `<PropertyGroup>` has `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`.

- [ ] **Step 2: Minimal bootstrap Program.cs**

> **Implementer note:** Terminal.Gui 2.4.6 API. Verify class/method names against the installed package (`Application.Init/Run/Shutdown`, `Window`, `Key`). If a member differs, adapt while preserving the behavior "open a titled window, quit on q / Ctrl-Q". Run the app once manually to confirm it opens and quits.

```csharp
// src/SqlFerret.Tui/Program.cs
using Terminal.Gui;

// Usage: SqlFerret.Tui <project.duckdb>   (project path required; mirrors the CLI's --project)
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: SqlFerret.Tui <project.duckdb>");
    return 1;
}

Application.Init();
try
{
    var win = new Window { Title = "SQLFerret" };
    win.KeyDown += (_, key) =>
    {
        if (key == Key.Q || key == Key.Q.WithCtrl) { Application.RequestStop(); key.Handled = true; }
    };
    Application.Run(win);
    win.Dispose();
}
finally { Application.Shutdown(); }
return 0;
```

- [ ] **Step 3: Placeholder test**

```csharp
// tests/SqlFerret.Tui.Tests/SmokePlaceholderTests.cs
using Xunit;
public class SmokePlaceholderTests
{
    [Fact] public void Tui_project_is_wired() => Assert.True(typeof(Terminal.Gui.Window) is not null);
}
```

- [ ] **Step 4: Build + test**

Run: `dotnet build` then `dotnet test --filter SmokePlaceholderTests`
Expected: build 0 errors; 1 test passes. (Optionally `dotnet run --project src/SqlFerret.Tui -- /tmp/x.duckdb` to eyeball the window, then `q`.)

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "chore(tui): scaffold SqlFerret.Tui + Tui.Tests (Terminal.Gui 2.4.6)"
```

---

## Task 2: Core — IngestionProgress + IProgress on IngestionService.Ingest

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/IngestionProgress.cs`
- Modify: `src/SqlFerret.Core/Ingestion/IngestionService.cs`
- Test: `tests/SqlFerret.Core.Tests/IngestionProgressTests.cs`

**Interfaces:**
- Produces: `record IngestionProgress(long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures, string CurrentFile)`; new overload `IngestionResult Ingest(string sourcePath, IEnumerable<(IXeEventData ev, string fileName, long offset)> events, IProgress<IngestionProgress>? progress)`. Reports after each batch flush and once before finishing. Existing 2-arg behavior unchanged (optional param).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/IngestionProgressTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Core.Filtering;
using Xunit;

public class IngestionProgressTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    private static (IXeEventData, string, long) Batch(int i) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
            new Dictionary<string, object?> { ["batch_text"] = $"SELECT {i}", ["duration"] = (long)i },
            new Dictionary<string, object?>()), "s_0.xel", i);

    [Fact]
    public void Ingest_reports_progress_and_final_matches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, [], BatchSize: 2));
            var seen = new List<IngestionProgress>();
            var progress = new Progress<IngestionProgress>(seen.Add);

            // Progress<T> posts asynchronously; collect synchronously instead for a deterministic test:
            var captured = new List<IngestionProgress>();
            IProgress<IngestionProgress> sync = new SyncProgress(captured.Add);

            var result = svc.Ingest("logs/", Enumerable.Range(1, 5).Select(Batch), sync);

            Assert.Equal(5, result.Read);
            Assert.Equal(5, result.Mapped);
            Assert.NotEmpty(captured);                       // at least one progress tick
            Assert.Equal(result.Read, captured[^1].Read);    // last tick equals final counters
            Assert.Equal("s_0.xel", captured[^1].CurrentFile);
            Assert.True(captured.Select(p => p.Read).SequenceEqual(captured.Select(p => p.Read).OrderBy(x => x))); // monotonic
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class SyncProgress(Action<IngestionProgress> onReport) : IProgress<IngestionProgress>
    {
        public void Report(IngestionProgress value) => onReport(value);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter IngestionProgressTests`
Expected: FAIL (no 3-arg `Ingest`, `IngestionProgress` undefined).

- [ ] **Step 3: Add IngestionProgress and the overload**

```csharp
// src/SqlFerret.Core/Ingestion/IngestionProgress.cs
namespace SqlFerret.Core.Ingestion;

public record IngestionProgress(
    long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures, string CurrentFile);
```

In `src/SqlFerret.Core/Ingestion/IngestionService.cs`, change the `Ingest` signature and add reporting. Keep all existing logic; only the signature line, a `currentFile` tracker, and two `progress?.Report(...)` calls are new:

```csharp
public IngestionResult Ingest(string sourcePath,
    IEnumerable<(IXeEventData ev, string fileName, long offset)> events,
    IProgress<IngestionProgress>? progress = null)
{
    long runId = project.BeginRun(sourcePath, filesCount: 1, bytesTotal: 0,
        redactionPolicy: options.Redaction.ToString().ToLowerInvariant());

    long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
    string currentFile = "";
    var buffer = new List<PreparedRow>(options.BatchSize);

    foreach (var (ev, fileName, offset) in events)
    {
        currentFile = fileName;
        read++;
        var e = EventMapper.Map(ev, fileName, offset);
        if (e.EventClass == EventClass.Unknown || string.IsNullOrEmpty(e.SqlTextRaw)) { unmapped++; continue; }
        if (!_ingestKeep(e)) { cleaned++; continue; }

        var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
        if (nq.TokenizeFailed) tokenizeFailures++;

        buffer.Add(new PreparedRow(e, nq, RedactParams(e)));
        mapped++;

        if (buffer.Count >= options.BatchSize)
        {
            project.InsertBatch(runId, buffer); buffer.Clear();
            progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
        }
    }
    if (buffer.Count > 0) project.InsertBatch(runId, buffer);

    progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
    project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures);
    return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures);
}
```

> Note: the existing 2-arg call sites (CLI, prior tests) still compile because `progress` is optional.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter IngestionProgressTests` then full `dotnet test`
Expected: PASS; no regressions.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): ingestion progress callback (IProgress<IngestionProgress>)"
```

---

## Task 3: Core — WorkloadQueries.LoadExecution(executionId) → ExecutionEvent

**Files:**
- Modify: `src/SqlFerret.Core/Analysis/WorkloadQueries.cs`
- Test: `tests/SqlFerret.Core.Tests/LoadExecutionTests.cs`

**Interfaces:**
- Produces: `ExecutionEvent WorkloadQueries.LoadExecution(long executionId)` — reads one `executions` row + its `execution_parameters` (ordered by `ordinal`) and rebuilds an `ExecutionEvent` whose `EventClass`, `ObjectName`, `SqlTextRaw`, and `Parameters` are sufficient for `ReplayBuilder.Build`. `event_class` text is parsed case-insensitively to `EventClass`; `source_kind` text to `ParameterSourceKind`. Throws `InvalidOperationException` if the id is absent.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/LoadExecutionTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Replay;
using SqlFerret.Core.Storage;
using SqlFerret.Core.Filtering;
using Xunit;

public class LoadExecutionTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    [Fact]
    public void LoadExecution_rebuilds_event_for_replay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            // an rpc_completed event with object_name + a statement carrying params
            var ev = new FakeEvent("rpc_completed", new DateTime(2026, 1, 1),
                new Dictionary<string, object?> {
                    ["statement"] = "exec dbo.GetOrder @OrderId = 123",
                    ["object_name"] = "dbo.GetOrder", ["duration"] = 10L },
                new Dictionary<string, object?>());
            new IngestionService(db, new IngestionOptions(RedactionMode.Full, []))
                .Ingest("logs/", new[] { ((IXeEventData)ev, "s_0.xel", 0L) });

            long id;
            using (var cmd = db.Connection.CreateCommand())
            { cmd.CommandText = "SELECT execution_id FROM executions LIMIT 1"; id = Convert.ToInt64(cmd.ExecuteScalar()); }

            var loaded = new WorkloadQueries(db.Connection).LoadExecution(id);
            Assert.Equal(EventClass.RpcCall, loaded.EventClass);
            Assert.Equal("dbo.GetOrder", loaded.ObjectName);
            Assert.Single(loaded.Parameters);
            Assert.Equal("@OrderId", loaded.Parameters[0].Name);

            var replay = ReplayBuilder.Build(loaded);
            Assert.Equal(ReplayKind.ExecProc, replay.Kind);
            Assert.Equal("EXEC dbo.GetOrder @OrderId = 123;", replay.Sql);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter LoadExecutionTests`
Expected: FAIL (no `LoadExecution`).

- [ ] **Step 3: Implement LoadExecution**

Add to `WorkloadQueries` (uses the existing private `Add` helper). Reads the row, then the parameters:

```csharp
public ExecutionEvent LoadExecution(long executionId)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT event_name, event_class, object_name, database_name, login_name,
             client_hostname, client_app_name, session_id, captured_at, duration_us,
             sql_text_raw, xe_file_name, file_offset
      FROM executions WHERE execution_id = $id
      """;
    Add(cmd, "$id", executionId);

    string eventName, eventClassText, sqlRaw, xeFile;
    string? objectName, db, login, host, app;
    int? sessionId; long? durationUs; long fileOffset; DateTime capturedAt;
    using (var r = cmd.ExecuteReader())
    {
        if (!r.Read()) throw new InvalidOperationException($"execution {executionId} not found");
        eventName = r.GetString(0);
        eventClassText = r.GetString(1);
        objectName = r.IsDBNull(2) ? null : r.GetString(2);
        db = r.IsDBNull(3) ? null : r.GetString(3);
        login = r.IsDBNull(4) ? null : r.GetString(4);
        host = r.IsDBNull(5) ? null : r.GetString(5);
        app = r.IsDBNull(6) ? null : r.GetString(6);
        sessionId = r.IsDBNull(7) ? null : r.GetInt32(7);
        capturedAt = r.GetDateTime(8);
        durationUs = r.IsDBNull(9) ? null : r.GetInt64(9);
        sqlRaw = r.GetString(10);
        xeFile = r.GetString(11);
        fileOffset = r.GetInt64(12);
    }

    var parameters = new List<RawParameter>();
    using (var pcmd = conn.CreateCommand())
    {
        pcmd.CommandText = """
          SELECT ordinal, name, source_kind, sql_type_guess, value_text, parse_confidence
          FROM execution_parameters WHERE execution_id = $id ORDER BY ordinal
          """;
        Add(pcmd, "$id", executionId);
        using var pr = pcmd.ExecuteReader();
        while (pr.Read())
            parameters.Add(new RawParameter(
                pr.GetInt32(0),
                pr.IsDBNull(1) ? null : pr.GetString(1),
                Enum.Parse<ParameterSourceKind>(pr.GetString(2), ignoreCase: true),
                pr.IsDBNull(3) ? null : pr.GetString(3),
                pr.GetString(4),
                pr.GetDouble(5)));
    }

    return new ExecutionEvent
    {
        CapturedAt = capturedAt,
        EventName = eventName,
        EventClass = Enum.Parse<EventClass>(eventClassText, ignoreCase: true),
        ObjectName = objectName,
        DatabaseName = db,
        LoginName = login,
        ClientHostname = host,
        ClientAppName = app,
        SessionId = sessionId,
        DurationUs = durationUs,
        SqlTextRaw = sqlRaw,
        Parameters = parameters,
        XeFileName = xeFile,
        FileOffset = fileOffset,
    };
}
```

Add `using SqlFerret.Core.Model;` at the top of `WorkloadQueries.cs` if not present.

> **Implementer note:** confirm the `executions` column names against `DuckDbProject.CreateSchema`. `event_class` is stored via `EventClass.ToString()`; `source_kind` via `SourceKind.ToString().ToLowerInvariant()` — both parse back with `ignoreCase: true`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter LoadExecutionTests` then full `dotnet test`
Expected: PASS; no regressions.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): WorkloadQueries.LoadExecution (rebuild ExecutionEvent for replay)"
```

---

## Task 4: IClipboard + native + file-fallback implementations

**Files:**
- Create: `src/SqlFerret.Tui/Clipboard/IClipboard.cs`, `NativeClipboard.cs`, `FileFallbackClipboard.cs`
- Test: `tests/SqlFerret.Tui.Tests/ClipboardTests.cs`

**Interfaces:**
- Produces:
  - `record ClipboardResult(bool ToClipboard, string? FilePath, string Description)`
  - `interface IClipboard { ClipboardResult Copy(string text, string suggestedFileBaseName); }`
  - `class FileFallbackClipboard(string folder) : IClipboard` — writes `<folder>/<suggestedFileBaseName>.sql`, returns `ClipboardResult(false, path, "wrote …")`.
  - `class NativeClipboard(IClipboard fallback) : IClipboard` — tries the Terminal.Gui clipboard; on failure/unavailable delegates to `fallback`.

- [ ] **Step 1: Write the failing test (file fallback is the deterministic, headless one)**

```csharp
// tests/SqlFerret.Tui.Tests/ClipboardTests.cs
using SqlFerret.Tui.Clipboard;
using Xunit;

public class ClipboardTests
{
    [Fact]
    public void FileFallback_writes_sql_file_and_returns_path()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clip = new FileFallbackClipboard(dir);
            var res = clip.Copy("SELECT 1;", "exec-42");
            Assert.False(res.ToClipboard);
            Assert.NotNull(res.FilePath);
            Assert.True(File.Exists(res.FilePath));
            Assert.Equal("SELECT 1;", File.ReadAllText(res.FilePath!));
            Assert.EndsWith("exec-42.sql", res.FilePath);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ClipboardTests`
Expected: FAIL (types undefined).

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Tui/Clipboard/IClipboard.cs
namespace SqlFerret.Tui.Clipboard;

public record ClipboardResult(bool ToClipboard, string? FilePath, string Description);

public interface IClipboard
{
    ClipboardResult Copy(string text, string suggestedFileBaseName);
}
```

```csharp
// src/SqlFerret.Tui/Clipboard/FileFallbackClipboard.cs
namespace SqlFerret.Tui.Clipboard;

public class FileFallbackClipboard(string folder) : IClipboard
{
    public ClipboardResult Copy(string text, string suggestedFileBaseName)
    {
        Directory.CreateDirectory(folder);
        var safe = string.Concat(suggestedFileBaseName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0) safe = "script";
        var path = Path.Combine(folder, $"{safe}.sql");
        File.WriteAllText(path, text);
        return new ClipboardResult(false, path, $"wrote {path}");
    }
}
```

```csharp
// src/SqlFerret.Tui/Clipboard/NativeClipboard.cs
using Terminal.Gui;

namespace SqlFerret.Tui.Clipboard;

// Tries the Terminal.Gui clipboard; falls back to writing a .sql file when no
// system clipboard is available (e.g. Linux without xclip/wl-clipboard).
public class NativeClipboard(IClipboard fallback) : IClipboard
{
    public ClipboardResult Copy(string text, string suggestedFileBaseName)
    {
        try
        {
            if (Clipboard.IsSupported)
            {
                Clipboard.Contents = text;
                return new ClipboardResult(true, null, "copied to clipboard");
            }
        }
        catch { /* fall through to file */ }
        return fallback.Copy(text, suggestedFileBaseName);
    }
}
```

> **Implementer note:** verify the Terminal.Gui 2.4.6 clipboard API (`Clipboard.IsSupported`, `Clipboard.Contents` / `Clipboard.TrySetClipboardData`). If the member names differ, adapt — the contract is: try the system clipboard; on any failure or unsupported, return the file-fallback result. Only `FileFallbackClipboard` is unit-tested (deterministic); `NativeClipboard` is exercised manually.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ClipboardTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): clipboard abstraction with .sql file fallback"
```

---

## Task 5: TopSlowPresenter + test-seeding helper

**Files:**
- Create: `src/SqlFerret.Tui/Presenters/TopSlowPresenter.cs`, `tests/SqlFerret.Tui.Tests/TestProject.cs`
- Test: `tests/SqlFerret.Tui.Tests/TopSlowPresenterTests.cs`

**Interfaces:**
- Consumes: `WorkloadQueries.TopSlow`, `QueryStat`, `FilterRule`.
- Produces:
  - `static class TestProject` (test helper): `static DuckDbProject SeedFrom(IEnumerable<(string name, string sql, string? objectName, long durationUs)> rows)` — opens a temp DuckDB and ingests fake events, returning the open project (caller disposes; file path via `TestProject.LastPath`).
  - `sealed class TopSlowPresenter(WorkloadQueries q)` with: `int Limit {get;set;} = 50`, `string SortColumn {get; private set;} = "total_duration_us"`, `string? TextFilter {get; private set;}`, `IReadOnlyList<FilterRule> Filters {get;set;} = []`, `IReadOnlyList<QueryStat> Load()`, `void CycleSort()` (cycles total→p95→max→avg→total), `void SetTextFilter(string? s)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Tui.Tests/TopSlowPresenterTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Tui.Presenters;
using Xunit;

public class TopSlowPresenterTests
{
    [Fact]
    public void Load_returns_grouped_signatures_sorted_by_total()
    {
        using var db = TestProject.SeedFrom(
        [
            ("sql_batch_completed", "SELECT * FROM dbo.Users WHERE Id = 1", null, 5000),
            ("sql_batch_completed", "SELECT * FROM dbo.Users WHERE Id = 2", null, 7000), // same signature
            ("sql_batch_completed", "SELECT * FROM dbo.Orders", null, 1000),
        ]);
        var p = new TopSlowPresenter(new WorkloadQueries(db.Connection));
        var rows = p.Load();
        Assert.Equal(2, rows.Count);                          // two distinct signatures
        Assert.Equal(2, rows[0].Count);                       // Users signature aggregated (5000+7000)
        Assert.True(rows[0].TotalDurationUs >= rows[1].TotalDurationUs); // sorted by total desc
    }

    [Fact]
    public void TextFilter_narrows_by_normalized_sql()
    {
        using var db = TestProject.SeedFrom(
        [
            ("sql_batch_completed", "SELECT * FROM dbo.Users WHERE Id = 1", null, 5000),
            ("sql_batch_completed", "SELECT * FROM dbo.Orders", null, 1000),
        ]);
        var p = new TopSlowPresenter(new WorkloadQueries(db.Connection));
        p.SetTextFilter("orders");
        var rows = p.Load();
        Assert.Single(rows);
        Assert.Contains("orders", rows[0].NormalizedSql);
    }

    [Fact]
    public void CycleSort_advances_through_columns()
    {
        using var db = TestProject.SeedFrom([("sql_batch_completed", "SELECT 1", null, 1)]);
        var p = new TopSlowPresenter(new WorkloadQueries(db.Connection));
        Assert.Equal("total_duration_us", p.SortColumn);
        p.CycleSort(); Assert.Equal("p95_duration_us", p.SortColumn);
        p.CycleSort(); Assert.Equal("max_duration_us", p.SortColumn);
        p.CycleSort(); Assert.Equal("avg_duration_us", p.SortColumn);
        p.CycleSort(); Assert.Equal("total_duration_us", p.SortColumn);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter TopSlowPresenterTests`
Expected: FAIL (`TestProject`, `TopSlowPresenter` undefined).

- [ ] **Step 3: Implement the seeding helper and the presenter**

```csharp
// tests/SqlFerret.Tui.Tests/TestProject.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public static class TestProject
{
    public static string LastPath { get; private set; } = "";

    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    // Seeds a fresh temp DuckDB by ingesting fake events. sql goes to batch_text for
    // sql_batch_completed, or to statement for rpc_completed (object_name set from objectName).
    public static DuckDbProject SeedFrom(IEnumerable<(string name, string sql, string? objectName, long durationUs)> rows)
    {
        LastPath = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        var db = DuckDbProject.Open(LastPath);
        long offset = 0;
        var events = new List<(IXeEventData, string, long)>();
        foreach (var (name, sql, objectName, dur) in rows)
        {
            var fields = new Dictionary<string, object?> { ["duration"] = dur };
            if (name.Contains("batch")) fields["batch_text"] = sql; else fields["statement"] = sql;
            if (objectName is not null) fields["object_name"] = objectName;
            events.Add((new FakeEvent(name, new DateTime(2026, 1, 1), fields, new Dictionary<string, object?>()),
                       "s_0.xel", offset++));
        }
        new IngestionService(db, new IngestionOptions(RedactionMode.Full, [])).Ingest("logs/", events);
        return db;
    }
}
```

```csharp
// src/SqlFerret.Tui/Presenters/TopSlowPresenter.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;

namespace SqlFerret.Tui.Presenters;

public sealed class TopSlowPresenter(WorkloadQueries q)
{
    private static readonly string[] SortCycle =
        ["total_duration_us", "p95_duration_us", "max_duration_us", "avg_duration_us"];

    public int Limit { get; set; } = 50;
    public string SortColumn { get; private set; } = "total_duration_us";
    public string? TextFilter { get; private set; }
    public IReadOnlyList<FilterRule> Filters { get; set; } = [];

    public IReadOnlyList<QueryStat> Load()
    {
        var rows = q.TopSlow(Limit, SortColumn, Filters);
        if (!string.IsNullOrWhiteSpace(TextFilter))
            rows = rows.Where(r => r.NormalizedSql.Contains(TextFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        return rows;
    }

    public void CycleSort()
    {
        int i = Array.IndexOf(SortCycle, SortColumn);
        SortColumn = SortCycle[(i + 1) % SortCycle.Length];
    }

    public void SetTextFilter(string? s) => TextFilter = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter TopSlowPresenterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): TopSlowPresenter + test seeding helper"
```

---

## Task 6: DrillDownPresenter

**Files:**
- Create: `src/SqlFerret.Tui/Presenters/DrillDownPresenter.cs`
- Test: `tests/SqlFerret.Tui.Tests/DrillDownPresenterTests.cs`

**Interfaces:**
- Consumes: `WorkloadQueries.Occurrences/ParameterImpact/LoadExecution`, `ReplayBuilder`, `QueryStat`, `Occurrence`, `ParamImpact`, `ReplayScript`.
- Produces: `sealed class DrillDownPresenter(WorkloadQueries q, QueryStat signature)` with `IReadOnlyList<Occurrence> Occurrences(int limit = 200)`, `IReadOnlyList<ParamImpact> ParameterImpact(string paramName)`, `ReplayScript BuildReplay(long executionId)`. `Signature` is exposed as a read-only property for the view header.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Tui.Tests/DrillDownPresenterTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Replay;
using SqlFerret.Tui.Presenters;
using Xunit;

public class DrillDownPresenterTests
{
    [Fact]
    public void Occurrences_and_replay_for_rpc()
    {
        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 123", "dbo.GetOrder", 4000),
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 999", "dbo.GetOrder", 6000),
        ]);
        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var p = new DrillDownPresenter(q, sig);

        var occ = p.Occurrences();
        Assert.Equal(2, occ.Count);

        var replay = p.BuildReplay(occ[0].ExecutionId);
        Assert.Equal(ReplayKind.ExecProc, replay.Kind);
        Assert.StartsWith("EXEC dbo.GetOrder @OrderId = ", replay.Sql);
    }

    [Fact]
    public void ParameterImpact_groups_by_value_slowest_first()
    {
        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 1", "dbo.GetOrder", 1000),
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 2", "dbo.GetOrder", 9000),
        ]);
        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var p = new DrillDownPresenter(q, sig);
        var impact = p.ParameterImpact("@OrderId");
        Assert.Equal(2, impact.Count);
        Assert.True(impact[0].AvgDurationUs >= impact[1].AvgDurationUs); // slowest value-set first
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter DrillDownPresenterTests`
Expected: FAIL (`DrillDownPresenter` undefined).

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Tui/Presenters/DrillDownPresenter.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Replay;

namespace SqlFerret.Tui.Presenters;

public sealed class DrillDownPresenter(WorkloadQueries q, QueryStat signature)
{
    public QueryStat Signature => signature;

    public IReadOnlyList<Occurrence> Occurrences(int limit = 200) =>
        q.Occurrences(signature.NormalizedHash, limit);

    public IReadOnlyList<ParamImpact> ParameterImpact(string paramName) =>
        q.ParameterImpact(signature.NormalizedHash, paramName);

    public ReplayScript BuildReplay(long executionId) =>
        ReplayBuilder.Build(q.LoadExecution(executionId));
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter DrillDownPresenterTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): DrillDownPresenter (occurrences, param-impact, build-for-SSMS)"
```

---

## Task 7: ImportPresenter

**Files:**
- Create: `src/SqlFerret.Tui/Presenters/ImportPresenter.cs`
- Test: `tests/SqlFerret.Tui.Tests/ImportPresenterTests.cs`

**Interfaces:**
- Consumes: `XelSource.Resolve`, `XelReader`, `IngestionService`, `IngestionOptions`, `IngestionResult`, `IngestionProgress`, `RedactionMode`, `DuckDbProject`.
- Produces: `sealed class ImportPresenter(DuckDbProject project)` with `Task<IngestionResult> RunAsync(string path, RedactionMode redaction, IProgress<IngestionProgress> progress, CancellationToken ct)`. Resolves files, reads via `XelReader`, ingests with the progress callback. Throws `FileNotFoundException` for a bad path (from `XelSource.Resolve`).

- [ ] **Step 1: Write the failing test (real `.xel` via the gitignored sample/ — SkippableFact)**

```csharp
// tests/SqlFerret.Tui.Tests/ImportPresenterTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Presenters;
using Xunit;

public class ImportPresenterTests
{
    // Walk up from the test bin dir to find the gitignored sample/ folder of real .xel traces.
    private static string? FindSampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sample = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(sample))
            {
                var perf = Directory.GetFiles(sample, "performances_*.xel");
                if (perf.Length > 0) return perf.OrderBy(f => new FileInfo(f).Length).First();
            }
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public async Task RunAsync_imports_real_sample_and_reports_progress()
    {
        var file = FindSampleFile();
        Skip.If(file is null, "sample/ folder with a performances_*.xel trace not present");

        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var presenter = new ImportPresenter(db);
            var ticks = new List<IngestionProgress>();
            IProgress<IngestionProgress> sync = new ListProgress(ticks.Add);

            var result = await presenter.RunAsync(file!, RedactionMode.Masked, sync, CancellationToken.None);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);
            Assert.Equal(result.Read, ticks[^1].Read);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ListProgress(Action<IngestionProgress> a) : IProgress<IngestionProgress>
    { public void Report(IngestionProgress value) => a(value); }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ImportPresenterTests`
Expected: FAIL to compile (`ImportPresenter` undefined). After Step 3 it PASSES on a machine with `sample/`, or SKIPS where absent.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Tui/Presenters/ImportPresenter.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Tui.Presenters;

public sealed class ImportPresenter(DuckDbProject project)
{
    public Task<IngestionResult> RunAsync(
        string path, RedactionMode redaction, IProgress<IngestionProgress> progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var (files, _) = XelSource.Resolve(path);           // throws FileNotFoundException for a bad path
            var events = new XelReader().Read(files);
            var svc = new IngestionService(project, new IngestionOptions(redaction, []));
            return svc.Ingest(path, events, progress);
        }, ct);
    }
}
```

- [ ] **Step 4: Run to verify pass/skip**

Run: `dotnet test --filter ImportPresenterTests`
Expected: PASS where `sample/` exists; SKIPPED otherwise. Full `dotnet test` stays green.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): ImportPresenter (async ingestion with progress)"
```

---

## Task 8: Shell — MainWindow (view-rail, content host, title, status bar) + smoke test

**Files:**
- Create: `src/SqlFerret.Tui/Shell/Keys.cs`, `src/SqlFerret.Tui/Shell/MainWindow.cs`
- Modify: `src/SqlFerret.Tui/Program.cs`
- Test: `tests/SqlFerret.Tui.Tests/ShellSmokeTests.cs`

**Interfaces:**
- Consumes: `DuckDbProject`, `SqlFerretConfig`, `UiState`, `WorkloadQueries`.
- Produces: `sealed class MainWindow : Window` constructed as `new MainWindow(AppContext ctx)` where `record AppContext(DuckDbProject Project, SqlFerretConfig Config, UiState Ui, IClipboard Clipboard, string UiStatePath)`. Left `ListView` rail with entries `Import`, `Top Slow`; a content `FrameView` that hosts the active view; a title `Label`; a `StatusBar`. Selecting a rail entry swaps the content view. `static class Keys` holds key constants.

- [ ] **Step 1: Write the failing smoke test**

```csharp
// tests/SqlFerret.Tui.Tests/ShellSmokeTests.cs
using SqlFerret.Core.Config;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Shell;
using Terminal.Gui;
using Xunit;

public class ShellSmokeTests
{
    [Fact]
    public void MainWindow_constructs_without_throwing()
    {
        Application.Init(new FakeDriver());     // headless driver — no real terminal
        try
        {
            using var db = TestProject.SeedFrom([("sql_batch_completed", "SELECT 1", null, 10)]);
            var dir = Directory.CreateTempSubdirectory().FullName;
            var ctx = new AppContext(db, SqlFerretConfig.Load(Path.Combine(dir, "missing.json")),
                new UiState(), new FileFallbackClipboard(dir), Path.Combine(dir, "ui.json"));
            var win = new MainWindow(ctx);
            Assert.NotNull(win);
            win.Dispose();
        }
        finally { Application.Shutdown(); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ShellSmokeTests`
Expected: FAIL (`MainWindow`, `AppContext` undefined).

> **Implementer note:** Terminal.Gui 2.4.6 ships a headless `FakeDriver` for tests — confirm the exact type name and the `Application.Init(driver)` overload. If it differs, use the documented test-init path; the contract is "construct `MainWindow` with no real terminal and assert no throw."

- [ ] **Step 3: Implement Keys, AppContext, MainWindow, and wire Program.cs**

```csharp
// src/SqlFerret.Tui/Shell/Keys.cs
using Terminal.Gui;
namespace SqlFerret.Tui.Shell;

public static class Keys
{
    public static readonly Key Quit = Key.Q;
    public static readonly Key Filter = Key.Slash;
    public static readonly Key Sort = Key.S;
    public static readonly Key Columns = Key.C.WithShift;     // 'C'
    public static readonly Key Copy = Key.C;                  // 'c'
    public static readonly Key Back = Key.Esc;
}
```

```csharp
// src/SqlFerret.Tui/Shell/MainWindow.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Clipboard;
using Terminal.Gui;

namespace SqlFerret.Tui.Shell;

public record AppContext(DuckDbProject Project, SqlFerretConfig Config, UiState Ui, IClipboard Clipboard, string UiStatePath);

public sealed class MainWindow : Window
{
    private readonly AppContext _ctx;
    private readonly FrameView _content;
    private readonly Label _title;

    public MainWindow(AppContext ctx)
    {
        _ctx = ctx;
        Title = "SQLFerret";

        _title = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = TitleText() };

        var rail = new ListView
        {
            X = 0, Y = 1, Width = 16, Height = Dim.Fill(1),
            Source = new ListWrapper<string>(["Import", "Top Slow"]),
        };
        var frame = new FrameView { Title = "Top Slow" };
        _content = frame;
        frame.X = 16; frame.Y = 1; frame.Width = Dim.Fill(); frame.Height = Dim.Fill(1);

        rail.SelectedItemChanged += (_, e) => Show(e.Item);

        var status = new StatusBar(
        [
            new Shortcut(Keys.Quit, "Quit", () => Application.RequestStop()),
        ]) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        Add(_title, rail, frame, status);
        Show(1);                                  // default to Top Slow
    }

    private string TitleText()
    {
        using var c = _ctx.Project.Connection.CreateCommand();
        c.CommandText = "SELECT (SELECT count(*) FROM executions), (SELECT count(*) FROM normalized_queries)";
        using var r = c.ExecuteReader(); r.Read();
        return $" SQLFerret · {r.GetInt64(0):n0} execs / {r.GetInt64(1):n0} signatures";
    }

    // Swap the content view. Real view wiring lands in Tasks 9-11; here it hosts a placeholder
    // so the shell is independently testable.
    private void Show(int railIndex)
    {
        _content.RemoveAll();
        _content.Title = railIndex == 0 ? "Import" : "Top Slow";
        _content.Add(new Label { X = 0, Y = 0, Text = $"(view {railIndex})" });
    }
}
```

Update `src/SqlFerret.Tui/Program.cs` to build the real shell:

```csharp
using SqlFerret.Core.Config;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Shell;
using Terminal.Gui;

if (args.Length < 1) { Console.Error.WriteLine("usage: SqlFerret.Tui <project.duckdb>"); return 1; }

DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
var config = SqlFerretConfig.Load(Path.Combine(Directory.GetCurrentDirectory(), "sqlferret.config.json"));
var uiPath = Path.Combine(Directory.GetCurrentDirectory(), "sqlferret.ui.json");
var ui = UiState.Load(uiPath);
using var project = DuckDbProject.Open(args[0]);
var clipboard = new NativeClipboard(new FileFallbackClipboard(Path.GetTempPath()));

Application.Init();
try
{
    var ctx = new AppContext(project, config, ui, clipboard, uiPath);
    using var win = new MainWindow(ctx);
    Application.Run(win);
    ui.Save(uiPath);
}
finally { Application.Shutdown(); }
return 0;
```

> **Implementer note:** the Terminal.Gui 2.4.6 layout API (`Dim.Fill`, `Pos.AnchorEnd`, `ListView.Source`/`ListWrapper`, `StatusBar`/`Shortcut`, `FrameView.RemoveAll`) may differ slightly from this reference. Adapt member names to the installed API; the contract is the layout described in **Interfaces**. Keep `MainWindow` host-only — it must not perform analysis.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ShellSmokeTests` then full `dotnet test`
Expected: PASS; no regressions. Optionally run the app against a real project to eyeball the shell.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): app shell (view-rail, content host, title, status bar)"
```

---

## Task 9: Top Slow view (TableView bound to TopSlowPresenter)

**Files:**
- Create: `src/SqlFerret.Tui/Views/TopSlowView.cs`
- Modify: `src/SqlFerret.Tui/Shell/MainWindow.cs` (host the real view; raise a drill event)

**Interfaces:**
- Consumes: `TopSlowPresenter`, `QueryStat`, `DisplayFormat`, `Keys`.
- Produces: `sealed class TopSlowView : View` constructed `new TopSlowView(TopSlowPresenter presenter, string durationUnit)`. Renders a `TableView` of `kind · signature · count · avg · p95 · max · total` (durations via `DisplayFormat`). Keys: `s` cycle sort + reload, `/` prompt text filter + reload, `Enter` raises `event Action<QueryStat>? DrillRequested`. Exposes `void Reload()`.

- [ ] **Step 1: Behavior is presenter-driven (already unit-tested in Task 5).** The view is UI glue. Add a thin **render test** that the view builds its table from the presenter without throwing under `FakeDriver`:

```csharp
// tests/SqlFerret.Tui.Tests/TopSlowViewTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui;
using Xunit;

public class TopSlowViewTests
{
    [Fact]
    public void View_builds_rows_from_presenter()
    {
        Application.Init(new FakeDriver());
        try
        {
            using var db = TestProject.SeedFrom([("sql_batch_completed", "SELECT * FROM dbo.Users WHERE Id=1", null, 5000)]);
            var view = new TopSlowView(new TopSlowPresenter(new WorkloadQueries(db.Connection)), "ms");
            view.Reload();
            Assert.Equal(1, view.RowCount);        // expose RowCount for the test
            view.Dispose();
        }
        finally { Application.Shutdown(); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter TopSlowViewTests`
Expected: FAIL (`TopSlowView` undefined).

- [ ] **Step 3: Implement TopSlowView and host it in MainWindow**

```csharp
// src/SqlFerret.Tui/Views/TopSlowView.cs
using System.Data;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Shell;
using Terminal.Gui;

namespace SqlFerret.Tui.Views;

public sealed class TopSlowView : View
{
    private readonly TopSlowPresenter _p;
    private readonly string _unit;
    private readonly TableView _table;
    private IReadOnlyList<QueryStat> _rows = [];

    public event Action<QueryStat>? DrillRequested;
    public int RowCount => _rows.Count;

    public TopSlowView(TopSlowPresenter presenter, string durationUnit)
    {
        _p = presenter; _unit = durationUnit;
        Width = Dim.Fill(); Height = Dim.Fill();
        _table = new TableView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), FullRowSelect = true };
        var hint = new Label { X = 0, Y = Pos.AnchorEnd(1), Text = "Enter=drill  s=sort  /=filter  C=cols" };
        Add(_table, hint);

        _table.KeyDown += (_, key) =>
        {
            if (key == Keys.Sort) { _p.CycleSort(); Reload(); key.Handled = true; }
            else if (key == Keys.Filter) { PromptFilter(); key.Handled = true; }
            else if (key == Key.Enter && _table.SelectedRow >= 0 && _table.SelectedRow < _rows.Count)
            { DrillRequested?.Invoke(_rows[_table.SelectedRow]); key.Handled = true; }
        };
    }

    public void Reload()
    {
        _rows = _p.Load();
        var dt = new DataTable();
        foreach (var col in new[] { "kind", "signature", "count", "avg", "p95", "max", "total" }) dt.Columns.Add(col);
        foreach (var s in _rows)
            dt.Rows.Add(s.StatementKind, Trim(s.NormalizedSql), s.Count,
                DisplayFormat.Duration((long)s.AvgDurationUs, _unit),
                DisplayFormat.Duration(s.P95DurationUs, _unit),
                DisplayFormat.Duration(s.MaxDurationUs, _unit),
                DisplayFormat.Duration(s.TotalDurationUs, _unit));
        _table.Table = new DataTableSource(dt);
    }

    private void PromptFilter()
    {
        // Minimal inline prompt; adapt to TG 2.4.6 dialog API.
        var input = new TextField { Width = 40 };
        var dlg = new Dialog { Title = "Filter (normalized SQL)", Width = 50, Height = 7 };
        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepting += (_, _) => { _p.SetTextFilter(input.Text?.ToString()); Application.RequestStop(); };
        dlg.Add(new Label { X = 0, Y = 0, Text = "contains:" }, input); dlg.AddButton(ok);
        Application.Run(dlg); dlg.Dispose();
        Reload();
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
```

In `MainWindow.Show`, host the real view for index 1 and forward `DrillRequested` (drill view lands in Task 10 — for now, on drill, open a placeholder or no-op until Task 10):

```csharp
// inside MainWindow.Show(int railIndex)  — replace the Top Slow placeholder branch:
if (railIndex == 1)
{
    var view = new SqlFerret.Tui.Views.TopSlowView(
        new SqlFerret.Tui.Presenters.TopSlowPresenter(new WorkloadQueries(_ctx.Project.Connection)),
        _ctx.Config.DurationUnit);
    view.DrillRequested += OpenDrillDown;     // OpenDrillDown added in Task 10; stub it to no-op for now
    view.Reload();
    _content.RemoveAll(); _content.Title = "Top Slow"; _content.Add(view);
    view.SetFocus();
    return;
}
```

> **Implementer note:** verify `DataTableSource`, `TableView.Table`, `TextField.Text`, `Button.Accepting`, and `Dialog`/`Application.Run(dialog)` against Terminal.Gui 2.4.6. Adapt member names; the contract is "render the presenter's rows; `s` re-sorts; `/` filters; `Enter` drills." Add a temporary `private void OpenDrillDown(QueryStat s) { }` stub to `MainWindow` so this task compiles; Task 10 replaces it.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter TopSlowViewTests` then full `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): Top Slow view (sortable/filterable grid)"
```

---

## Task 10: Drill-down view (occurrences, parameter-impact, build-for-SSMS)

**Files:**
- Create: `src/SqlFerret.Tui/Views/DrillDownView.cs`
- Modify: `src/SqlFerret.Tui/Shell/MainWindow.cs` (replace the `OpenDrillDown` stub)

**Interfaces:**
- Consumes: `DrillDownPresenter`, `Occurrence`, `ParamImpact`, `ReplayScript`, `IClipboard`, `DisplayFormat`, `Keys`.
- Produces: `sealed class DrillDownView : View` constructed `new DrillDownView(DrillDownPresenter presenter, IClipboard clipboard, string durationUnit)`. Header shows the signature's raw+normalized SQL and metric summary. `Tab` cycles sub-panels Occurrences → Parameter-impact → (back). On an occurrence row, `c` builds the replay via `BuildReplay`, copies via `IClipboard`, and shows a result line with `ReplayKind`, confidence, and a redaction note. Raises `event Action? BackRequested` on `Esc`. Exposes `void Reload()` and `int OccurrenceCount`.

- [ ] **Step 1: Render test (presenter logic already covered in Task 6)**

```csharp
// tests/SqlFerret.Tui.Tests/DrillDownViewTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui;
using Xunit;

public class DrillDownViewTests
{
    [Fact]
    public void View_lists_occurrences_and_copies_replay()
    {
        Application.Init(new FakeDriver());
        try
        {
            using var db = TestProject.SeedFrom([("rpc_completed", "exec dbo.GetOrder @OrderId = 1", "dbo.GetOrder", 4000)]);
            var q = new WorkloadQueries(db.Connection);
            var sig = q.TopSlow(10, "total_duration_us", [])[0];
            var dir = Directory.CreateTempSubdirectory().FullName;
            var view = new DrillDownView(new DrillDownPresenter(q, sig), new FileFallbackClipboard(dir), "ms");
            view.Reload();
            Assert.Equal(1, view.OccurrenceCount);
            var res = view.CopySelectedReplay();           // expose for the test
            Assert.True(File.Exists(res.FilePath));
            Assert.Contains("EXEC dbo.GetOrder", File.ReadAllText(res.FilePath!));
            view.Dispose();
        }
        finally { Application.Shutdown(); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter DrillDownViewTests`
Expected: FAIL (`DrillDownView` undefined).

- [ ] **Step 3: Implement DrillDownView and wire MainWindow.OpenDrillDown**

```csharp
// src/SqlFerret.Tui/Views/DrillDownView.cs
using System.Data;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Shell;
using Terminal.Gui;

namespace SqlFerret.Tui.Views;

public sealed class DrillDownView : View
{
    private readonly DrillDownPresenter _p;
    private readonly IClipboard _clip;
    private readonly string _unit;
    private readonly TableView _occ;
    private readonly Label _result;
    private IReadOnlyList<Occurrence> _rows = [];

    public event Action? BackRequested;
    public int OccurrenceCount => _rows.Count;

    public DrillDownView(DrillDownPresenter presenter, IClipboard clipboard, string durationUnit)
    {
        _p = presenter; _clip = clipboard; _unit = durationUnit;
        Width = Dim.Fill(); Height = Dim.Fill();

        var header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 3,
            Text = $"{_p.Signature.StatementKind}  {_p.Signature.NormalizedSql}\n"
                 + $"count={_p.Signature.Count}  avg={DisplayFormat.Duration((long)_p.Signature.AvgDurationUs, _unit)}"
                 + $"  p95={DisplayFormat.Duration(_p.Signature.P95DurationUs, _unit)}" };
        _occ = new TableView { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
        _result = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "Esc=back  c=copy build-for-SSMS" };
        Add(header, _occ, _result);

        _occ.KeyDown += (_, key) =>
        {
            if (key == Keys.Copy) { var r = CopySelectedReplay(); _result.Text = r.Description; key.Handled = true; }
            else if (key == Keys.Back) { BackRequested?.Invoke(); key.Handled = true; }
        };
    }

    public void Reload()
    {
        _rows = _p.Occurrences();
        var dt = new DataTable();
        foreach (var c in new[] { "time", "db", "login", "duration", "sql" }) dt.Columns.Add(c);
        foreach (var o in _rows)
            dt.Rows.Add(o.CapturedAt.ToString("HH:mm:ss"), o.Database ?? "", o.Login ?? "",
                o.DurationUs is { } d ? DisplayFormat.Duration(d, _unit) : "", Trim(o.SqlTextRaw));
        _occ.Table = new DataTableSource(dt);
    }

    public ClipboardResult CopySelectedReplay()
    {
        int i = _occ.SelectedRow;
        if (i < 0 || i >= _rows.Count) return new ClipboardResult(false, null, "no row selected");
        var script = _p.BuildReplay(_rows[i].ExecutionId);
        var res = _clip.Copy(script.Sql, $"exec-{_rows[i].ExecutionId}");
        string note = script.Confidence < 1.0 ? $" (confidence {script.Confidence:0.0})" : "";
        return res with { Description = $"{script.Kind}: {res.Description}{note}" };
    }

    private static string Trim(string s) => s.Length <= 50 ? s : s[..47] + "...";
}
```

Replace the `OpenDrillDown` stub in `MainWindow`:

```csharp
private void OpenDrillDown(QueryStat signature)
{
    var view = new SqlFerret.Tui.Views.DrillDownView(
        new SqlFerret.Tui.Presenters.DrillDownPresenter(new WorkloadQueries(_ctx.Project.Connection), signature),
        _ctx.Clipboard, _ctx.Config.DurationUnit);
    view.BackRequested += () => Show(1);          // back to Top Slow
    view.Reload();
    _content.RemoveAll(); _content.Title = "Drill-down"; _content.Add(view); view.SetFocus();
}
```

> **Implementer note:** parameter-impact sub-panel (prompt for `@param`, show `ParameterImpact`) follows the same `DataTableSource` pattern; add it as a second `TableView` toggled by `Tab`. Keep the MVP focused: Occurrences + build-for-SSMS are the must-haves; parameter-impact is included here but its grid mirrors Occurrences. Verify TG 2.4.6 members as before.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter DrillDownViewTests` then full `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): drill-down view (occurrences, param-impact, build-for-SSMS)"
```

---

## Task 11: Import view (form + async progress + redaction picker)

**Files:**
- Create: `src/SqlFerret.Tui/Views/ImportView.cs`
- Modify: `src/SqlFerret.Tui/Shell/MainWindow.cs` (host the real Import view at rail index 0)

**Interfaces:**
- Consumes: `ImportPresenter`, `IngestionProgress`, `IngestionResult`, `RedactionMode`, `Keys`.
- Produces: `sealed class ImportView : View` constructed `new ImportView(ImportPresenter presenter, RedactionMode defaultRedaction)`. A form: path `TextField` (+ a browse button using TG `FileDialog`), redaction `RadioGroup`, `Start` button. On Start: disables inputs, runs `presenter.RunAsync` with `progress = new Progress<IngestionProgress>(p => UpdateCounters(p))` (the `Progress<T>` callback runs on the captured sync context; inside the view, marshal with `Application.Invoke`). Raises `event Action<IngestionResult>? Completed`. Exposes `Task StartAsync(string path)` for testing the wiring headlessly.

- [ ] **Step 1: Headless wiring test (real ingestion lives in Task 7's SkippableFact; here assert the view drives the presenter and surfaces a result)**

```csharp
// tests/SqlFerret.Tui.Tests/ImportViewTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui;
using Xunit;

public class ImportViewTests
{
    private static string? FindSampleFile()  // same walk-up as ImportPresenterTests
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sample = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(sample))
            {
                var perf = Directory.GetFiles(sample, "performances_*.xel");
                if (perf.Length > 0) return perf.OrderBy(f => new FileInfo(f).Length).First();
            }
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public async Task Start_runs_import_and_raises_completed()
    {
        var file = FindSampleFile();
        Skip.If(file is null, "sample/ not present");
        Application.Init(new FakeDriver());
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(
                Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb"));
            var view = new ImportView(new ImportPresenter(db), RedactionMode.Masked);
            IngestionResult? done = null;
            view.Completed += r => done = r;
            await view.StartAsync(file!);
            Assert.NotNull(done);
            Assert.True(done!.Read > 0);
            view.Dispose();
        }
        finally { Application.Shutdown(); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ImportViewTests`
Expected: FAIL (`ImportView` undefined).

- [ ] **Step 3: Implement ImportView and host it**

```csharp
// src/SqlFerret.Tui/Views/ImportView.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Tui.Presenters;
using Terminal.Gui;

namespace SqlFerret.Tui.Views;

public sealed class ImportView : View
{
    private static readonly RedactionMode[] Modes = [RedactionMode.Off, RedactionMode.Hash, RedactionMode.Masked, RedactionMode.Full];
    private readonly ImportPresenter _p;
    private readonly TextField _path;
    private readonly RadioGroup _redaction;
    private readonly Label _progress;

    public event Action<IngestionResult>? Completed;

    public ImportView(ImportPresenter presenter, RedactionMode defaultRedaction)
    {
        _p = presenter;
        Width = Dim.Fill(); Height = Dim.Fill();
        _path = new TextField { X = 12, Y = 0, Width = Dim.Fill(2) };
        _redaction = new RadioGroup
        {
            X = 12, Y = 2,
            RadioLabels = [.. Modes.Select(m => m.ToString())],
            SelectedItem = Array.IndexOf(Modes, defaultRedaction),
        };
        var start = new Button { X = 12, Y = 7, Text = "Start" };
        _progress = new Label { X = 0, Y = 9, Width = Dim.Fill(), Text = "" };
        start.Accepting += async (_, _) => await StartAsync(_path.Text?.ToString() ?? "");
        Add(new Label { X = 0, Y = 0, Text = "Path:" }, _path,
            new Label { X = 0, Y = 2, Text = "Redaction:" }, _redaction, start, _progress);
    }

    public async Task StartAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { _progress.Text = "enter a .xel file or folder path"; return; }
        var redaction = Modes[_redaction.SelectedItem];
        var progress = new Progress<IngestionProgress>(p =>
            Application.Invoke(() => _progress.Text =
                $"read={p.Read} mapped={p.Mapped} unmapped={p.Unmapped} cleaned={p.Cleaned} failures={p.TokenizeFailures}  [{p.CurrentFile}]"));
        try
        {
            var result = await _p.RunAsync(path, redaction, progress, CancellationToken.None);
            _progress.Text = $"done — run {result.RunId}: read={result.Read} mapped={result.Mapped}";
            Completed?.Invoke(result);
        }
        catch (Exception ex) { _progress.Text = $"import failed: {ex.Message}"; }
    }
}
```

Host it at rail index 0 in `MainWindow.Show` (forward `Completed` → refresh title + switch to Top Slow):

```csharp
if (railIndex == 0)
{
    var view = new SqlFerret.Tui.Views.ImportView(
        new SqlFerret.Tui.Presenters.ImportPresenter(_ctx.Project),
        Enum.TryParse<SqlFerret.Core.Parameters.RedactionMode>(_ctx.Config.RedactionPolicy, true, out var m)
            ? m : SqlFerret.Core.Parameters.RedactionMode.Masked);
    view.Completed += _ => { _title.Text = TitleText(); Show(1); };
    _content.RemoveAll(); _content.Title = "Import"; _content.Add(view); view.SetFocus();
    return;
}
```

> **Implementer note:** verify `RadioGroup.RadioLabels`/`SelectedItem`, `Button.Accepting`, and `Application.Invoke` against TG 2.4.6. The threading contract is fixed: `Task.Run` does the ingest (inside `ImportPresenter`); `Progress<T>`/`Application.Invoke` marshal UI updates. The view never blocks the main loop. Optionally add a `FileDialog` browse button — not required for the MVP.

- [ ] **Step 4: Run to verify pass/skip**

Run: `dotnet test --filter ImportViewTests` then full `dotnet test`
Expected: PASS where `sample/` exists; SKIPPED otherwise. No regressions.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): Import view (async ingestion with live progress)"
```

---

## Task 12: Column chooser + UiState persistence + manual end-to-end

**Files:**
- Create: `src/SqlFerret.Tui/Views/ColumnChooserDialog.cs`
- Modify: `src/SqlFerret.Tui/Views/TopSlowView.cs` (apply chosen columns; `C` opens the chooser; load/save `ViewLayout`), `src/SqlFerret.Tui/Shell/MainWindow.cs` (pass `UiState` + path to the view)
- Test: `tests/SqlFerret.Tui.Tests/ColumnChooserTests.cs`

**Interfaces:**
- Consumes: `UiState`, `UiState.ViewLayout`, `Keys`.
- Produces:
  - `static class ColumnChooser` with `IReadOnlyList<string> Choose(IReadOnlyList<string> all, IReadOnlyList<string> current)` — pure logic returning the chosen/ordered subset (the dialog UI calls this; the function is what we unit-test). For the MVP, the dialog supports show/hide + reset; reorder is optional.
  - `TopSlowView` reads its `ViewLayout` (key `"topSlow"`) from `UiState` on construction (falling back to all columns) and writes it back via `UiState.Save` when columns change.

- [ ] **Step 1: Write the failing test for the pure chooser logic**

```csharp
// tests/SqlFerret.Tui.Tests/ColumnChooserTests.cs
using SqlFerret.Tui.Views;
using Xunit;

public class ColumnChooserTests
{
    [Fact]
    public void Choose_keeps_only_selected_in_catalog_order()
    {
        string[] all = ["kind", "signature", "count", "avg", "p95", "max", "total"];
        // simulate selecting kind, signature, total (hiding the rest)
        var chosen = ColumnChooser.Apply(all, selected: ["signature", "total", "kind"]);
        Assert.Equal(["kind", "signature", "total"], chosen);   // returned in catalog order
    }

    [Fact]
    public void Choose_empty_selection_falls_back_to_all()
    {
        string[] all = ["kind", "signature", "total"];
        Assert.Equal(all, ColumnChooser.Apply(all, selected: []));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ColumnChooserTests`
Expected: FAIL (`ColumnChooser` undefined).

- [ ] **Step 3: Implement the pure logic, the dialog, and wire TopSlowView persistence**

```csharp
// src/SqlFerret.Tui/Views/ColumnChooserDialog.cs
using SqlFerret.Tui.Shell;
using Terminal.Gui;

namespace SqlFerret.Tui.Views;

public static class ColumnChooser
{
    // Pure: returns the catalog filtered to `selected`, preserving catalog order.
    // Empty selection falls back to the full catalog (never show zero columns).
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> catalog, IReadOnlyList<string> selected)
    {
        if (selected.Count == 0) return catalog;
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return catalog.Where(set.Contains).ToList();
    }

    // Modal chooser (checkbox list + reset). Returns the new visible-column list, or null on cancel.
    public static IReadOnlyList<string>? Show(IReadOnlyList<string> catalog, IReadOnlyList<string> current)
    {
        var checks = catalog.Select(c => new CheckBox { Text = c, CheckedState =
            current.Contains(c) ? CheckState.Checked : CheckState.UnChecked }).ToList();
        var dlg = new Dialog { Title = "Choose columns", Width = 30, Height = catalog.Count + 6 };
        for (int i = 0; i < checks.Count; i++) { checks[i].X = 1; checks[i].Y = i; dlg.Add(checks[i]); }
        IReadOnlyList<string>? result = null;
        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepting += (_, _) =>
        {
            var sel = catalog.Where((c, i) => checks[i].CheckedState == CheckState.Checked).ToList();
            result = Apply(catalog, sel); Application.RequestStop();
        };
        var reset = new Button { Text = "Reset" };
        reset.Accepting += (_, _) => { result = catalog; Application.RequestStop(); };
        dlg.AddButton(ok); dlg.AddButton(reset);
        Application.Run(dlg); dlg.Dispose();
        return result;
    }
}
```

In `TopSlowView`: add **optional** `UiState? ui = null` + `string? uiPath = null` constructor parameters (optional so Task 9's test and the Task 9 `MainWindow` call still compile unchanged). Read the `"topSlow"` `ViewLayout` to set the visible columns (default = full catalog), build the `DataTable` from the **visible** columns only, handle `C`:

```csharp
// add fields: private readonly UiState _ui; private readonly string? _uiPath;
// private static readonly string[] Catalog = ["kind","signature","count","avg","p95","max","total"];
// private string[] _visible;
// ctor signature becomes:
//   public TopSlowView(TopSlowPresenter presenter, string durationUnit, UiState? ui = null, string? uiPath = null)
// in ctor:
//   _ui = ui ?? new UiState(); _uiPath = uiPath;
//   _visible = _ui.Views.TryGetValue("topSlow", out var vl) && vl.Columns.Length > 0 ? vl.Columns : Catalog;
// in KeyDown: else if (key == Keys.Columns) { ChooseColumns(); key.Handled = true; }

private void ChooseColumns()
{
    var chosen = ColumnChooser.Show(Catalog, _visible);
    if (chosen is null) return;
    _visible = chosen.ToArray();
    _ui.Views["topSlow"] = new UiState.ViewLayout(_visible, _p.SortColumn);
    if (_uiPath is not null) _ui.Save(_uiPath);     // persist only when a path was supplied
    Reload();
}
```

In `Reload`, build the table from `_visible` (map each visible column name to its value selector; a hidden column is simply not added). Update the Task 9 `MainWindow.Show` index-1 branch to pass the extra args: `new TopSlowView(presenter, _ctx.Config.DurationUnit, _ctx.Ui, _ctx.UiStatePath)`.

> **Implementer note:** verify `CheckBox.CheckedState`/`CheckState`, `Dialog.AddButton`, `Button.Accepting` against TG 2.4.6. The only unit-tested part is `ColumnChooser.Apply` (pure); `Show` is exercised manually. Keep a value-selector map so a hidden column simply isn't added to the `DataTable`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ColumnChooserTests` then full `dotnet test`
Expected: PASS; no regressions.

- [ ] **Step 5: Manual end-to-end against the real sample trace**

```bash
# import a real workload trace, then explore in the TUI
dotnet run --project src/SqlFerret.Cli -- import sample/performances_0_134262655313690000.xel --project /tmp/tui.duckdb
dotnet run --project src/SqlFerret.Tui -- /tmp/tui.duckdb
# In the TUI: Top Slow renders; s cycles sort; / filters; C chooses columns (persists to sqlferret.ui.json);
# Enter drills; c copies build-for-SSMS (clipboard or .sql fallback path shown); Esc back; q quits.
# Also run the Import view directly against a .xel and watch live counters. Then:
rm -f /tmp/tui.duckdb
```

Document the observed result in the commit message. Confirm nothing under `sample/` and no `.duckdb` got staged.

- [ ] **Step 6: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(tui): column chooser + UiState persistence; MVP TUI end-to-end"
```

---

## Notes for the executor

- **Terminal.Gui 2.4.6 API drift is the main risk.** Every TG-facing task carries an implementer note: the *contract* (layout/behavior) is fixed; adapt member names to the installed API, exactly as Plan 1 adapted XELite's `ReadEventStream` and DuckDB.NET's parameter naming. The presenter layer (Tasks 5-7) has no TG dependency and is fully unit-tested regardless.
- **Test discipline:** real logic lives in presenters (headless tests) + the two Core additions (Core tests). View tasks add a thin render/wiring test under `FakeDriver`; `.xel`-touching tests are `[SkippableFact]` against the gitignored `sample/`.
- **No Core analysis logic leaks into the TUI.** If a view needs data the presenters/`WorkloadQueries` don't expose, add a `WorkloadQueries` method (Core) rather than SQL in the host.
- **On exit**, `Program.cs` saves `UiState`. Never commit `sample/` data or `.duckdb` files.
```
