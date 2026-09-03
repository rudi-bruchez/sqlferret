// tests/SqlFerret.Core.Tests/HealthStorageTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthStorageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static long Count(DuckDbProject db, string table)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt64(c.ExecuteScalar());
    }

    [Fact]
    public void A_cycle_and_its_children_are_persisted()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var qp = new ServerDiagnosticsSample(at, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed,
            [new HealthMetric("maxWorkers", null, 9600, null),
             new HealthMetric("trackingNonYieldingScheduler", null, null, "0x0")],
            [new HealthWait(false, "byCount", "CXPACKET", 1000, 2000, 5_026_000)],
            [new HealthCpuRequest(53, 0, "SELECT", 1_200_000, 12.5, "0x1F")],
            [new HealthPendingTask("WidgetRecalc", 3)], [], [], []);
        var io = new ServerDiagnosticsSample(at.AddTicks(2393), "IO_SUBSYSTEM", "CLEAN",
            DiagnosticsOutcome.Parsed, [], [], [], [],
            [new HealthPendingIo(900_000, "X:/AppDb/AppDb.mdf", "0x5", 4096)], [], []);
        var mem = new ServerDiagnosticsSample(at.AddTicks(619), "RESOURCE", "CLEAN",
            DiagnosticsOutcome.Parsed, [], [], [], [], [],
            [new HealthMemoryEntry("Process/System Counts", "Value", "Available Physical Memory", 8000, null)], []);
        var unhandled = new ServerDiagnosticsSample(at.AddTicks(3000), "events", "UNKNOWN",
            DiagnosticsOutcome.Unhandled, [], [], [], [], [], [], []);

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [new HealthCycle(at, "34", [qp, io, mem, unhandled])]);

            Assert.Equal(1L, Count(db, "health_cycles"));
            Assert.Equal(4L, Count(db, "health_samples"));
            Assert.Equal(2L, Count(db, "health_metrics"));
            Assert.Equal(1L, Count(db, "health_waits"));
            Assert.Equal(1L, Count(db, "health_cpu_requests"));
            Assert.Equal(1L, Count(db, "health_pending_tasks"));
            Assert.Equal(1L, Count(db, "health_pending_io"));
            Assert.Equal(1L, Count(db, "health_memory_entries"));

            using var c = db.Connection.CreateCommand();

            // Un composant non gere garde sa ligne, sans enfants : rien n'est perdu en silence.
            c.CommandText = "SELECT handled FROM health_samples WHERE component = 'events'";
            Assert.False((bool)c.ExecuteScalar()!);

            c.CommandText = "SELECT value_big FROM health_metrics WHERE name = 'maxWorkers'";
            Assert.Equal(9600L, Convert.ToInt64(c.ExecuteScalar()));

            c.CommandText = "SELECT value_text FROM health_metrics WHERE name = 'trackingNonYieldingScheduler'";
            Assert.Equal("0x0", (string?)c.ExecuteScalar());

            // Toutes les lignes du cycle pointent vers le meme cycle_id, et il porte la serie.
            c.CommandText = """
              SELECT count(*) FROM health_samples s
              JOIN health_cycles y ON y.cycle_id = s.cycle_id WHERE y.series_key = '34'
              """;
            Assert.Equal(4L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reopening_the_project_does_not_collide_on_ids()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        static ServerDiagnosticsSample S(DateTime t) =>
            new(t, "SYSTEM", "CLEAN", DiagnosticsOutcome.Parsed, [], [], [], [], [], [], []);

        var path = TempDb();
        try
        {
            using (var db = DuckDbProject.Open(path))
                db.InsertHealthCycles(db.BeginRun("logs/", 1, 0, "masked"),
                    [new HealthCycle(at, "34", [S(at)])]);
            using (var db = DuckDbProject.Open(path))
                db.InsertHealthCycles(db.BeginRun("logs/", 1, 0, "masked"),
                    [new HealthCycle(at.AddMinutes(5), "34", [S(at.AddMinutes(5))])]);

            using (var db = DuckDbProject.Open(path))
            {
                Assert.Equal(2L, Count(db, "health_cycles"));
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(DISTINCT cycle_id) FROM health_cycles";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
                c.CommandText = "SELECT count(DISTINCT sample_id) FROM health_samples";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
