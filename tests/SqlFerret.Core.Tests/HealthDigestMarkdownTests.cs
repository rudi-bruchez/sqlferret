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
    /// <summary>
    /// Les deux jauges en microsecondes sortaient en entiers nus — dans le fichier meme qui avait
    /// ete deplace vers l'hote POUR respecter l'invariant de formatage. Et la ligne de pression
    /// des workers interpolait ses doubles en culture courante, seule du fichier a le faire :
    /// en fr-FR elle rendait des virgules decimales dans un tableau invariant.
    /// </summary>
    [Fact]
    public void Microsecond_gauges_are_formatted_and_every_number_is_invariant()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        static ServerDiagnosticsSample Qp2(DateTime ts) =>
            new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed,
                [new HealthMetric("oldestPendingTaskWaitingTimeUs", null, 1_500_000, null)],
                [], [], [], [], [], []);
        static ServerDiagnosticsSample Res(DateTime ts) =>
            new(ts, "RESOURCE", "CLEAN", DiagnosticsOutcome.Parsed,
                [new HealthMetric("processOutOfMemoryPeriodUs", null, 2_000_000, null)],
                [], [], [], [], [], []);

        var prior = System.Globalization.CultureInfo.CurrentCulture;
        var path = TempDb();
        try
        {
            // La culture francaise est le piege : elle rend « 1500000 » en « 1500000 » mais tout
            // double non invariant en « 1,5 ».
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "1", [Qp2(at), Res(at)]),
                new HealthCycle(at.AddMinutes(5), "1", [Qp2(at.AddMinutes(5)), Res(at.AddMinutes(5))]),
            ]);

            var md = SqlFerret.Cli.HealthDigestMarkdown.Render(new HealthDigest(db.Connection).Build());

            // 1 500 000 us = 1500 ms. Le nombre nu ne doit plus apparaitre.
            Assert.DoesNotContain("1500000", md, StringComparison.Ordinal);
            Assert.DoesNotContain("2000000", md, StringComparison.Ordinal);
            Assert.Contains("oldestPendingTaskWaitingTimeUs", md, StringComparison.Ordinal);
            Assert.Contains("processOutOfMemoryPeriodUs", md, StringComparison.Ordinal);
            // Aucune virgule decimale : elles trahiraient une interpolation en culture courante.
            Assert.DoesNotContain("1,5", md, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prior;
            if (File.Exists(path)) File.Delete(path);
        }
    }
    /// <summary>
    /// Tout texte venu de la capture est une donnee d'une AUTRE machine. Rendu tel quel entre
    /// backticks, il sort de sa cellule : un backtick ferme le span, une barre verticale ouvre une
    /// colonne, un saut de ligne termine le tableau, et le reste devient du Markdown a part
    /// entiere — titres fabriques, lignes de metriques forgees, HTML brut. Le digest est justement
    /// l'artefact qu'on envoie a un client ou qu'on ouvre dans un portail qui rend le Markdown.
    /// <para>Le vecteur realiste n'est pas le type d'attente, qui vient de l'enumeration du
    /// moteur, mais le chemin de fichier — quiconque cree une base choisit le nom du fichier — le
    /// nom d'un groupe de disponibilite, et la description d'un clerc memoire.</para>
    /// </summary>
    [Fact]
    public void Capture_text_cannot_break_out_of_its_cell()
    {
        const string Evil = "X` | 9 | 9 | 9 |\n\n## Injected heading\n<img src=x onerror=alert(1)>\n\n`Y";
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "full");

            ServerDiagnosticsSample Qp3(DateTime ts, long n) =>
                new(ts, "QUERY_PROCESSING", "CLEAN", DiagnosticsOutcome.Parsed, [],
                    [new HealthWait(false, "byCount", Evil, n, 2_000, 5_000)], [], [], [], [], []);
            ServerDiagnosticsSample Ios(DateTime ts) =>
                new(ts, "IO_SUBSYSTEM", "WARNING", DiagnosticsOutcome.Parsed, [], [], [], [],
                    [new HealthPendingIo(900_000, Evil, "h1", 0)], [], []);

            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "1", [Qp3(at, 10), Ios(at)]),
                new HealthCycle(at.AddMinutes(5), "1", [Qp3(at.AddMinutes(5), 30), Ios(at.AddMinutes(5))]),
            ]);

            var md = SqlFerret.Cli.HealthDigestMarkdown.Render(new HealthDigest(db.Connection).Build());

            // La propriete n'est pas que le texte disparaisse — ce serait detruire la donnee —
            // mais qu'il ne soit plus INTERPRETE. Reste dans sa cellule, il est inerte.
            var starts = md.Split((char)10).Select(l => l.TrimEnd((char)13)).ToList();
            Assert.DoesNotContain(starts, l => l.StartsWith("## Injected", StringComparison.Ordinal));
            Assert.DoesNotContain(starts, l => l.StartsWith("<img", StringComparison.Ordinal));
            // Et il reste lisible : c'est un rendu, pas une censure.
            Assert.Contains("Injected heading", md, StringComparison.Ordinal);
            // Et le tableau des attentes garde exactement UNE ligne de donnees. La valeur
            // hostile en fabriquait une seconde en enjambant la fin de ligne.
            Assert.Equal(1, starts.Count(l => l.StartsWith("| " + (char)96, StringComparison.Ordinal)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
