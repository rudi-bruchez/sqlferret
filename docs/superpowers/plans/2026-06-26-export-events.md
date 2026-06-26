# Export-events Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL. Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

Goal: add an MCP-callable `export-events` CLI command that extracts raw blocked-process and deadlock-graph XML over a time window (or by fingerprint/database) into a directory, one file per event plus an `index.json` manifest.

Architecture: persist the raw blocked-process XML at ingestion (new `blocking_reports.raw_xml` column, gated on `redaction=off`, mirroring the existing `deadlock_reports.graph_xml`). A new Core service `EventExportService` selects events in SQL, writes one file per event, and returns counts. A thin CLI command wires flags to the service and prints a JSON summary.

Tech stack: .NET 10 / C# 14, DuckDB.NET.Data.Full, System.Text.Json, xUnit.

## Global Constraints

- Target framework `net10.0`, Nullable + ImplicitUsings on, LangVersion latest. Zero build warnings expected.
- Microseconds stay in Core; no unit conversion here (this feature does not touch durations).
- Redaction before disk: raw XML is stored only when the import ran with `RedactionMode.Off`. Otherwise `blocking_reports.raw_xml` is `NULL` and `deadlock_reports.graph_xml` is `'<redacted/>'`.
- SQL safety: every user-supplied value (`from`, `to`, `database`, `fingerprint`, `limit`) is a bound parameter (`$name`). Only fixed column names and the `kind` discriminator are interpolated. The `--out` directory rejects any path segment equal to `..`.
- KISS: selection and counting live in DuckDB SQL, not in C# reduction loops. No new abstractions, DI, or interfaces.
- Aggregation result types are plain `record` / `enum`. Services use primary constructors.
- TDD: red, green, commit per change. `git` is wrapped with `rtk`; commits are co-authored.

Reference spec: `docs/superpowers/specs/2026-06-26-export-events-design.md`.

---

## File structure

- `src/SqlFerret.Core/Storage/DuckDbProject.cs` (modify): add `raw_xml` to the `blocking_reports` schema, add the idempotent migration, add the `$raw` bind in `InsertBlockingBatch`.
- `src/SqlFerret.Core/Storage/PreparedBlocking.cs` (modify): add `RawXml` to `PreparedBlockingReport`.
- `src/SqlFerret.Core/Ingestion/IngestionService.cs` (modify): compute the gated raw XML, thread it through `Prepare`.
- `src/SqlFerret.Core/Analysis/EventExport.cs` (create): `EventKind` enum, `EventExportOptions`, `EventExportResult`, `EventExportManifestEntry`, `EventExportService`.
- `src/SqlFerret.Cli/Program.cs` (modify): add the `export-events` case and update the usage line.
- `tests/SqlFerret.Core.Tests/EventExportSchemaTests.cs` (create): schema + migration.
- `tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs` (modify): raw-XML persistence gating.
- `tests/SqlFerret.Core.Tests/EventExportServiceTests.cs` (create): export service behavior.

---

## Task 1: raw_xml column and idempotent migration

Files:
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (schema block near lines 52-67)
- Test: `tests/SqlFerret.Core.Tests/EventExportSchemaTests.cs` (create)

Interfaces:
- Produces: a `blocking_reports` table with a trailing nullable `raw_xml TEXT` column, present on both freshly created and pre-existing project files.

- [ ] Step 1: Write the failing test

Create `tests/SqlFerret.Core.Tests/EventExportSchemaTests.cs`:

```csharp
using SqlFerret.Core.Storage;

public class EventExportSchemaTests
{
    [Fact]
    public void Blocking_reports_has_raw_xml_column_and_reopen_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT raw_xml FROM blocking_reports LIMIT 0";
                using var r = c.ExecuteReader();   // throws if the column is missing
                Assert.Equal("raw_xml", r.GetName(0));
            }
            // Re-open: the ADD COLUMN IF NOT EXISTS migration must not throw on an existing DB.
            using (DuckDbProject.Open(path)) { }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] Step 2: Run the test to verify it fails

Run: `rtk dotnet test --filter FullyQualifiedName~EventExportSchemaTests`
Expected: FAIL. The reader throws because `raw_xml` does not exist (message mentions `raw_xml`).

- [ ] Step 3: Add the column to the CREATE statement

In `DuckDbProject.cs`, change the `blocking_reports` create (lines 52-54) to add the column:

```csharp
        CREATE TABLE IF NOT EXISTS blocking_reports (
          report_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP,
          monitor_loop INTEGER, database_id INTEGER, raw_xml TEXT);
