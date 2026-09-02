// tests/SqlFerret.Core.Tests/AdHocQueryTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Storage;
using Xunit;

public class AdHocQueryTests
{
    private static string NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        using var db = DuckDbProject.Open(path);
        using var c = db.Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO normalized_queries
                (normalized_hash, normalized_sql, statement_kind, primary_table,
                 target_object, normalizer_version, first_seen_at, last_seen_at)
          VALUES ('h1','s1','CREATE INDEX','dbo.T','IX_A',2,'2026-08-13','2026-08-13'),
                 ('h2','s2','SELECT','dbo.U',null,2,'2026-08-13','2026-08-13'),
                 ('h3','s3','DROP INDEX','dbo.T','IX_B',2,'2026-08-13','2026-08-13')
          """;
        c.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void Returns_columns_and_raw_values()
    {
        var path = NewDb();
        try
        {
            using var db = DuckDbProject.OpenReadOnly(path);
            var t = new AdHocQuery(db.Connection)
                .Run("SELECT statement_kind, target_object FROM normalized_queries ORDER BY normalized_hash");

            Assert.Equal(["statement_kind", "target_object"], t.Columns);
            Assert.Equal(3, t.Rows.Count);
            Assert.Equal("CREATE INDEX", t.Rows[0][0]);
            Assert.Null(t.Rows[1][1]);          // NULL SQL -> null CLR, pas DBNull
            Assert.False(t.Truncated);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Limit_truncates_and_flags()
    {
        var path = NewDb();
        try
        {
            using var db = DuckDbProject.OpenReadOnly(path);
            var t = new AdHocQuery(db.Connection)
                .Run("SELECT normalized_hash FROM normalized_queries ORDER BY normalized_hash", limit: 2);

            Assert.Equal(2, t.Rows.Count);
            Assert.True(t.Truncated);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Limit_not_reached_is_not_truncated()
    {
        var path = NewDb();
        try
        {
            using var db = DuckDbProject.OpenReadOnly(path);
            var t = new AdHocQuery(db.Connection)
                .Run("SELECT normalized_hash FROM normalized_queries", limit: 10);

            Assert.Equal(3, t.Rows.Count);
            Assert.False(t.Truncated);
        }
        finally { File.Delete(path); }
    }
}
