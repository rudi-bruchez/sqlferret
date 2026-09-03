// tests/SqlFerret.Core.Tests/HealthQueriesCoverageTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthQueriesCoverageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample S(DateTime ts) =>
        new(ts, "SYSTEM", "CLEAN", DiagnosticsOutcome.Parsed, [], [], [], [], [], [], []);

    [Fact]
    public void Coverage_reports_cycles_span_median_and_the_largest_gap()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            // 0, 5, 10, puis un trou de 40 min, puis 50
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "34", [S(at)]),
                new HealthCycle(at.AddMinutes(5),  "34", [S(at.AddMinutes(5))]),
                new HealthCycle(at.AddMinutes(10), "34", [S(at.AddMinutes(10))]),
                new HealthCycle(at.AddMinutes(50), "34", [S(at.AddMinutes(50))]),
            ]);

            var cov = new HealthQueries(db.Connection).Coverage();

            Assert.Equal(4L, cov.Cycles);
            Assert.Equal(at, cov.First);
            Assert.Equal(50d, cov.SpanMinutes, 3);
            Assert.Equal(5d, cov.MedianIntervalMin, 3);
            Assert.Equal(40d, cov.LargestGapMin, 3);
            Assert.Single(cov.Series);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Two_series_are_reported_separately()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var b = new DateTime(2026, 9, 3, 0, 52, 06, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            List<HealthCycle> cycles = [];
            for (int i = 0; i < 3; i++)
            {
                cycles.Add(new HealthCycle(a.AddMinutes(5 * i), "34", [S(a.AddMinutes(5 * i))]));
                cycles.Add(new HealthCycle(b.AddMinutes(5 * i), "06", [S(b.AddMinutes(5 * i))]));
            }
            db.InsertHealthCycles(runId, cycles);

            var cov = new HealthQueries(db.Connection).Coverage();

            Assert.Equal(2, cov.Series.Count);
            Assert.All(cov.Series, s => Assert.Equal(3L, s.Cycles));
            // Chaque serie garde sa propre cadence : 5 min, pas les 2,5 min de leur entrelacement.
            Assert.All(cov.Series, s => Assert.Equal(5d, s.MedianIntervalMin, 3));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void An_empty_project_reports_zero_rather_than_throwing()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);

            var cov = new HealthQueries(db.Connection).Coverage();

            Assert.Equal(0L, cov.Cycles);
            Assert.Null(cov.First);
            Assert.Empty(cov.Series);
            Assert.Equal(0d, cov.NonDiagnosticsShare, 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// La part d'evenements qui ne sont pas des diagnostics : sur la capture mesuree, 98,8 %.
    /// C'est ce chiffre qui dit au lecteur ce que vaut le reste du digest.
    /// </summary>
    [Fact]
    public void The_non_diagnostics_share_comes_from_the_ingestion_counters()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [new HealthCycle(at, "34", [S(at)])]);
            // 100 evenements lus, 20 de diagnostics : 80 % ne le sont pas.
            db.FinishRun(runId, read: 100, mapped: 80, unmapped: 0, cleaned: 0, tokenizeFailures: 0,
                blocking: 0, deadlocks: 0, blockingParseFailures: 0,
                serverDiagnostics: 18, serverDiagnosticsUnhandled: 1,
                serverDiagnosticsParseFailures: 1);

            var cov = new HealthQueries(db.Connection).Coverage();

            Assert.Equal(0.80d, cov.NonDiagnosticsShare, 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
