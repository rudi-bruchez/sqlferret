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
            p.InsertQdsRuntimeStats(run, [new QdsRuntimeStatRow(2, 5, 1, new DateTime(2026, 6, 1), new DateTime(2026, 6, 1), "Regular", 1, metrics)]);
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
}
