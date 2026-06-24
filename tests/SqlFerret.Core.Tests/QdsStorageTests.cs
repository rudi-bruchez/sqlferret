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
