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
    /// Deux incidences sans rapport partageant un monitor_loop ne forment pas une chaine.
    /// Un filtre sur `source` ne corrige pas ce cas : la fabrication a lieu dans la CTE, avant
    /// tout regroupement. Profondeur 2 pour une paire isolee — `heads` part a 1, la recursion
    /// ajoute 1 ; une chaine fabriquee vaudrait 3.
    /// </summary>
    [Fact]
    public void Chains_does_not_fabricate_a_chain_across_unrelated_reports()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertBlockingBatch(runId, [
                Report(42, 61, 72, "event"),
                Report(42, 72, 83, "event"),
            ]);

            var chains = new BlockingQueries(db.Connection).Chains();

            Assert.NotEmpty(chains);
            Assert.All(chains, ch => Assert.Equal(2, ch.Depth));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
