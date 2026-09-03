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

    private static ServerDiagnosticsSample Io(DateTime ts, long counter) =>
        new(ts, "IO_SUBSYSTEM", "CLEAN", DiagnosticsOutcome.Parsed,
            [new HealthMetric("totalLongIos", null, counter, null),
             new HealthMetric("intervalLongIos", null, counter, null)],
            [], [], [], [], [], []);

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

    /// <summary>
    /// Une cadence heterogene — typiquement deux sessions enregistrant le meme serveur — est
    /// signalee, et le seul compteur qui depend de la session est tu. Le digest ne pretend pas
    /// savoir quel cycle appartient a quelle session, parce que la capture ne le dit pas.
    /// </summary>
    [Fact]
    public void An_irregular_cadence_is_announced_and_suppresses_the_interval_counter()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            // Deux sessions a 5 min, decalees de 30 s : les ecarts alternent 0,5 / 4,5 min.
            List<HealthCycle> cycles = [];
            for (int i = 0; i < 6; i++)
            {
                var t1 = a.AddMinutes(5 * i);
                var t2 = t1.AddSeconds(30);
                cycles.Add(new HealthCycle(t1, "1", [Io(t1, 100 + i)]));
                cycles.Add(new HealthCycle(t2, "1", [Io(t2, 200 + i)]));
            }
            db.InsertHealthCycles(runId, cycles);

            var e = new HealthDigest(db.Connection).Build();

            Assert.True(e.Digest.Coverage.CadenceIsIrregular);
            Assert.Contains(e.Digest.Notes,
                n => n.Contains("not homogeneous", StringComparison.OrdinalIgnoreCase));
            // totalLongIos est cumulatif par instance : il reste. intervalLongIos depend de la
            // session : il disparait.
            Assert.Contains(e.Digest.IoCounters, x => x.Name == "totalLongIos");
            Assert.DoesNotContain(e.Digest.IoCounters, x => x.Name == "intervalLongIos");
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

    /// <summary>
    /// L'enveloppe JSON est la raison d'etre de §8, et rien ne l'assertait : un ValueTuple
    /// n'expose Item1/Item2 qu'en CHAMPS, et System.Text.Json ne serialise que des proprietes,
    /// donc la section sortait en objets vides. Invisible en Markdown, qui la rendait bien.
    /// </summary>
    [Fact]
    public void The_json_envelope_carries_every_section_with_its_values()
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

            var envelope = new HealthDigest(db.Connection).Build();
            var json = System.Text.Json.JsonSerializer.Serialize(envelope,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            Assert.Contains("\"SchemaVersion\": 1", json);
            // Le nom ET la valeur, pas un objet vide.
            Assert.Contains("spinlockBackoffs", json);
            Assert.Contains("\"Delta\": 500", json);
            Assert.Contains("CXPACKET", json);
            Assert.DoesNotContain("{},", json);
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
