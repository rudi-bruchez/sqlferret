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
            Assert.Equal("QUERY_PROCESSING", r.Component);
            Assert.Equal("WARNING", r.State);
            Assert.Equal(2L, r.Cycles);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Memory_movers_report_the_change_across_the_window_not_the_last_value()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        static HealthMemoryEntry E(string d, double v) =>
            new("Process/System Counts", "Value", d, v, null);
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
        static BlockingProcess Proc(int spid, long? waitUs) =>
            new(spid, 0, "suspended", "KEY: 5:1 (x)", WaitResourceType.Key, null, null, waitUs,
                "S", "read committed (2)", 1, "SampleApp", "WS1", "svc",
                "exec AppSchema.WidgetRecalc @WidgetId=1", "fp_" + spid);

        static PreparedBlockingReport Rep(int loop, string source)
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
