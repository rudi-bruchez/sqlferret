// tests/SqlFerret.Core.Tests/DuckDbProjectSchemaTests.cs
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
}
