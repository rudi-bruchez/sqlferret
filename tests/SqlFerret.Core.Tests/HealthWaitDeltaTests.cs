// tests/SqlFerret.Core.Tests/HealthWaitDeltaTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthWaitDeltaTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample Qp(DateTime ts, params HealthWait[] waits) =>
        new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed, [], waits, [], [], [], [], []);

    private static HealthWait W(string type, long waits, string ranking = "byCount") =>
        new(false, ranking, type, waits, 2_000, 5_026_000);

    [Fact]
    public void A_cumulative_counter_yields_a_delta_and_a_rate_not_a_sum()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "34", [Qp(at,                W("CXPACKET", 1_000_000))]),
                new HealthCycle(at.AddMinutes(5),  "34", [Qp(at.AddMinutes(5),  W("CXPACKET", 1_000_600))]),
                new HealthCycle(at.AddMinutes(10), "34", [Qp(at.AddMinutes(10), W("CXPACKET", 1_001_000))]),
            ]);

            var d = Assert.Single(new HealthQueries(db.Connection).WaitDeltas("34"));

            Assert.Equal("CXPACKET", d.WaitType);
            Assert.Equal(1_000L, d.WaitsDelta);      // 1 001 000 - 1 000 000, pas la somme
            Assert.Equal(10d, d.SpanMinutes, 3);
            Assert.Equal(100d, d.PerMinute, 3);
            Assert.Equal(0L, d.RestartIntervalsDropped);
            // Les deux colonnes de temps sont des chiffres depuis le demarrage de l'instance,
            // transportes tels quels : aucun classement ne s'ordonne dessus.
            Assert.Equal(5_026_000L, d.LifetimeMaxWaitUs);
            Assert.Equal(2_000L, d.LifetimeAvgWaitUs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_wait_type_that_leaves_the_top_list_reports_the_span_it_actually_covers()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "34", [Qp(at,                W("LCK_M_IX", 500))]),
                new HealthCycle(at.AddMinutes(5),  "34", [Qp(at.AddMinutes(5),  W("LCK_M_IX", 700))]),
                new HealthCycle(at.AddMinutes(60), "34", [Qp(at.AddMinutes(60), W("CXPACKET", 10))]),
            ]);

            var lck = new HealthQueries(db.Connection).WaitDeltas("34")
                .Single(x => x.WaitType == "LCK_M_IX");

            Assert.Equal(5d, lck.SpanMinutes, 3);   // 5 min, pas les 60 de la fenetre
            Assert.Equal(200L, lck.WaitsDelta);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_falling_counter_means_a_restart_and_the_interval_is_dropped_and_counted()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "34", [Qp(at,                W("CXPACKET", 5_000))]),
                new HealthCycle(at.AddMinutes(5),  "34", [Qp(at.AddMinutes(5),  W("CXPACKET", 10))]),
                new HealthCycle(at.AddMinutes(10), "34", [Qp(at.AddMinutes(10), W("CXPACKET", 60))]),
            ]);

            var d = Assert.Single(new HealthQueries(db.Connection).WaitDeltas("34"));

            // L'intervalle du redemarrage est ecarte, pas compense : seul le pas croissant compte.
            Assert.Equal(50L, d.WaitsDelta);
            Assert.Equal(1L, d.RestartIntervalsDropped);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// ScalarDeltas doit ecarter l'intervalle du redemarrage comme WaitDeltas le fait, et non
    /// faire un simple dernier-moins-premier : celui-ci perdrait toute l'activite anterieure au
    /// redemarrage, et un greatest(..., 0) la ferait disparaitre en silence.
    /// </summary>
    [Fact]
    public void Scalar_deltas_keep_the_activity_from_before_an_instance_restart()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        static ServerDiagnosticsSample Sys(DateTime ts, long backoffs) =>
            new(ts, "SYSTEM", "CLEAN", DiagnosticsOutcome.Parsed,
                [new HealthMetric("spinlockBackoffs", null, backoffs, null)],
                [], [], [], [], [], []);

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "1", [Sys(at,                1_000)]),
                new HealthCycle(at.AddMinutes(5),  "1", [Sys(at.AddMinutes(5),  1_400)]),  // +400
                new HealthCycle(at.AddMinutes(10), "1", [Sys(at.AddMinutes(10),    10)]),  // redemarrage
                new HealthCycle(at.AddMinutes(15), "1", [Sys(at.AddMinutes(15),    70)]),  // +60
            ]);

            var d = Assert.Single(new HealthQueries(db.Connection)
                .ScalarDeltas("1", ["spinlockBackoffs"]));

            // 400 avant le redemarrage + 60 apres. Un dernier-moins-premier aurait rendu 0.
            Assert.Equal(460L, d.Delta);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Only_the_by_count_ranking_is_returned()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at, "34", [Qp(at,
                    W("CXPACKET", 100), W("CXPACKET", 900, "byDuration"))]),
                new HealthCycle(at.AddMinutes(5), "34", [Qp(at.AddMinutes(5),
                    W("CXPACKET", 200), W("CXPACKET", 1900, "byDuration"))]),
            ]);

            var rows = new HealthQueries(db.Connection).WaitDeltas("34");

            // Un seul CXPACKET : melanger les deux classements le ferait apparaitre deux fois
            // dans le top N et gonflerait le classement avec des chiffres incomparables.
            var d = Assert.Single(rows);
            Assert.Equal("byCount", d.Ranking);
            Assert.Equal(100L, d.WaitsDelta);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
