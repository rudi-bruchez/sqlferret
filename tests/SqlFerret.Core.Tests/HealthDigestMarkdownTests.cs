// tests/SqlFerret.Core.Tests/HealthDigestMarkdownTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

/// <summary>
/// Le rendu vit dans l'hote, pas dans Core : l'invariant interdit toute conversion d'unite dans
/// Core, et BlockingDigestMarkdown etablit deja ce patron.
/// </summary>
public class HealthDigestMarkdownTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static ServerDiagnosticsSample Qp(DateTime ts, long counter) =>
        new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed,
            [new HealthMetric("pendingTasks", null, 3, null),
             new HealthMetric("spinlockBackoffs", null, counter, null)],
            [new HealthWait(false, "byCount", "CXPACKET", counter, 2_000, 5_026_000)],
            [], [], [], [], []);

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

            var md = SqlFerret.Cli.HealthDigestMarkdown.Render(new HealthDigest(db.Connection).Build());

            var coverageAt = md.IndexOf("## Coverage", StringComparison.Ordinal);
            var waitsAt = md.IndexOf("## Waits", StringComparison.Ordinal);
            Assert.True(coverageAt >= 0 && waitsAt > coverageAt, "Coverage doit venir en premier");
            Assert.Contains("since instance start", md, StringComparison.OrdinalIgnoreCase);
            // Les durees sont rendues par DisplayFormat, dans l'hote : « ms », jamais un nombre nu.
            Assert.Contains(" ms", md, StringComparison.Ordinal);
            // Les sept sections de la spec §9 sont toutes rendues, pas seulement celles qui ont des
            // donnees : une section absente se lirait comme une section sans probleme.
            foreach (var h in new[] { "## Non-clean states", "## Waits", "## Memory",
                                      "## Worker pressure", "## Stability signals",
                                      "## Worst pending I/O", "## I/O",
                                      "## Blocking seen in diagnostics cycles" })
                Assert.Contains(h, md, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
