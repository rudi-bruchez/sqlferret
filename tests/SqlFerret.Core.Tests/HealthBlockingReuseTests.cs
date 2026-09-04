// tests/SqlFerret.Core.Tests/HealthBlockingReuseTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

/// <summary>
/// La section 5 de la spec repose sur une affirmation : les rapports de blocage integres
/// heritent de la porte de confidentialite sans code nouveau. Ces tests la verifient plutot que
/// de la supposer, et sur les quatre combinaisons des deux politiques.
/// </summary>
public class HealthBlockingReuseTests
{
    private const string Pii = "alice@example.com";

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Diag(string inputBuf) =>
        (new FakeEvent("sp_server_diagnostics_component_result", new DateTime(2026, 9, 3, 8, 26, 34),
            new Dictionary<string, object?>
            {
                ["component"] = "QUERY_PROCESSING",
                ["state"] = "WARNING",
                ["data"] = $"""
                <queryProcessing maxWorkers="9600">
                  <blockingTasks>
                    <blocked-process-report monitorLoop="11881">
                      <blocked-process><process spid="61" waitresource="KEY: 5:1 (x)" waittime="4100">
                        <inputbuf>{inputBuf}</inputbuf></process></blocked-process>
                      <blocking-process><process spid="72">
                        <inputbuf>update AppSchema.Widget set WidgetScaling=0</inputbuf></process></blocking-process>
                    </blocked-process-report>
                  </blockingTasks>
                </queryProcessing>
                """,
            },
            new Dictionary<string, object?>()),
         "system_health_0_1.xel", 0);

    private static string? Scalar(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = sql;
        var v = c.ExecuteScalar();
        return v is null or DBNull ? null : v.ToString();
    }

    [Fact]
    public void An_embedded_report_lands_as_a_diagnostics_source_report()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("logs/", [Diag("exec AppSchema.WidgetRecalc @Code='X1'")]);

            Assert.Equal("diagnostics", Scalar(db, "SELECT source FROM blocking_reports"));
            // ms -> us par le parseur de blocage existant, sans code nouveau.
            Assert.Equal("4100000", Scalar(db,
                "SELECT wait_time_us FROM blocking_processes WHERE role='blocked'"));
            Assert.Equal("Key", Scalar(db,
                "SELECT wait_resource_type FROM blocking_processes WHERE role='blocked'"));
            // L'empreinte qui joint a normalized_queries est calculee comme pour les autres.
            Assert.NotNull(Scalar(db,
                "SELECT inputbuf_fingerprint FROM blocking_processes WHERE role='blocked'"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// Le verbatim n'est conserve que si les DEUX politiques l'autorisent. C'est la composition
    /// reparee en 0.2.0, et un rapport integre doit y etre soumis comme les autres.
    /// </summary>
    [Theory]
    [InlineData(RedactionMode.Off, SqlTextSanitization.Raw, true)]
    [InlineData(RedactionMode.Off, SqlTextSanitization.Literals, false)]
    [InlineData(RedactionMode.Masked, SqlTextSanitization.Raw, false)]
    [InlineData(RedactionMode.Masked, SqlTextSanitization.Literals, false)]
    public void The_input_buffer_obeys_both_policies(RedactionMode red, SqlTextSanitization txt, bool verbatim)
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(red, [], SqlText: txt))
                .Ingest("logs/", [Diag($"exec AppSchema.WidgetRecalc @Code='{Pii}'")]);

            var stored = Scalar(db, "SELECT inputbuf FROM blocking_processes WHERE role='blocked'") ?? "";
            if (verbatim) Assert.Contains(Pii, stored);
            else Assert.DoesNotContain(Pii, stored);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// Le XML brut porte les input buffers en clair, donc il passe la meme porte que l'inputbuf.
    /// Ce test manquait, et son absence a laisse passer un portillon a moitie cable : le chemin
    /// evenement appliquait la porte, le chemin integre passait `null` en dur, et la Theory
    /// voisine n'assertait que `inputbuf`. Elle etait donc verte sur les quatre combinaisons
    /// contre un code qui ne conservait le XML dans aucune.
    /// </summary>
    [Theory]
    [InlineData(RedactionMode.Off, SqlTextSanitization.Raw, true)]
    [InlineData(RedactionMode.Off, SqlTextSanitization.Literals, false)]
    [InlineData(RedactionMode.Masked, SqlTextSanitization.Raw, false)]
    [InlineData(RedactionMode.Masked, SqlTextSanitization.Literals, false)]
    public void The_raw_xml_obeys_both_policies(RedactionMode red, SqlTextSanitization txt, bool verbatim)
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(red, [], SqlText: txt))
                .Ingest("logs/", [Diag($"exec AppSchema.WidgetRecalc @Code='{Pii}'")]);

            var raw = Scalar(db, "SELECT raw_xml FROM blocking_reports WHERE source='diagnostics'");
            if (verbatim)
            {
                Assert.NotNull(raw);
                Assert.Contains(Pii, raw);
                // Le fragment conserve est le rapport de blocage, pas le cycle de diagnostics
                // entier : celui-ci n'est stocke nulle part.
                Assert.Contains("blocked-process-report", raw);
            }
            else
            {
                Assert.Null(raw);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// La fuite ne doit pas se deplacer d'une colonne a l'autre : normalized_queries vit dans le
    /// meme fichier et se joint sur la meme empreinte.
    /// </summary>
    [Fact]
    public void The_normalized_row_of_an_embedded_report_carries_no_literal_either()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Diag($"exec AppSchema.WidgetRecalc @Code='{Pii}'")]);

            var normalized = Scalar(db, """
              SELECT n.normalized_sql
              FROM normalized_queries n
              JOIN blocking_processes b ON b.inputbuf_fingerprint = n.normalized_hash
              WHERE b.role = 'blocked'
              """) ?? "";
            Assert.DoesNotContain(Pii, normalized);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
