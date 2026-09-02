// tests/SqlFerret.Core.Tests/StaleVersionWarningTests.cs
using SqlFerret.Core.Storage;
using Xunit;

public class StaleVersionWarningTests
{
    [Fact]
    public void Detects_rows_below_current_normalizer_version()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            Assert.False(db.HasStaleClassification());

            using var c = db.Connection.CreateCommand();
            c.CommandText = """
              INSERT INTO normalized_queries
                    (normalized_hash, normalized_sql, statement_kind, primary_table,
                     target_object, normalizer_version, first_seen_at, last_seen_at)
              VALUES ('h1','x','OTHER',null,null,1,'2026-08-13','2026-08-13')
              """;
            c.ExecuteNonQuery();

            Assert.True(db.HasStaleClassification());
        }
        finally { File.Delete(path); }
    }
}