```

- [ ] Step 4: Add the idempotent migration for existing project files

Immediately after `cmd.ExecuteNonQuery();` (line 67, before `CreateQdsSchema(conn);`), insert:

```csharp
        using (var migrate = conn.CreateCommand())
        {
            // Existing projects were created before raw_xml existed; CREATE TABLE IF NOT EXISTS
            // is a no-op for them, so add the column here. No-op when already present.
            migrate.CommandText = "ALTER TABLE blocking_reports ADD COLUMN IF NOT EXISTS raw_xml TEXT;";
            migrate.ExecuteNonQuery();
        }
```

- [ ] Step 5: Run the test to verify it passes

Run: `rtk dotnet test --filter FullyQualifiedName~EventExportSchemaTests`
Expected: PASS.

- [ ] Step 6: Commit

```bash
rtk git add src/SqlFerret.Core/Storage/DuckDbProject.cs tests/SqlFerret.Core.Tests/EventExportSchemaTests.cs
rtk git commit -m "feat(core): add blocking_reports.raw_xml column + idempotent migration

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: persist raw blocking XML at ingestion (gated on redaction=off)

Files:
- Modify: `src/SqlFerret.Core/Storage/PreparedBlocking.cs:7`
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` (`InsertBlockingBatch`, lines 201-219)
- Modify: `src/SqlFerret.Core/Ingestion/IngestionService.cs` (Blocked branch lines 35-42, `Prepare` lines 77-80)
- Test: `tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs` (add two tests)

Interfaces:
- Consumes: `EventMapper.ExtractBlockingXml(ev)` returns the raw `<blocked-process-report>` string or null (existing).
- Produces: `PreparedBlockingReport(BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking, string? RawXml = null)`. After ingestion, `blocking_reports.raw_xml` holds the captured XML when `RedactionMode.Off`, else `NULL`.

- [ ] Step 1: Write the failing tests

Append to `tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs` (inside the class). These reuse the same XML and `FakeEvent` style as the existing `Ingest_routes_blocking_report_and_counts_it` test:

```csharp
    private const string SampleBlockedXml =
        "<blocked-process-report monitorLoop=\"1\">" +
        "<blocked-process><process spid=\"201\" waitresource=\"OBJECT: 5:99:0\" waittime=\"5000\">" +
        "<inputbuf>update dbo.Y set a=1 where id=2</inputbuf></process></blocked-process>" +
        "<blocking-process><process spid=\"118\"><inputbuf>select 1</inputbuf></process></blocking-process>" +
        "</blocked-process-report>";

    [Fact]
    public void Ingest_stores_raw_blocking_xml_when_redaction_off()
    {
        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = SampleBlockedXml },
                new Dictionary<string, object?>());

            new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Off, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT raw_xml FROM blocking_reports";
            Assert.Equal(SampleBlockedXml, (string)c.ExecuteScalar()!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ingest_nulls_raw_blocking_xml_when_redaction_masked()
    {
        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = SampleBlockedXml },
                new Dictionary<string, object?>());

            new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Masked, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT raw_xml FROM blocking_reports";
            Assert.True(c.ExecuteReader() is var r && r.Read() && r.IsDBNull(0));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

- [ ] Step 2: Run the tests to verify they fail

Run: `rtk dotnet test --filter "FullyQualifiedName~Ingest_stores_raw_blocking_xml_when_redaction_off|FullyQualifiedName~Ingest_nulls_raw_blocking_xml_when_redaction_masked"`
Expected: FAIL to compile or assert. `blocking_reports` has no `raw_xml` value bound yet, so the column is `NULL` even under `Off`, and the first test fails on the equality assertion.

- [ ] Step 3: Add RawXml to PreparedBlockingReport

In `src/SqlFerret.Core/Storage/PreparedBlocking.cs`, change line 7:

```csharp
public record PreparedBlockingReport(BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking, string? RawXml = null);
```

- [ ] Step 4: Thread raw XML through ingestion

In `src/SqlFerret.Core/Ingestion/IngestionService.cs`, the Blocked branch (lines 35-42) currently reads:

```csharp
                if (bkind == BlockingEventKind.Blocked)
                {
                    var xml = EventMapper.ExtractBlockingXml(ev);
                    var rep = xml is null ? null : BlockingReportParser.Parse(xml, ev.Timestamp);
                    if (rep is null) { blockingParseFailures++; continue; }
                    project.InsertBlockingBatch(runId, [Prepare(rep)]);
                    blocking++;
                }
```

