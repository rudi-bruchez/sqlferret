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
}
