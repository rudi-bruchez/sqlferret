using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;
using Xunit;

public class QueryStoreImportServiceTests
{
    [Fact]
    public void Version_is_one()
        => Assert.Equal(1, QueryStoreImportService.Version);

    [SkippableFact]
    public void Import_loads_query_store_end_to_end()
    {
        var connStr = Environment.GetEnvironmentVariable("SQLFERRET_TEST_CONN");
        Skip.If(string.IsNullOrEmpty(connStr), "SQLFERRET_TEST_CONN not set — skipping integration test");

        var dbPath = Path.Combine(Path.GetTempPath(), $"qds_it_{Guid.NewGuid():N}.duckdb");
        var plansDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var db = DuckDbProject.Open(dbPath);
            var svc = new QueryStoreImportService(connStr!, db, plansDir);
            var result = svc.Import(new QueryStoreImportOptions(Database: null, WritePlans: true, Window: default));

            Assert.True(result.QueriesCount > 0, "expected at least one query in the target DB's Query Store");
            Assert.True(result.RuntimeStatRows > 0);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM qds_runs"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM qds_runtime_stats"; Assert.True(Convert.ToInt64(c.ExecuteScalar()) > 0);

            if (result.PlanFilesWritten > 0)
                Assert.True(Directory.GetFiles(Path.Combine(plansDir, "qds"), "*.sqlplan").Length > 0);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            Directory.Delete(plansDir, true);
        }
    }

    [SkippableFact]
    public void Import_clean_error_when_query_store_off()
    {
        var connStr = Environment.GetEnvironmentVariable("SQLFERRET_TEST_CONN_NOQDS");
        Skip.If(string.IsNullOrEmpty(connStr), "SQLFERRET_TEST_CONN_NOQDS not set — skipping");
        var dbPath = Path.Combine(Path.GetTempPath(), $"qds_off_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(dbPath);
            var svc = new QueryStoreImportService(connStr!, db, Directory.CreateTempSubdirectory().FullName);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                svc.Import(new QueryStoreImportOptions(null, true, default)));
            Assert.Contains("Query Store", ex.Message);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }
}
