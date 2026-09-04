// tests/SqlFerret.Core.Tests/ServerDiagnosticsIngestionTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class ServerDiagnosticsIngestionTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Diag(string component, string? xml, DateTime ts) =>
        (new FakeEvent("sp_server_diagnostics_component_result", ts,
            new Dictionary<string, object?> { ["component"] = component, ["state"] = "CLEAN", ["data"] = xml },
            new Dictionary<string, object?>()),
         "system_health_0_1.xel", 0);

    private const string Qp = """<queryProcessing maxWorkers="9600" workersIdle="10"/>""";
    private const string Io = """<ioSubsystem totalLongIos="7"/>""";

    [Fact]
    public void The_three_counters_are_exclusive_and_account_for_every_event()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [
                    Diag("QUERY_PROCESSING", Qp, at),
                    Diag("IO_SUBSYSTEM", Io, at.AddTicks(2393)),
                    Diag("events", "<events/>", at.AddTicks(3000)),        // unhandled
                    Diag("QUERY_PROCESSING", null, at.AddMinutes(5)),      // failed
                ]);

            Assert.Equal(4, res.Read);
            Assert.Equal(2, res.ServerDiagnostics);
            Assert.Equal(1, res.ServerDiagnosticsUnhandled);
            Assert.Equal(1, res.ServerDiagnosticsParseFailures);
            Assert.Equal(0, res.Unmapped);   // plus rien ne tombe dans le fourre-tout

            // Les compteurs d'EVENEMENTS se reconcilient exactement avec events_read. Les
            // compteurs de sous-documents n'y entrent pas.
            Assert.Equal(res.Read,
                res.Mapped + res.Unmapped + res.Cleaned + res.Blocking + res.Deadlocks
                + res.PlanProfiles + res.ServerDiagnostics + res.ServerDiagnosticsUnhandled
                + res.ServerDiagnosticsParseFailures);
            Assert.Equal(0, res.Blocking);   // aucun blocked_process_report dans ce flux

            using var c = db.Connection.CreateCommand();
            c.CommandText = """
              SELECT events_server_diagnostics, events_server_diagnostics_unhandled,
                     server_diagnostics_parse_failures
              FROM ingestion_runs WHERE run_id = $r
              """;
            var p = c.CreateParameter(); p.ParameterName = "r"; p.Value = res.RunId; c.Parameters.Add(p);
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(2L, r.GetInt64(0));
            Assert.Equal(1L, r.GetInt64(1));
            Assert.Equal(1L, r.GetInt64(2));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Cycles_are_grouped_and_persisted()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Diag("QUERY_PROCESSING", Qp, at), Diag("IO_SUBSYSTEM", Io, at.AddTicks(2393))]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM health_cycles";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM health_samples";
            Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// La vidange par lots ne doit jamais couper un cycle en deux : les quatre composants d'un
    /// cycle arrivent a moins d'une milliseconde d'ecart et doivent rester groupes, quelle que
    /// soit la taille de lot.
    /// </summary>
    [Fact]
    public void Batched_flushing_never_splits_a_cycle()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        var events = new List<(IXeEventData, string, long)>();
        for (int i = 0; i < 30; i++)
        {
            var t = at.AddMinutes(5 * i);
            events.Add(Diag("SYSTEM", """<system spinlockBackoffs="1"/>""", t));
            events.Add(Diag("RESOURCE", """<resource outOfMemoryExceptions="0"/>""", t.AddTicks(619)));
            events.Add(Diag("QUERY_PROCESSING", Qp, t.AddTicks(2352)));
            events.Add(Diag("IO_SUBSYSTEM", Io, t.AddTicks(2393)));
        }

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = new IngestionService(db,
                    new IngestionOptions(RedactionMode.Masked, [], BatchSize: 7))
                .Ingest("logs/", events);

            Assert.Equal(120, res.ServerDiagnostics);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM health_cycles";
            Assert.Equal(30L, Convert.ToInt64(c.ExecuteScalar()));
            // Chaque cycle porte bien ses quatre composants : aucun n'a ete coupe par une vidange.
            c.CommandText = """
              SELECT count(*) FROM (
                SELECT cycle_id, count(*) AS n FROM health_samples GROUP BY cycle_id
              ) WHERE n <> 4
              """;
            Assert.Equal(0L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string? FindSystemHealthCapture()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var sample = Path.Combine(dir, "sample");
            if (!Directory.Exists(sample)) continue;
            return Directory.GetFiles(sample, "system_health*.xel").OrderBy(f => f).FirstOrDefault();
        }
        return null;
    }

    /// <summary>
    /// Bout en bout sur une vraie capture, quand il y en a une sous sample/. Le gate suit la
    /// convention du depot : absent en clone propre, donc ignore.
    /// </summary>
    [SkippableFact]
    public void A_real_system_health_capture_yields_cycles_and_reconciles()
    {
        var capture = FindSystemHealthCapture();
        Skip.If(capture is null, "no system_health capture under sample/");

        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest(capture!, new XelReader().Read([capture!]));

            Assert.True(res.ServerDiagnostics > 0, "la capture devrait contenir des diagnostics");
            Assert.Equal(res.Read,
                res.Mapped + res.Unmapped + res.Cleaned + res.Blocking + res.Deadlocks
                + res.PlanProfiles + res.ServerDiagnostics + res.ServerDiagnosticsUnhandled
                + res.ServerDiagnosticsParseFailures);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM health_cycles";
            Assert.True(Convert.ToInt64(c.ExecuteScalar()) > 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void An_embedded_blocking_report_counts_as_a_sub_document_not_as_an_event()
    {
        var at = new DateTime(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);
        const string withBlocking = """
        <queryProcessing maxWorkers="9600">
          <blockingTasks>
            <blocked-process-report monitorLoop="11881">
              <blocked-process><process spid="61" waittime="4100"><inputbuf>exec AppSchema.WidgetRecalc</inputbuf></process></blocked-process>
              <blocking-process><process spid="72"><inputbuf>update AppSchema.Widget</inputbuf></process></blocking-process>
            </blocked-process-report>
          </blockingTasks>
        </queryProcessing>
        """;
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Diag("QUERY_PROCESSING", withBlocking, at)]);

            Assert.Equal(1, res.ServerDiagnostics);
            Assert.Equal(1, res.EmbeddedBlocking);
            // events_blocking compte les evenements blocked_process_report. Un rapport integre est
            // un fragment d'un evenement de diagnostics, pas un evenement : l'y compter ferait
            // depasser events_read a la somme de reconciliation.
            Assert.Equal(0, res.Blocking);
            Assert.Equal(res.Read,
                res.Mapped + res.Unmapped + res.Cleaned + res.Blocking + res.Deadlocks
                + res.PlanProfiles + res.ServerDiagnostics + res.ServerDiagnosticsUnhandled
                + res.ServerDiagnosticsParseFailures);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT source FROM blocking_reports";
            Assert.Equal("diagnostics", (string?)c.ExecuteScalar());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    /// <summary>
    /// Le tampon de diagnostics ne se vidait que lorsqu'un cycle etait clos, c'est-a-dire quand
    /// deux echantillons consecutifs etaient separes de plus d'une seconde. Une capture dont les
    /// evenements arrivent en rafales sous la seconde n'en fermait donc jamais aucun : rien
    /// n'etait ecrit, tout restait en memoire, et <c>Group()</c> retriait l'integralite du tampon
    /// a chaque evenement — cout superlineaire mesure a 6 000 → 11 s, 24 000 → 74 s.
    /// <para>Un vrai cycle porte quatre a six echantillons. Au-dela du plafond, ce n'est plus un
    /// cycle mais une pathologie, et la garantie « ne jamais couper un cycle » cesse de valoir
    /// contre l'epuisement memoire de l'hote.</para>
    /// </summary>
    [Fact]
    public void A_sub_second_burst_cannot_hold_the_whole_capture_in_memory()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);

            // 600 evenements a 100 ms d'ecart : aucun n'ouvre jamais l'ecart d'une seconde, donc
            // aucun cycle ne se ferme. Avec un lot de 50, le plafond du tampon est a 500.
            List<(IXeEventData, string, long)> evs = [];
            for (int i = 0; i < 600; i++)
                evs.Add(Diag("QUERY_PROCESSING", Qp, at.AddMilliseconds(100 * i)));

            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [], BatchSize: 50))
                .Ingest("logs/", evs);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM health_cycles";
            var cycles = Convert.ToInt64(c.ExecuteScalar());
            c.CommandText = "SELECT count(*) FROM health_samples";
            var samples = Convert.ToInt64(c.ExecuteScalar());

            Assert.Equal(600L, samples);
            // Sans plafond, les 600 echantillons formaient UN seul cycle jamais vidange, garde en
            // memoire jusqu'a la fin du flux. Le plafond le coupe, donc il y en a plus d'un.
            Assert.True(cycles > 1,
                $"le tampon n'a jamais ete vidange : {cycles} cycle pour {samples} echantillons");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
