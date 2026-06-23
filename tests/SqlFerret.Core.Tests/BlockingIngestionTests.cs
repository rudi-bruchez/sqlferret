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
