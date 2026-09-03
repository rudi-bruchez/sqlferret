// tests/SqlFerret.Core.Tests/ReclassifierSampleProvenanceTests.cs
using SqlFerret.Core.Storage;
using Xunit;

/// <summary>
/// Un echantillon deja normalise n'est pas du T-SQL : <c>where c = ?</c> ne parse pas, et le
/// reclassement le degraderait silencieusement en <c>OTHER</c>. Deux provenances produisent ce
/// cas, et aucune n'est detectable dans le texte lui-meme — seule la provenance le dit :
/// <list type="bullet">
/// <item><c>executions.sql_text_raw</c> d'un run importe en <c>--sanitize-sql-text literals</c>.</item>
/// <item><c>blocking_processes.inputbuf</c>, qu'<c>IngestionService.PrepareProc</c> stocke
/// normalise pour toute politique de redaction autre que <c>off</c>.</item>
/// </list>
/// </summary>
public class ReclassifierSampleProvenanceTests
{
    private static string NewProjectPath() =>
        Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");

    private static void SeedRun(DuckDbProject db, long runId, string redaction, string sqlTextPolicy)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"""
          INSERT INTO ingestion_runs (run_id, redaction_policy, sql_text_policy, normalizer_version)
          VALUES ({runId}, '{redaction}', '{sqlTextPolicy}', 1)
          """;
        c.ExecuteNonQuery();
    }

    private static void SeedSignature(DuckDbProject db, string hash, string kind)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"""
          INSERT INTO normalized_queries
                (normalized_hash, normalized_sql, statement_kind, primary_table,
                 target_object, normalizer_version, first_seen_at, last_seen_at)
          VALUES ('{hash}','x','{kind}',null,null,1,'2026-08-13','2026-08-13')
          """;
        c.ExecuteNonQuery();
    }

    private static void SeedExecution(DuckDbProject db, long id, long runId, string sql, string hash)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"""
          INSERT INTO executions
                (execution_id, run_id, captured_at, event_name, event_class, object_name,
                 is_system, database_name, login_name, client_hostname, client_app_name,
                 session_id, duration_us, cpu_time_us, logical_reads, physical_reads, writes,
                 row_count, query_hash, query_plan_hash, sql_text_raw, normalized_hash,
                 xe_file_name, file_offset)
          VALUES ({id},{runId},'2026-08-13','sql_statement_completed','statement',null,
                  false,null,null,null,null,
                  null,0,0,0,0,0,
                  0,null,null,$sql,'{hash}',
                  null,0)
          """;
        var p = c.CreateParameter();
        p.ParameterName = "sql";
        p.Value = sql;
        c.Parameters.Add(p);
        c.ExecuteNonQuery();
    }

    private static void SeedBlocking(DuckDbProject db, long runId, string inputbuf, string hash)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"""
          INSERT INTO blocking_reports (report_id, run_id, captured_at, monitor_loop, database_id)
          VALUES (1, {runId}, '2026-08-13', 0, 5);

          INSERT INTO blocking_processes
          VALUES (1,'blocker',53,0,'suspended',null,'Unknown',null,null,
                  null,null,null,null,null,null,null,
                  $buf,'{hash}')
          """;
        var p = c.CreateParameter();
        p.ParameterName = "buf";
        p.Value = inputbuf;
        c.Parameters.Add(p);
        c.ExecuteNonQuery();
    }

    private static string? KindOf(DuckDbProject db, string hash)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"SELECT statement_kind FROM normalized_queries WHERE normalized_hash='{hash}'";
        return (string?)c.ExecuteScalar();
    }

    [Fact]
    public void Sanitized_execution_sample_is_counted_apart_and_left_untouched()
    {
        var path = NewProjectPath();
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                SeedRun(db, 1, "hash", "literals");
                SeedSignature(db, "h1", "SELECT");
                SeedExecution(db, 1, 1, "select WidgetId from AppSchema.Widget where GadgetCode = ?", "h1");
            }

            using (var db = DuckDbProject.Open(path))
            {
                var r = new Reclassifier(db).Run();

                Assert.Equal(1L, r.RowsExamined);
                Assert.Equal(1L, r.RowsUnusableSample);
                Assert.Equal(0L, r.Unclassified);
                Assert.Equal(0L, r.RowsChanged);
                Assert.Equal(0L, r.RowsWithoutSample);
                Assert.Equal("SELECT", KindOf(db, "h1"));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Raw_execution_sample_wins_over_a_sanitized_one_for_the_same_signature()
    {
        var path = NewProjectPath();
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                SeedRun(db, 1, "hash", "literals");
                SeedRun(db, 2, "hash", "raw");
                SeedSignature(db, "h1", "OTHER");
                // L'echantillon sanitise est insere en premier : sans preference explicite pour un
                // run brut, c'est lui qu'any_value ramene, et `nvarchar(?)` ne parse pas.
                SeedExecution(db, 1, 1, "alter table AppSchema.Widget add GadgetCode nvarchar(?) null", "h1");
                SeedExecution(db, 2, 2, "ALTER TABLE AppSchema.Widget ADD GadgetCode nvarchar(10) NULL", "h1");
            }

            using (var db = DuckDbProject.Open(path))
            {
                var r = new Reclassifier(db).Run();

                Assert.Equal(1L, r.RowsChanged);
                Assert.Equal(0L, r.RowsUnusableSample);
                Assert.Equal(0L, r.Unclassified);
                Assert.Equal("ALTER TABLE ADD COLUMN", KindOf(db, "h1"));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Blocking_inputbuf_of_a_redacted_run_is_counted_apart_and_left_untouched()
    {
        var path = NewProjectPath();
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                SeedRun(db, 1, "hash", "raw");
                SeedSignature(db, "hb", "SELECT");
                SeedBlocking(db, 1, "select WidgetId from AppSchema.Widget where GadgetCode = ?", "hb");
            }

            using (var db = DuckDbProject.Open(path))
            {
                var r = new Reclassifier(db).Run();

                Assert.Equal(1L, r.RowsUnusableSample);
                Assert.Equal(0L, r.Unclassified);
                Assert.Equal("SELECT", KindOf(db, "hb"));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Blocking_inputbuf_of_a_redaction_off_run_is_still_reclassified()
    {
        var path = NewProjectPath();
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                SeedRun(db, 1, "off", "raw");
                SeedSignature(db, "hb", "OTHER");
                SeedBlocking(db, 1, "ALTER TABLE AppSchema.WidgetScaling REBUILD", "hb");
            }

            using (var db = DuckDbProject.Open(path))
            {
                var r = new Reclassifier(db).Run();

                Assert.Equal(1L, r.RowsChanged);
                Assert.Equal(0L, r.RowsUnusableSample);
                Assert.Equal("ALTER TABLE REBUILD", KindOf(db, "hb"));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Force_does_not_reclassify_an_unusable_sample_either()
    {
        var path = NewProjectPath();
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                SeedRun(db, 1, "hash", "literals");
                SeedSignature(db, "h1", "SELECT");
                SeedExecution(db, 1, 1, "select WidgetId from AppSchema.Widget where GadgetCode = ?", "h1");
            }

            using (var db = DuckDbProject.Open(path))
            {
                new Reclassifier(db).Run();
                var forced = new Reclassifier(db).Run(force: true);

                Assert.Equal(1L, forced.RowsUnusableSample);
                Assert.Equal("SELECT", KindOf(db, "h1"));
            }
        }
        finally { File.Delete(path); }
    }
}