Replace it with (gate the raw XML on redaction, pass it to `Prepare`):

```csharp
                if (bkind == BlockingEventKind.Blocked)
                {
                    var xml = EventMapper.ExtractBlockingXml(ev);
                    var rep = xml is null ? null : BlockingReportParser.Parse(xml, ev.Timestamp);
                    if (rep is null) { blockingParseFailures++; continue; }
                    // Mirror deadlock gating: keep the raw XML only when nothing is redacted.
                    var rawXml = options.Redaction == RedactionMode.Off ? xml : null;
                    project.InsertBlockingBatch(runId, [Prepare(rep, rawXml)]);
                    blocking++;
                }
```

Then update `Prepare` (lines 77-80):

```csharp
    private PreparedBlockingReport Prepare(BlockingReport rep, string? rawXml)
    {
        return new PreparedBlockingReport(rep, PrepareProc(rep.Blocked), PrepareProc(rep.Blocking), rawXml);
    }
```

- [ ] Step 5: Bind raw_xml in InsertBlockingBatch

In `src/SqlFerret.Core/Storage/DuckDbProject.cs`, inside `InsertBlockingBatch` (lines 207-213), change the insert statement and add the bind:

```csharp
                c.Transaction = tx;
                c.CommandText = "INSERT INTO blocking_reports VALUES ($id,$run,$ts,$loop,$db,$raw)";
                Add(c, "$id", id); Add(c, "$run", runId); Add(c, "$ts", pr.Report.CapturedAt);
                Add(c, "$loop", (object?)pr.Report.MonitorLoop); Add(c, "$db", (object?)pr.Report.DatabaseId);
                Add(c, "$raw", (object?)pr.RawXml);
                c.ExecuteNonQuery();
```

- [ ] Step 6: Run the tests to verify they pass

Run: `rtk dotnet test --filter "FullyQualifiedName~Ingest_stores_raw_blocking_xml_when_redaction_off|FullyQualifiedName~Ingest_nulls_raw_blocking_xml_when_redaction_masked"`
Expected: PASS (both).

- [ ] Step 7: Commit

```bash
rtk git add src/SqlFerret.Core/Storage/PreparedBlocking.cs src/SqlFerret.Core/Storage/DuckDbProject.cs src/SqlFerret.Core/Ingestion/IngestionService.cs tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs
rtk git commit -m "feat(core): persist raw blocked-process XML when redaction=off

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: EventExportService with the deadlock path

Files:
- Create: `src/SqlFerret.Core/Analysis/EventExport.cs`
- Test: `tests/SqlFerret.Core.Tests/EventExportServiceTests.cs` (create)

Interfaces:
- Produces:
  - `enum EventKind { Blocking, Deadlock, Both }`
  - `record EventExportOptions(string OutDir, EventKind Kind, QueryStoreWindow Window, string? Fingerprint, int? DatabaseId, int Limit)`
  - `record EventExportResult(int BlockingWritten, int BlockingSkipped, int DeadlockWritten, int DeadlockSkipped, string OutDir, string IndexPath)`
  - `class EventExportService(DuckDBConnection conn)` with `EventExportResult Export(EventExportOptions opts, IProgress<string>? progress = null)`
- Consumes: `QueryStoreWindow` from `SqlFerret.Core.Server` (nullable `From`/`To`).

- [ ] Step 1: Write the failing test

Create `tests/SqlFerret.Core.Tests/EventExportServiceTests.cs`:

```csharp
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;

