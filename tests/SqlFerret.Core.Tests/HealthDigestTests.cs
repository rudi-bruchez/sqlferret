// tests/SqlFerret.Core.Tests/HealthDigestTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class HealthDigestTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample Qp(DateTime ts, long counter) =>
        new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed,
            [new HealthMetric("pendingTasks", null, 3, null),
             new HealthMetric("spinlockBackoffs", null, counter, null)],
            [new HealthWait(false, "byCount", "CXPACKET", counter, 2_000, 5_026_000)],
            [], [], [], [], []);

    [Fact]
    public void A_project_with_no_health_data_says_so_rather_than_reporting_an_empty_ranking()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);

            var e = new HealthDigest(db.Connection).Build();

            Assert.Equal(HealthDigest.SchemaVersion, e.SchemaVersion);
            Assert.Equal(0L, e.Digest.Coverage.Cycles);
            Assert.Contains(e.Digest.Notes,
                n => n.Contains("no diagnostics", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(e.Digest.TopWaits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void More_than_one_series_is_announced_in_the_notes()
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
                cycles.Add(new HealthCycle(a.AddMinutes(5 * i), "34", [Qp(a.AddMinutes(5 * i), 100 + i)]));
                cycles.Add(new HealthCycle(b.AddMinutes(5 * i), "06", [Qp(b.AddMinutes(5 * i), 200 + i)]));
            }
            db.InsertHealthCycles(runId, cycles);

            var e = new HealthDigest(db.Connection).Build();

            Assert.Equal(2, e.Digest.Coverage.Series.Count);
            Assert.Contains(e.Digest.Notes,
                n => n.Contains("sampling series", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Markdown_leads_with_coverage_and_renders_every_section()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "34", [Qp(at, 1_000)]),
                new HealthCycle(at.AddMinutes(5), "34", [Qp(at.AddMinutes(5), 1_500)]),
            ]);

            var md = HealthDigest.ToMarkdown(new HealthDigest(db.Connection).Build());

            var coverageAt = md.IndexOf("## Coverage", StringComparison.Ordinal);
            var waitsAt = md.IndexOf("## Waits", StringComparison.Ordinal);
            Assert.True(coverageAt >= 0 && waitsAt > coverageAt, "Coverage doit venir en premier");
            Assert.Contains("since instance start", md, StringComparison.OrdinalIgnoreCase);
            // Les sept sections de la spec §9 sont toutes rendues, pas seulement celles qui ont des
            // donnees : une section absente se lirait comme une section sans probleme.
            foreach (var h in new[] { "## Non-clean states", "## Waits", "## Memory",
                                      "## Worker pressure", "## Stability signals",
                                      "## Worst pending I/O", "## Blocking seen in diagnostics cycles" })
                Assert.Contains(h, md, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void An_all_clean_project_says_nothing_moved_rather_than_going_silent()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "34", [Qp(at, 1_000)]),
                new HealthCycle(at.AddMinutes(5), "34", [Qp(at.AddMinutes(5), 1_000)]),
            ]);

            var e = new HealthDigest(db.Connection).Build();

            Assert.Empty(e.Digest.NonCleanStates);
            Assert.Empty(e.Digest.StabilitySignals);
            // Un serveur sain est un resultat, pas une absence de donnees : le digest le dit.
            Assert.Contains(e.Digest.Notes,
                n => n.Contains("expected result", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
