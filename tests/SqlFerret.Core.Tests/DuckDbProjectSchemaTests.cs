// tests/SqlFerret.Core.Tests/DuckDbProjectSchemaTests.cs
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Storage;
using Xunit;

public class DuckDbProjectSchemaTests
{
    [Fact]
    public void Open_creates_all_tables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var project = DuckDbProject.Open(path))
            {
                using var cmd = project.Connection.CreateCommand();
                cmd.CommandText =
                    "SELECT count(*) FROM information_schema.tables " +
                    "WHERE table_name IN ('ingestion_runs','executions','normalized_queries','execution_parameters')";
                Assert.Equal(4L, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Normalized_queries_carries_target_object_and_v2()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                using var c = db.Connection.CreateCommand();
                c.CommandText = """
                  INSERT INTO normalized_queries
                        (normalized_hash, normalized_sql, statement_kind, primary_table,
                         target_object, normalizer_version, first_seen_at, last_seen_at)
                  VALUES ('h1', 'CREATE INDEX ? ON ?', 'CREATE INDEX', 'dbo.T',
                          'IX_A', 2, '2026-08-13', '2026-08-13')
                  """;
                c.ExecuteNonQuery();

                using var r = db.Connection.CreateCommand();
                r.CommandText = "SELECT target_object FROM normalized_queries WHERE normalized_hash = 'h1'";
                Assert.Equal("IX_A", (string?)r.ExecuteScalar());
            }
            // Garde-fou volontaire : toute evolution d'AstClassifier qui change une classification
            // doit incrementer la version, sinon les projets deja importes gardent l'ancienne sans
            // que HasStaleClassification ne le signale.
            Assert.Equal(4, QueryNormalizer.Version);
        }
        finally { File.Delete(path); }
    }
}
