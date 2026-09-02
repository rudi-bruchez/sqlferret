// tests/SqlFerret.Core.Tests/DuckDbProjectReadOnlyTests.cs
using SqlFerret.Core.Storage;
using Xunit;

public class DuckDbProjectReadOnlyTests
{
    [Fact]
    public void ReadOnly_connection_refuses_writes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var _ = DuckDbProject.Open(path)) { }   // crée le schéma puis referme

            using var ro = DuckDbProject.OpenReadOnly(path);

            using var read = ro.Connection.CreateCommand();
            read.CommandText = "SELECT count(*) FROM normalized_queries";
            Assert.Equal(0L, Convert.ToInt64(read.ExecuteScalar()));

            using var write = ro.Connection.CreateCommand();
            write.CommandText = """
              INSERT INTO normalized_queries
                    (normalized_hash, normalized_sql, statement_kind, primary_table,
                     target_object, normalizer_version, first_seen_at, last_seen_at)
              VALUES ('h', 's', 'SELECT', null, null, 2, '2026-08-13', '2026-08-13')
              """;
            Assert.ThrowsAny<Exception>(() => { write.ExecuteNonQuery(); });
        }
        finally { File.Delete(path); }
    }
}
