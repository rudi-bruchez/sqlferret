// tests/SqlFerret.Core.Tests/ReclassifierTests.cs
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Storage;
using Xunit;

public class ReclassifierTests
{
    /// <summary>
    /// Projet en v1 : une ligne DDL classable, une ligne non parsable, une ligne sans exécution.
    /// </summary>
    private static string SeedV1Project()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        using var db = DuckDbProject.Open(path);
        using var c = db.Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO normalized_queries
                (normalized_hash, normalized_sql, statement_kind, primary_table,
                 target_object, normalizer_version, first_seen_at, last_seen_at)
          VALUES ('h1','x','OTHER',null,null,1,'2026-08-13','2026-08-13'),
                 ('h2','x','OTHER',null,null,1,'2026-08-13','2026-08-13'),
                 ('h3','x','OTHER',null,null,1,'2026-08-13','2026-08-13');

          INSERT INTO executions
                (execution_id, run_id, captured_at, event_name, event_class, object_name,
                 is_system, database_name, login_name, client_hostname, client_app_name,
                 session_id, duration_us, cpu_time_us, logical_reads, physical_reads, writes,
                 row_count, query_hash, query_plan_hash, sql_text_raw, normalized_hash,
                 xe_file_name, file_offset)
          VALUES (1,1,'2026-08-13','sql_statement_completed','statement',null,
                  false,null,null,null,null,
                  null,0,0,0,0,0,
                  0,null,null,'CREATE NONCLUSTERED INDEX IX_A ON dbo.T (c1)','h1',
                  null,0),
                 (2,1,'2026-08-13','sql_statement_completed','statement',null,
                  false,null,null,null,null,
                  null,0,0,0,0,0,
                  0,null,null,'@@@ ((','h2',
                  null,0);
          """;
        c.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void Reclassifies_ddl_and_bumps_version()
    {
        var path = SeedV1Project();
        try
        {
            using var db = DuckDbProject.Open(path);
            var r = new Reclassifier(db).Run();

            Assert.Equal(3L, r.RowsExamined);
            Assert.Equal(1L, r.RowsChanged);          // h1
            Assert.Equal(1L, r.Unclassified);         // h2
            Assert.Equal(1L, r.RowsWithoutSample);    // h3
            Assert.Equal(0L, r.RowsUnchanged);
            Assert.Equal(QueryNormalizer.Version, r.ToVersion);

            // La provenance n'est pas reecrite : ingestion_runs garde la version d'import.
            using var v = db.Connection.CreateCommand();
            v.CommandText = "SELECT count(*) FROM ingestion_runs WHERE normalizer_version >= 2";
            Assert.Equal(0L, Convert.ToInt64(v.ExecuteScalar()));

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT statement_kind, primary_table, target_object FROM normalized_queries WHERE normalized_hash='h1'";
            using var rd = c.ExecuteReader();
            Assert.True(rd.Read());
            Assert.Equal("CREATE INDEX", rd.GetString(0));
            Assert.Equal("dbo.T", rd.GetString(1));
            Assert.Equal("IX_A", rd.GetString(2));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Counters_are_exhaustive()
    {
        var path = SeedV1Project();
        try
        {
            using var db = DuckDbProject.Open(path);
            var r = new Reclassifier(db).Run();
            Assert.Equal(r.RowsExamined,
                r.RowsChanged + r.RowsUnchanged + r.Unclassified + r.RowsWithoutSample);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Second_run_converges_to_zero()
    {
        var path = SeedV1Project();
        try
        {
            using var db = DuckDbProject.Open(path);
            new Reclassifier(db).Run();
            var again = new Reclassifier(db).Run();
            Assert.Equal(0L, again.RowsExamined);    // y compris la ligne non parsable
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Force_reexamines_everything()
    {
        var path = SeedV1Project();
        try
        {
            using var db = DuckDbProject.Open(path);
            new Reclassifier(db).Run();
            var forced = new Reclassifier(db).Run(force: true);
            Assert.Equal(3L, forced.RowsExamined);
        }
        finally { File.Delete(path); }
    }
    // -----------------------------------------------------------------------------------------
    // B10 : les signatures issues d'un inputbuf de blocage n'ont pas d'execution. Le LEFT JOIN
    // rendait NULL, la ligne etait comptee "sans echantillon" et n'etait jamais reclassee — mais
    // sa version etait quand meme portee, si bien que le projet se declarait a jour et que le
    // message conseillait --force, qui n'y pouvait rien.
    // -----------------------------------------------------------------------------------------

    /// <summary>Projet v1 dont l'unique signature ne vit que dans un rapport de blocage.</summary>
    private static string SeedBlockingOnlyProject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        using var db = DuckDbProject.Open(path);
        using var c = db.Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO normalized_queries
                (normalized_hash, normalized_sql, statement_kind, primary_table,
                 target_object, normalizer_version, first_seen_at, last_seen_at)
          VALUES ('hb','x','OTHER',null,null,1,'2026-08-13','2026-08-13');

          INSERT INTO blocking_processes
          VALUES (1,'blocker',53,0,'suspended',null,'Unknown',null,null,
                  null,null,null,null,null,null,null,
                  'ALTER TABLE AppSchema.WidgetScaling REBUILD','hb');
          """;
        c.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void Blocking_inputbuf_is_used_when_the_signature_has_no_execution()
    {
        var path = SeedBlockingOnlyProject();
        try
        {
            using var db = DuckDbProject.Open(path);
            var r = new Reclassifier(db).Run();

            Assert.Equal(1L, r.RowsExamined);
            Assert.Equal(1L, r.RowsChanged);
            Assert.Equal(0L, r.RowsWithoutSample);      // le texte etait la, il fallait le lire

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT statement_kind, primary_table FROM normalized_queries WHERE normalized_hash='hb'";
            using var rd = c.ExecuteReader();
            Assert.True(rd.Read());
            Assert.Equal("ALTER TABLE REBUILD", rd.GetString(0));
            Assert.Equal("AppSchema.WidgetScaling", rd.GetString(1));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Une signature dont aucun texte n'est conserve reste comptee a part, et sa version est tout
    /// de meme portee : c'est ce qui fait converger le traitement. Le message de l'hote doit donc
    /// dire "reimport", pas "--force" — ce que verifie
    /// <see cref="Force_does_not_change_the_count_for_rows_without_any_source_text"/>.
    /// </summary>
    [Fact]
    public void Force_does_not_change_the_count_for_rows_without_any_source_text()
    {
        var path = SeedV1Project();
        try
        {
            using var db = DuckDbProject.Open(path);
            var first = new Reclassifier(db).Run();
            Assert.Equal(1L, first.RowsWithoutSample);          // h3

            var forced = new Reclassifier(db).Run(force: true);
            Assert.Equal(first.RowsWithoutSample, forced.RowsWithoutSample);
        }
        finally { File.Delete(path); }
    }
}
