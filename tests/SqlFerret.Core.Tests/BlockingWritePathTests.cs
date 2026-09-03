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
