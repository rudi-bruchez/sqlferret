// tests/SqlFerret.Core.Tests/BlockingInputBufSanitizationTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

/// <summary>
/// <c>blocking_processes.inputbuf</c> est du texte d'instruction : il relève de
/// <c>--sanitize-sql-text</c> au même titre qu'<c>executions.sql_text_raw</c>. Les deux politiques
/// se composent — le verbatim n'est conservé que si la rédaction est <c>off</c> <b>et</b> la
/// politique de texte <c>raw</c> — de sorte que la sanitisation ne peut jamais relâcher ce que la
/// rédaction retenait.
/// </summary>
public class BlockingInputBufSanitizationTests
{
    private const string Pii = "alice@example.com";

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) BlockedReport(string blockedInputBuf, string blockingInputBuf)
    {
        var xml = $"""
        <blocked-process-report monitorLoop="42">
          <blocked-process>
            <process id="p1" waitresource="KEY: 5:72057594041204736 (x)" waittime="5972"
                     spid="201" status="suspended" trancount="2" lockMode="S"
                     isolationlevel="read committed (2)" clientapp="SampleApp" hostname="WS1" loginname="svc">
              <inputbuf>{blockedInputBuf}</inputbuf>
            </process>
          </blocked-process>
          <blocking-process>
            <process id="p2" spid="118" status="sleeping" trancount="1"
                     clientapp="SampleApp" hostname="WS2" loginname="svc">
              <inputbuf>{blockingInputBuf}</inputbuf>
            </process>
          </blocking-process>
        </blocked-process-report>
        """;
        return (new FakeEvent("blocked_process_report", new DateTime(2026, 1, 1),
                    new Dictionary<string, object?> { ["blocked_process"] = xml },
                    new Dictionary<string, object?>()),
                "s_0.xel", 0);
    }

    private static string InputBufOf(DuckDbProject db, string role)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"SELECT inputbuf FROM blocking_processes WHERE role = '{role}'";
        return (string)c.ExecuteScalar()!;
    }

    private static string NormalizedSqlOf(DuckDbProject db)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = """
          SELECT n.normalized_sql
          FROM normalized_queries n
          JOIN blocking_processes b ON b.inputbuf_fingerprint = n.normalized_hash
          WHERE b.role = 'blocked'
          """;
        return (string)c.ExecuteScalar()!;
    }

    [Fact]
    public void Literals_collapses_the_inputbuf_even_when_redaction_is_off()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [BlockedReport(
                    $"exec AppSchema.WidgetRecalc @TenantId=897,@Code='{Pii}'",
                    "UPDATE AppSchema.Widget SET WidgetScaling=0 WHERE WidgetId=42")]);

            var blocked = InputBufOf(db, "blocked");
            Assert.DoesNotContain(Pii, blocked);
            Assert.DoesNotContain("897", blocked);
            Assert.Contains("WidgetRecalc", blocked);   // identifiants conservés, comme pour les exécutions

            Assert.DoesNotContain("42", InputBufOf(db, "blocking"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Literals_collapses_the_inputbuf_in_normalized_queries_too()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [BlockedReport(
                    $"exec AppSchema.WidgetRecalc @Code='{Pii}'",
                    "UPDATE AppSchema.Widget SET WidgetScaling=0")]);

            // La fuite ne doit pas se déplacer d'une colonne à l'autre : les deux vivent dans le
            // même fichier et se rejoignent sur la même empreinte.
            Assert.DoesNotContain(Pii, NormalizedSqlOf(db));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Raw_under_redaction_off_still_stores_the_inputbuf_verbatim()
    {
        const string buf = "exec AppSchema.WidgetRecalc @TenantId=897,@Code='X1'";
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("logs/", [BlockedReport(buf, "UPDATE AppSchema.Widget SET WidgetScaling=0")]);

            Assert.Equal(buf, InputBufOf(db, "blocked"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static (IXeEventData, string, long) DeadlockReport(string inputBuf)
    {
        var xml = $"""
        <deadlock>
          <victim-list><victimProcess id="p1"/></victim-list>
          <process-list>
            <process id="p1" spid="201"><inputbuf>{inputBuf}</inputbuf></process>
          </process-list>
        </deadlock>
        """;
        return (new FakeEvent("xml_deadlock_report", new DateTime(2026, 1, 1),
                    new Dictionary<string, object?> { ["xml_report"] = xml },
                    new Dictionary<string, object?>()),
                "s_0.xel", 0);
    }

    private static string? ScalarOrNull(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = sql;
        var v = c.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    /// <summary>
    /// Le XML brut porte les mêmes input buffers, en clair. Le sanitiser proprement supposerait de
    /// réécrire chaque nœud <c>inputbuf</c> du document ; tant que ce n'est pas fait, la seule
    /// réponse honnête à <c>literals</c> est de ne pas le conserver — sinon la politique demandée
    /// serait contredite par une colonne voisine.
    /// </summary>
    [Fact]
    public void Literals_does_not_retain_the_raw_blocking_xml_even_under_redaction_off()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [BlockedReport(
                    $"exec AppSchema.WidgetRecalc @Code='{Pii}'",
                    "UPDATE AppSchema.Widget SET WidgetScaling=0")]);

            Assert.Null(ScalarOrNull(db, "SELECT raw_xml FROM blocking_reports"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Literals_does_not_retain_the_deadlock_graph_even_under_redaction_off()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [DeadlockReport($"exec AppSchema.WidgetRecalc @Code='{Pii}'")]);

            var graph = ScalarOrNull(db, "SELECT graph_xml FROM deadlock_reports");
            Assert.DoesNotContain(Pii, graph ?? "");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Raw_under_redaction_off_still_retains_the_blocking_xml()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("logs/", [BlockedReport(
                    "exec AppSchema.WidgetRecalc @Code='X1'",
                    "UPDATE AppSchema.Widget SET WidgetScaling=0")]);

            Assert.Contains("blocked-process-report",
                ScalarOrNull(db, "SELECT raw_xml FROM blocking_reports") ?? "");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_double_quoted_value_in_an_inputbuf_is_collapsed()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            // Sous SET QUOTED_IDENTIFIER OFF, "..." est une valeur, pas un identifiant, et la
            // capture ne dit jamais quel réglage était en vigueur. On échoue du côté sûr.
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [BlockedReport(
                    $"SELECT WidgetId FROM AppSchema.Widget WHERE Email = \"{Pii}\"",
                    "UPDATE AppSchema.Widget SET WidgetScaling=0")]);

            Assert.DoesNotContain(Pii, InputBufOf(db, "blocked"));
            Assert.DoesNotContain(Pii, NormalizedSqlOf(db));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
