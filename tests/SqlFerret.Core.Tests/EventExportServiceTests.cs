using SqlFerret.Core.Analysis;
using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;

public class EventExportServiceTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"exp_{Guid.NewGuid():N}");
    private static void Exec(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
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

    [Fact]
    public void Export_reports_matched_count_so_limit_truncation_is_visible()
    {
        var path = TempDb();
        var outDir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            // 5 exportable blocking reports + 2 redacted (raw_xml NULL).
            for (int i = 1; i <= 5; i++)
                Exec(db, $"INSERT INTO blocking_reports VALUES ({i},1, TIMESTAMP '2026-06-0{i} 10:00:00', 1, 7, '<r>{i}</r>')");
            Exec(db, "INSERT INTO blocking_reports VALUES (6,1, TIMESTAMP '2026-06-06 10:00:00', 1, 7, NULL)");
            Exec(db, "INSERT INTO blocking_reports VALUES (7,1, TIMESTAMP '2026-06-07 10:00:00', 1, 7, NULL)");

            var res = new EventExportService(db.Connection).Export(new EventExportOptions(
                outDir, EventKind.Blocking, new QueryStoreWindow(null, null), null, null, 3));

            Assert.Equal(3, res.BlockingWritten);   // capped by limit
            Assert.Equal(5, res.BlockingMatched);   // total exportable, ignoring the limit
            Assert.Equal(2, res.BlockingSkipped);   // redacted/absent
            Assert.True(res.BlockingWritten < res.BlockingMatched, "truncation must be visible via Matched");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void Export_limit_is_deterministic_when_timestamps_tie()
    {
        var path = TempDb();
        var outDir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            // Three deadlocks at the SAME captured_at, inserted out of report_id order.
            Exec(db, "INSERT INTO deadlock_reports VALUES (3,1, TIMESTAMP '2026-06-01 10:00:00', 'v3', 'p3', '<d>3</d>')");
            Exec(db, "INSERT INTO deadlock_reports VALUES (1,1, TIMESTAMP '2026-06-01 10:00:00', 'v1', 'p1', '<d>1</d>')");
            Exec(db, "INSERT INTO deadlock_reports VALUES (2,1, TIMESTAMP '2026-06-01 10:00:00', 'v2', 'p2', '<d>2</d>')");

            var res = new EventExportService(db.Connection).Export(new EventExportOptions(
                outDir, EventKind.Deadlock, new QueryStoreWindow(null, null), null, null, 2));

            Assert.Equal(2, res.DeadlockWritten);
            Assert.Equal(3, res.DeadlockMatched);
            // Tiebreaker is report_id ASC, so ids 1 and 2 are exported deterministically, never 3.
            var ids = Directory.GetFiles(outDir, "deadlock_*.xdl")
                .Select(f => Path.GetFileNameWithoutExtension(f).Split('_').Last())
                .OrderBy(s => s).ToArray();
            Assert.Equal(["1", "2"], ids);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
}
