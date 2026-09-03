// tests/SqlFerret.Core.Tests/BlockingSourceIsolationTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class BlockingSourceIsolationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static BlockingProcess Proc(int spid, long? waitUs) =>
        new(spid, 0, "suspended", "KEY: 5:1 (x)", WaitResourceType.Key, null, null, waitUs,
            "S", "read committed (2)", 1, "SampleApp", "WS1", "svc",
            "exec AppSchema.WidgetRecalc @WidgetId=1", "fp_" + spid);

    private static PreparedBlockingReport Report(int loop, int blocked, int blocking, string source)
    {
        var rep = new BlockingReport(new DateTime(2026, 2, 24), loop, 5,
            Proc(blocked, 3_000_000L), Proc(blocking, null));
        return new PreparedBlockingReport(rep,
            new PreparedBlockingProcess(rep.Blocked, null, "exec AppSchema.WidgetRecalc @WidgetId=1"),
            new PreparedBlockingProcess(rep.Blocking, null, "update AppSchema.Widget"),
            RawXml: null, Source: source);
    }

    /// <summary>Une incidence reelle, trois instantanes de la meme : les compteurs doivent dire 1.</summary>
    [Fact]
    public void Diagnostics_snapshots_do_not_inflate_the_event_counts()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),
                Report(43, 61, 72, "diagnostics"),
                Report(44, 61, 72, "diagnostics"),
                Report(45, 61, 72, "diagnostics"),
            ]);

            var q = new BlockingQueries(db.Connection);
            Assert.Equal(1L, q.Overview().ReportCount);
            Assert.Equal(1L, q.TopBlockers(10)[0].Count);
            Assert.Equal(1L, q.LockModes().Sum(x => x.Count));
            Assert.Equal(1L, q.Locality().Sum(x => x.Count));
            Assert.Equal(1L, q.IsolationLevels().Sum(x => x.Count));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// SQL Server emet un rapport par processus BLOQUE, donc une chaine 83 -> 72 -> 61 arrive
    /// comme deux rapports partageant leur monitor_loop. Les recomposer est la raison d'etre de
    /// Chains(), et c'est ce que la premiere ecriture de ce test avait detruit en se clant sur
    /// report_id : un rapport ne porte qu'une arete, donc la profondeur plafonnait a 2 et le test
    /// epinglait le bug comme resultat attendu.
    /// </summary>
    [Fact]
    public void Chains_reconstructs_a_chain_that_spans_two_reports()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),   // 72 bloque 61
                Report(42, 72, 83, "event"),   // 83 bloque 72
            ]);

            var chains = new BlockingQueries(db.Connection).Chains();

            var deepest = chains.MaxBy(ch => ch.Depth)!;
            Assert.Equal(3, deepest.Depth);
            Assert.Equal(83, deepest.HeadSpid);
            Assert.Equal(2L, deepest.EdgeCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// Les instantanes de diagnostics ne rejoignent jamais une chaine d'evenements, meme quand
    /// leurs spids s'enchainent parfaitement : ce sont deux mecanismes qui numerotent leurs
    /// boucles dans le meme espace d'entiers sans aucun rapport entre eux.
    /// </summary>
    [Fact]
    public void Chains_never_links_an_event_report_to_a_diagnostics_snapshot()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),
                Report(42, 72, 83, "diagnostics"),
            ]);

            var chains = new BlockingQueries(db.Connection).Chains();

            // Seule l'arete 'event' subsiste : profondeur 2, une arete.
            var ch = Assert.Single(chains);
            Assert.Equal(2, ch.Depth);
            Assert.Equal(1L, ch.EdgeCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Deux incidents distincts, boucles distinctes : aucune chaine ne les relie.</summary>
    [Fact]
    public void Chains_does_not_link_reports_from_different_monitor_loops()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),
                Report(99, 72, 83, "event"),
            ]);

            var chains = new BlockingQueries(db.Connection).Chains();

            Assert.All(chains, ch => Assert.Equal(2, ch.Depth));
            Assert.All(chains, ch => Assert.Equal(1L, ch.EdgeCount));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