public class EventExportServiceTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"exp_{Guid.NewGuid():N}");
    private static void Exec(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery();
    }

    [Fact]
    public void Export_writes_deadlock_xml_skips_redacted_and_builds_manifest()
    {
        var path = TempDb();
        var outDir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            Exec(db, "INSERT INTO deadlock_reports VALUES (1,1, TIMESTAMP '2026-06-01 10:00:00', 'v1', 'p1', '<deadlock>A</deadlock>')");
            Exec(db, "INSERT INTO deadlock_reports VALUES (2,1, TIMESTAMP '2026-06-02 10:00:00', 'v2', 'p2', '<redacted/>')");

            var svc = new EventExportService(db.Connection);
            var res = svc.Export(new EventExportOptions(
                outDir, EventKind.Deadlock, new QueryStoreWindow(null, null), null, null, 100));

            Assert.Equal(1, res.DeadlockWritten);
            Assert.Equal(1, res.DeadlockSkipped);

            var files = Directory.GetFiles(outDir, "deadlock_*.xdl");
            Assert.Single(files);
            Assert.Equal("<deadlock>A</deadlock>", File.ReadAllText(files[0]));

            var index = File.ReadAllText(Path.Combine(outDir, "index.json"));
            Assert.Contains("\"kind\": \"deadlock\"", index);
            Assert.Contains("\"victim_spids\": \"v1\"", index);
            Assert.DoesNotContain("v2", index);   // redacted one is not in the manifest
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void Export_respects_time_window_for_deadlocks()
    {
        var path = TempDb();
        var outDir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            Exec(db, "INSERT INTO deadlock_reports VALUES (1,1, TIMESTAMP '2026-06-01 10:00:00', 'v1', 'p1', '<deadlock>early</deadlock>')");
            Exec(db, "INSERT INTO deadlock_reports VALUES (2,1, TIMESTAMP '2026-06-10 10:00:00', 'v2', 'p2', '<deadlock>late</deadlock>')");

            var svc = new EventExportService(db.Connection);
            var window = new QueryStoreWindow(new DateTime(2026, 6, 5), new DateTime(2026, 6, 15));
            var res = svc.Export(new EventExportOptions(
                outDir, EventKind.Deadlock, window, null, null, 100));

            Assert.Equal(1, res.DeadlockWritten);
            var files = Directory.GetFiles(outDir, "deadlock_*.xdl");
            Assert.Equal("<deadlock>late</deadlock>", File.ReadAllText(Assert.Single(files)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void Export_rejects_path_traversal_in_outdir()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var svc = new EventExportService(db.Connection);
            Assert.Throws<ArgumentException>(() => svc.Export(new EventExportOptions(
                "foo/../bar", EventKind.Both, new QueryStoreWindow(null, null), null, null, 10)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] Step 2: Run the test to verify it fails

Run: `rtk dotnet test --filter FullyQualifiedName~EventExportServiceTests`
Expected: FAIL to compile. `EventExportService` and friends do not exist.

- [ ] Step 3: Create the service with the deadlock path and the traversal guard

Create `src/SqlFerret.Core/Analysis/EventExport.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuckDB.NET.Data;
using SqlFerret.Core.Server;

namespace SqlFerret.Core.Analysis;

public enum EventKind { Blocking, Deadlock, Both }

public sealed record EventExportOptions(
    string OutDir,
    EventKind Kind,
    QueryStoreWindow Window,
    string? Fingerprint,
    int? DatabaseId,
    int Limit);

public sealed record EventExportResult(
    int BlockingWritten, int BlockingSkipped,
    int DeadlockWritten, int DeadlockSkipped,
    string OutDir, string IndexPath);

internal sealed record EventExportManifestEntry(
    long Id, string Kind, string CapturedAt, string File,
    int? DatabaseId = null, string? VictimSpids = null, string? ParticipantSpids = null);

/// <summary>
/// Extracts raw blocked-process / deadlock-graph XML to a directory (one file per event) plus an
/// index.json manifest. Selection runs in SQL with bound parameters; XML is only present for runs
/// imported with redaction=off (otherwise it is skipped and counted).
/// </summary>
public sealed class EventExportService(DuckDBConnection conn)
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public EventExportResult Export(EventExportOptions opts, IProgress<string>? progress = null)
    {
        if (opts.OutDir.Split('/', '\\').Any(seg => seg == ".."))
            throw new ArgumentException("--out must not contain a path-traversal segment ('..')");

        Directory.CreateDirectory(opts.OutDir);
        var manifest = new List<EventExportManifestEntry>();
        int bWritten = 0, bSkipped = 0, dWritten = 0, dSkipped = 0;

        if (opts.Kind is EventKind.Deadlock or EventKind.Both)
            (dWritten, dSkipped) = ExportDeadlock(opts, manifest, progress);

        var indexPath = Path.Combine(opts.OutDir, "index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(manifest, ManifestJson));

        return new EventExportResult(bWritten, bSkipped, dWritten, dSkipped, opts.OutDir, indexPath);
    }

    private (int written, int skipped) ExportDeadlock(
        EventExportOptions opts, List<EventExportManifestEntry> manifest, IProgress<string>? progress)
    {
        var (where, binds) = BuildWindowWhere(opts.Window, "captured_at");

        int written = 0;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
              SELECT report_id, captured_at, victim_spids, participant_spids, graph_xml
              FROM deadlock_reports
              WHERE {where} AND graph_xml IS NOT NULL AND graph_xml <> '<redacted/>'
              ORDER BY captured_at LIMIT $limit
              """;
            foreach (var (n, v) in binds) Bind(c, n, v);
            Bind(c, "$limit", opts.Limit);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                DateTime ts = r.GetDateTime(1);
                string? victim = r.IsDBNull(2) ? null : r.GetString(2);
                string? parts = r.IsDBNull(3) ? null : r.GetString(3);
                string xml = r.GetString(4);
                string file = $"deadlock_{FileStamp(ts)}_{id}.xdl";
                File.WriteAllText(Path.Combine(opts.OutDir, file), xml);
                manifest.Add(new EventExportManifestEntry(
                    id, "deadlock", IsoStamp(ts), file, VictimSpids: victim, ParticipantSpids: parts));
                written++;
                // NOTE: increment OUTSIDE the null-conditional. `progress?.Report($"... {++written}")`
                // short-circuits the whole expression when progress is null, so ++written never runs.
                progress?.Report($"deadlock {written}");
            }
        }

        int skipped;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
              SELECT count(*) FROM deadlock_reports
              WHERE {where} AND (graph_xml IS NULL OR graph_xml = '<redacted/>')
              """;
            foreach (var (n, v) in binds) Bind(c, n, v);
            skipped = (int)Convert.ToInt64(c.ExecuteScalar());
        }
        return (written, skipped);
    }

    // Builds a WHERE fragment for the optional time window. `col` is a fixed identifier supplied by
    // the caller (never user input); From/To are bound parameters appended only when present.
    private static (string where, List<(string name, object value)> binds) BuildWindowWhere(
        QueryStoreWindow w, string col)
    {
        var clauses = new List<string> { "1=1" };
        var binds = new List<(string, object)>();
        if (w.From is { } f) { clauses.Add($"{col} >= $from"); binds.Add(("$from", f)); }
        if (w.To is { } t) { clauses.Add($"{col} < $to"); binds.Add(("$to", t)); }
        return (string.Join(" AND ", clauses), binds);
    }

    private static void Bind(System.Data.IDbCommand c, string name, object value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value;
        c.Parameters.Add(p);
    }

    private static string FileStamp(DateTime ts) =>
        ts.ToString("yyyyMMddTHHmmssfff'Z'", CultureInfo.InvariantCulture);
    private static string IsoStamp(DateTime ts) =>
        ts.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
```

- [ ] Step 4: Run the test to verify it passes

Run: `rtk dotnet test --filter FullyQualifiedName~EventExportServiceTests`
Expected: PASS (all three tests).

- [ ] Step 5: Commit

```bash
rtk git add src/SqlFerret.Core/Analysis/EventExport.cs tests/SqlFerret.Core.Tests/EventExportServiceTests.cs
rtk git commit -m "feat(core): EventExportService deadlock XML export + manifest

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: EventExportService blocking path with fingerprint and database filters

Files:
- Modify: `src/SqlFerret.Core/Analysis/EventExport.cs` (add `ExportBlocking`, call it from `Export`)
- Test: `tests/SqlFerret.Core.Tests/EventExportServiceTests.cs` (add tests)

Interfaces:
- Consumes: `blocking_reports(report_id, run_id, captured_at, monitor_loop, database_id, raw_xml)` and `blocking_processes(report_id, role, ..., inputbuf_fingerprint)` from Tasks 1-2.
- Produces: `EventExportResult.BlockingWritten` / `BlockingSkipped` populated; blocking files named `blocking_<utc>_<id>.xml`.

- [ ] Step 1: Write the failing tests

Append to `EventExportServiceTests.cs` (inside the class). Note the `blocking_processes` insert has 18 columns matching the schema order:

```csharp
    [Fact]
    public void Export_writes_blocking_xml_and_skips_null_raw()
    {
        var path = TempDb();
        var outDir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            Exec(db, "INSERT INTO blocking_reports VALUES (1,1, TIMESTAMP '2026-06-01 10:00:00', 1, 7, '<blocked-process-report>A</blocked-process-report>')");
            Exec(db, "INSERT INTO blocking_reports VALUES (2,1, TIMESTAMP '2026-06-02 10:00:00', 1, 8, NULL)");

            var svc = new EventExportService(db.Connection);
            var res = svc.Export(new EventExportOptions(
                outDir, EventKind.Blocking, new QueryStoreWindow(null, null), null, null, 100));

            Assert.Equal(1, res.BlockingWritten);
            Assert.Equal(1, res.BlockingSkipped);

            var files = Directory.GetFiles(outDir, "blocking_*.xml");
            Assert.Equal("<blocked-process-report>A</blocked-process-report>", File.ReadAllText(Assert.Single(files)));

            var index = File.ReadAllText(Path.Combine(outDir, "index.json"));
            Assert.Contains("\"kind\": \"blocking\"", index);
            Assert.Contains("\"database_id\": 7", index);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void Export_filters_blocking_by_database_and_fingerprint_and_limit()
    {
        var path = TempDb();
        var outDir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            // Two reports in db 7, one in db 9. Report 1 has fingerprint abc; report 2 has fingerprint xyz.
            Exec(db, "INSERT INTO blocking_reports VALUES (1,1, TIMESTAMP '2026-06-01 10:00:00', 1, 7, '<r>one</r>')");
            Exec(db, "INSERT INTO blocking_reports VALUES (2,1, TIMESTAMP '2026-06-02 10:00:00', 1, 7, '<r>two</r>')");
            Exec(db, "INSERT INTO blocking_reports VALUES (3,1, TIMESTAMP '2026-06-03 10:00:00', 1, 9, '<r>three</r>')");
            Exec(db, "INSERT INTO blocking_processes VALUES (1,'blocking',118,NULL,NULL,NULL,'Other',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'abc')");
            Exec(db, "INSERT INTO blocking_processes VALUES (2,'blocking',119,NULL,NULL,NULL,'Other',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'xyz')");

            var svc = new EventExportService(db.Connection);

            // database filter: only db 7 => reports 1 and 2
            var byDb = svc.Export(new EventExportOptions(
                outDir, EventKind.Blocking, new QueryStoreWindow(null, null), null, 7, 100));
            Assert.Equal(2, byDb.BlockingWritten);
            Directory.Delete(outDir, true);

            // fingerprint filter: only report 1
            var byFp = svc.Export(new EventExportOptions(
                outDir, EventKind.Blocking, new QueryStoreWindow(null, null), "abc", null, 100));
            Assert.Equal(1, byFp.BlockingWritten);
            Assert.Equal("<r>one</r>", File.ReadAllText(Assert.Single(Directory.GetFiles(outDir, "blocking_*.xml"))));
            Directory.Delete(outDir, true);

            // limit caps files written (3 eligible, limit 2)
            var limited = svc.Export(new EventExportOptions(
                outDir, EventKind.Blocking, new QueryStoreWindow(null, null), null, null, 2));
            Assert.Equal(2, limited.BlockingWritten);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
```

- [ ] Step 2: Run the tests to verify they fail

Run: `rtk dotnet test --filter "FullyQualifiedName~Export_writes_blocking_xml_and_skips_null_raw|FullyQualifiedName~Export_filters_blocking_by_database_and_fingerprint_and_limit"`
Expected: FAIL. `BlockingWritten` is 0 because `Export` does not run the blocking path yet.

- [ ] Step 3: Wire the blocking path into Export

In `src/SqlFerret.Core/Analysis/EventExport.cs`, inside `Export`, add the blocking branch before the deadlock branch:

```csharp
        if (opts.Kind is EventKind.Blocking or EventKind.Both)
            (bWritten, bSkipped) = ExportBlocking(opts, manifest, progress);

        if (opts.Kind is EventKind.Deadlock or EventKind.Both)
            (dWritten, dSkipped) = ExportDeadlock(opts, manifest, progress);
```

- [ ] Step 4: Implement ExportBlocking

Add this method to `EventExportService` (next to `ExportDeadlock`):

```csharp
    private (int written, int skipped) ExportBlocking(
        EventExportOptions opts, List<EventExportManifestEntry> manifest, IProgress<string>? progress)
    {
        var (where, binds) = BuildWindowWhere(opts.Window, "r.captured_at");
        var extra = "";
        if (opts.DatabaseId is { } db) { extra += " AND r.database_id = $db"; binds.Add(("$db", db)); }
        if (!string.IsNullOrWhiteSpace(opts.Fingerprint))
        {
            extra += " AND EXISTS (SELECT 1 FROM blocking_processes bp " +
                     "WHERE bp.report_id = r.report_id AND bp.inputbuf_fingerprint = $fp)";
            binds.Add(("$fp", opts.Fingerprint!));   // non-null inside the guard
        }

        int written = 0;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
              SELECT r.report_id, r.captured_at, r.database_id, r.raw_xml
              FROM blocking_reports r
              WHERE {where}{extra} AND r.raw_xml IS NOT NULL
              ORDER BY r.captured_at LIMIT $limit
              """;
            foreach (var (n, v) in binds) Bind(c, n, v);
            Bind(c, "$limit", opts.Limit);
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                long id = rd.GetInt64(0);
                DateTime ts = rd.GetDateTime(1);
                int? dbid = rd.IsDBNull(2) ? null : rd.GetInt32(2);
                string xml = rd.GetString(3);
                string file = $"blocking_{FileStamp(ts)}_{id}.xml";
                File.WriteAllText(Path.Combine(opts.OutDir, file), xml);
                manifest.Add(new EventExportManifestEntry(id, "blocking", IsoStamp(ts), file, DatabaseId: dbid));
                written++;
                // NOTE: increment OUTSIDE the null-conditional (see Task 3 note) — `{++written}`
                // inside progress?.Report(...) is skipped entirely when progress is null.
                progress?.Report($"blocking {written}");
            }
        }

        int skipped;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
              SELECT count(*) FROM blocking_reports r
              WHERE {where}{extra} AND r.raw_xml IS NULL
              """;
            foreach (var (n, v) in binds) Bind(c, n, v);
            skipped = (int)Convert.ToInt64(c.ExecuteScalar());
        }
        return (written, skipped);
    }
```

- [ ] Step 5: Run the tests to verify they pass

Run: `rtk dotnet test --filter FullyQualifiedName~EventExportServiceTests`
Expected: PASS (all five tests in the class).

- [ ] Step 6: Commit

```bash
rtk git add src/SqlFerret.Core/Analysis/EventExport.cs tests/SqlFerret.Core.Tests/EventExportServiceTests.cs
rtk git commit -m "feat(core): EventExportService blocking XML export with filters

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: CLI export-events command

Files:
- Modify: `src/SqlFerret.Cli/Program.cs` (usage line near line 42; add a `case "export-events"` in the switch)
- Test: end-to-end integration test in Task 6; CLI glue is verified by a manual smoke run in this task.

Interfaces:
- Consumes: `EventExportService`, `EventExportOptions`, `EventExportResult`, `EventKind` (Task 3-4); `QueryStoreWindow.Parse` (existing); `BlockingDigestMarkdown.HasTraversal` (existing); `OpenProject()`, `Arg(...)`, `NullIfEmpty(...)` (existing in Program.cs).

- [ ] Step 1: Update the usage line

In `src/SqlFerret.Cli/Program.cs`, extend the usage string printed when `args.Length == 0` (line 42) by appending this to the message:

```
 | export-events --project <dir> --out <dir> [--kind blocking|deadlock|both] [--from <dt> --to <dt> | --last <N>{h|d}] [--fingerprint <hash>] [--database <id>] [--limit <n>]
```

- [ ] Step 2: Add the export-events case

In the `switch (args[0])` block, add a new case before the `default:` arm:

```csharp
    case "export-events":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var outDir = Arg("--out");
            if (string.IsNullOrWhiteSpace(outDir))
            { Console.Error.WriteLine("export-events: --out <dir> is required"); return 1; }
            if (SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outDir))
            { Console.Error.WriteLine("export-events: invalid --out path"); return 1; }

            var kindStr = Arg("--kind", "both");
            if (!Enum.TryParse<EventKind>(kindStr, ignoreCase: true, out var kind))
            { Console.Error.WriteLine($"export-events: invalid --kind '{kindStr}'. Valid: blocking, deadlock, both"); return 1; }

            QueryStoreWindow window;
            try
            {
                window = QueryStoreWindow.Parse(
                    NullIfEmpty(Arg("--from", "")), NullIfEmpty(Arg("--to", "")), NullIfEmpty(Arg("--last", "")),
                    DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine($"export-events: {ex.Message}"); return 1; }

            int? dbId = int.TryParse(Arg("--database", ""), out var dv) ? dv : null;
            var fingerprint = NullIfEmpty(Arg("--fingerprint", ""));
            var limit = int.TryParse(Arg("--limit", "100"), out var lv) ? lv : 100;

            if (kind == EventKind.Deadlock && (dbId is not null || fingerprint is not null))
                Console.Error.WriteLine("export-events: --fingerprint/--database are ignored for deadlocks");

            using var db = project.OpenDb();
            var svc = new EventExportService(db.Connection);
            var opts = new EventExportOptions(outDir, kind, window, fingerprint, dbId, limit);

            EventExportResult result;
            try { result = svc.Export(opts); }
            catch (ArgumentException ex) { Console.Error.WriteLine($"export-events: {ex.Message}"); return 1; }

            if (result.BlockingWritten == 0 && result.DeadlockWritten == 0 &&
                (result.BlockingSkipped > 0 || result.DeadlockSkipped > 0))
                Console.Error.WriteLine(
                    "export-events: nothing written; matching runs were imported with redaction != off. " +
                    "Re-import with --redaction off to export XML.");

            var summary = new
            {
                outDir = result.OutDir,
                indexPath = result.IndexPath,
                blocking = new { written = result.BlockingWritten, skipped = result.BlockingSkipped },
                deadlock = new { written = result.DeadlockWritten, skipped = result.DeadlockSkipped },
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
            return 0;
        }
```

- [ ] Step 3: Build

Run: `rtk dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] Step 4: Manual smoke run

Build a tiny project with one deadlock row, then export. Run:

```bash
rm -rf /tmp/ee_proj /tmp/ee_out
dotnet run -c Release --project src/SqlFerret.Cli -- top-slow --project /tmp/ee_proj >/dev/null 2>&1 || true
duckdb /tmp/ee_proj/sqlferret.duckdb -c "INSERT INTO deadlock_reports VALUES (1,1, TIMESTAMP '2026-06-01 10:00:00', 'v1','p1','<deadlock>A</deadlock>');"
dotnet run -c Release --project src/SqlFerret.Cli -- export-events --project /tmp/ee_proj --out /tmp/ee_out --kind deadlock
ls /tmp/ee_out
```

Expected: a single-line JSON summary with `"deadlock":{"written":1,"skipped":0}`, and `/tmp/ee_out` containing one `deadlock_*.xdl` file plus `index.json`. Clean up: `rm -rf /tmp/ee_proj /tmp/ee_out`.

- [ ] Step 5: Commit

```bash
rtk git add src/SqlFerret.Cli/Program.cs
rtk git commit -m "feat(cli): export-events command (blocking/deadlock XML extraction)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: end-to-end ingest-to-export integration test

Files:
- Test: `tests/SqlFerret.Core.Tests/EventExportServiceTests.cs` (add one test)

Interfaces:
- Consumes: `IngestionService` (redaction=off) writing `raw_xml` (Task 2); `EventExportService` reading it (Task 4).

- [ ] Step 1: Write the failing test

Append to `EventExportServiceTests.cs`. It ingests a real `blocked_process_report` event with `RedactionMode.Off`, then exports and checks the file content round-trips. `FakeEvent` lives in the test project (used by other tests):

```csharp
    [Fact]
    public void Ingest_off_then_export_round_trips_blocking_xml_to_file()
    {
        var path = TempDb();
        var outDir = TempDir();
        const string xml =
            "<blocked-process-report monitorLoop=\"1\">" +
            "<blocked-process><process spid=\"201\" waitresource=\"OBJECT: 5:99:0\" waittime=\"5000\">" +
            "<inputbuf>update dbo.Y set a=1 where id=2</inputbuf></process></blocked-process>" +
            "<blocking-process><process spid=\"118\"><inputbuf>select 1</inputbuf></process></blocking-process>" +
            "</blocked-process-report>";
        try
        {
            using var db = DuckDbProject.Open(path);
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = xml },
                new Dictionary<string, object?>());
            new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Off, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            var res = new EventExportService(db.Connection).Export(new EventExportOptions(
                outDir, EventKind.Blocking, new QueryStoreWindow(null, null), null, null, 100));

            Assert.Equal(1, res.BlockingWritten);
            var file = Assert.Single(Directory.GetFiles(outDir, "blocking_*.xml"));
            Assert.Equal(xml, File.ReadAllText(file));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
```

- [ ] Step 2: Run the test to verify it passes

Run: `rtk dotnet test --filter FullyQualifiedName~Ingest_off_then_export_round_trips_blocking_xml_to_file`
Expected: PASS. (Tasks 2 and 4 already implement the behavior; this test ties them together. If it fails, the bug is real, not a missing implementation.)

- [ ] Step 3: Run the full suite

Run: `rtk dotnet test`
Expected: all tests pass, 0 warnings.

- [ ] Step 4: Commit

```bash
rtk git add tests/SqlFerret.Core.Tests/EventExportServiceTests.cs
rtk git commit -m "test(core): end-to-end ingest(off)->export blocking XML round-trip

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Final verification

- [ ] Run `rtk dotnet build` — expect 0 warnings.
- [ ] Run `rtk dotnet test` — expect the full suite green (the one env-gated live-SQL test may skip).
- [ ] Confirm `export-events` appears in the usage line when running the CLI with no args.
