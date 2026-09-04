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
    private static ServerDiagnosticsSample Res(DateTime ts, params HealthMetric[] m) =>
        new(ts, "RESOURCE", "CLEAN", DiagnosticsOutcome.Parsed, m, [], [], [], [], [], []);

    /// <summary>
    /// Un plateau d'E/S longues est une panne disque stable, pas un serveur calme. La difference
    /// rendait zero et le filtre l'effacait ; la somme le montre.
    /// </summary>
    [Fact]
    public void A_steady_interval_io_counter_reaches_the_digest()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,                "1", [Io(at,                5)]),
                new HealthCycle(at.AddMinutes(5),  "1", [Io(at.AddMinutes(5),  5)]),
                new HealthCycle(at.AddMinutes(10), "1", [Io(at.AddMinutes(10), 5)]),
            ]);

            var e = new HealthDigest(db.Connection).Build();

            var interval = e.Digest.IoCounters.Single(x => x.Name == "intervalLongIos");
            Assert.Equal(15L, interval.Delta);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// Un drapeau 0/1 n'a pas de delta. Bloque a 1, il donnait un pas de zero, disparaissait du
    /// digest, et l'outil certifiait alors sain un serveur en penurie de memoire permanente.
    /// </summary>
    [Fact]
    public void A_pool_pinned_out_of_memory_is_reported_and_forbids_the_healthy_note()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "1", [Res(at,               new HealthMetric("isAnyPoolOutOfMemory", null, 1, null))]),
                new HealthCycle(at.AddMinutes(5), "1", [Res(at.AddMinutes(5), new HealthMetric("isAnyPoolOutOfMemory", null, 1, null))]),
            ]);

            var e = new HealthDigest(db.Connection).Build();

            var g = e.Digest.MemoryGauges.Single(x => x.Name == "isAnyPoolOutOfMemory");
            Assert.Equal(1d, g.Max);
            Assert.DoesNotContain(e.Digest.Notes,
                n => n.Contains("expected result", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// La note « serveur sain » ne regardait que trois des six sections. Un serveur qui n'a que
    /// des E/S longues la recevait quand meme.
    /// </summary>
    [Fact]
    public void Io_pressure_alone_forbids_the_healthy_note()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "1", [Io(at,               7)]),
                new HealthCycle(at.AddMinutes(5), "1", [Io(at.AddMinutes(5), 7)]),
            ]);

            var e = new HealthDigest(db.Connection).Build();

            Assert.NotEmpty(e.Digest.IoCounters);
            Assert.DoesNotContain(e.Digest.Notes,
                n => n.Contains("expected result", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    /// <summary>
    /// La part non-diagnostics decrit la CAPTURE, pas le projet. Elle sommait tous les runs, donc
    /// un projet qui contient aussi une trace de charge — usage documente, les imports s'ajoutent —
    /// rendait une part proche de 100 % qui ne mesurait plus rien. Seuls les runs qui ont
    /// reellement ingere des diagnostics entrent dans le calcul.
    /// </summary>
    [Fact]
    public void The_coverage_share_describes_the_health_capture_not_the_whole_project()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);

            // Run 1 : une trace de charge ordinaire, aucun diagnostic.
            long workload = db.BeginRun("trace/", 1, 0, "masked");
            db.FinishRun(workload, read: 1_000_000, mapped: 1_000_000, unmapped: 0, cleaned: 0,
                tokenizeFailures: 0, blocking: 0, deadlocks: 0, blockingParseFailures: 0);

            // Run 2 : la capture system_health — 1000 evenements dont 800 diagnostics.
            long health = db.BeginRun("logs/", 1, 0, "masked");
            db.FinishRun(health, read: 1_000, mapped: 200, unmapped: 0, cleaned: 0,
                tokenizeFailures: 0, blocking: 0, deadlocks: 0, blockingParseFailures: 0,
                serverDiagnostics: 800);
            db.InsertHealthCycles(health, [new HealthCycle(at, "1", [Qp(at, 1)])]);

            var cov = new HealthQueries(db.Connection).Coverage();

            // 200 / 1000 sur la capture, et non 1 000 200 / 1 001 000.
            Assert.Equal(0.2, cov.NonDiagnosticsShare, 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    /// <summary>
    /// Cinq composants sont documentes par Microsoft ; tout autre nom est celui d'un groupe de
    /// disponibilite, c'est-a-dire du nommage choisi par le client. Il est stocke verbatim sous
    /// --redaction full et imprime dans le digest, et docs/privacy.md n'enumerait que trois
    /// colonnes hors politique.
    /// </summary>
    [Fact]
    public void An_availability_group_name_is_announced_as_customer_naming()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "full");
            ServerDiagnosticsSample Ag(DateTime ts) =>
                new(ts, "SampleAppAvailabilityGroup", "WARNING", DiagnosticsOutcome.Unhandled,
                    [], [], [], [], [], [], []);
            db.InsertHealthCycles(runId, [
                new HealthCycle(at,               "1", [Ag(at)]),
                new HealthCycle(at.AddMinutes(5), "1", [Ag(at.AddMinutes(5))]),
            ]);

            var e = new HealthDigest(db.Connection).Build();

            Assert.Contains(e.Digest.Notes,
                n => n.Contains("availability group", StringComparison.OrdinalIgnoreCase)
                  && n.Contains("SampleAppAvailabilityGroup", StringComparison.Ordinal));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
