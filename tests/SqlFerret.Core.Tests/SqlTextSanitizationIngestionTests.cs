// tests/SqlFerret.Core.Tests/SqlTextSanitizationIngestionTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class SqlTextSanitizationIngestionTests
{
    private const string Pii = "alice@example.com";

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Batch(string sql, long offset = 0) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
            new Dictionary<string, object?> { ["batch_text"] = sql, ["duration"] = 1000L },
            new Dictionary<string, object?> { ["database_name"] = "Sales", ["session_id"] = 1 }),
         "s_0.xel", offset);

    private static string Scalar(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = sql;
        return (string)c.ExecuteScalar()!;
    }

    [Fact]
    public void Raw_keeps_the_statement_verbatim()
    {
        const string sql = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Batch(sql)]);

            Assert.Equal(sql, Scalar(db, "SELECT sql_text_raw FROM executions"));
            // Guards against the two counters double-counting one event.
            Assert.Equal(0, result.SqlTextSanitizeFailures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Literals_removes_the_inlined_literal_from_executions()
    {
        const string sql = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch(sql)]);

            var stored = Scalar(db, "SELECT sql_text_raw FROM executions");
            Assert.DoesNotContain(Pii, stored);
            Assert.Contains("?", stored);
            Assert.Contains("Customers", stored);   // identifiers survive: values, not schema
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The regression test for the defect found in revision 1 of the design: protecting only
    // sql_text_raw moves the literal into normalized_queries, in the same file, joinable on
    // normalized_hash. This test is not optional.
    [Fact]
    public void Literals_on_tokenize_failure_scrubs_normalized_queries_too()
    {
        const string truncated = $"exec dbo.X @Email='{Pii}";   // unterminated string literal
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch(truncated)]);

            Assert.DoesNotContain(Pii, Scalar(db, "SELECT sql_text_raw FROM executions"));
            Assert.DoesNotContain(Pii, Scalar(db, "SELECT normalized_sql FROM normalized_queries"));
            Assert.Equal(1, result.TokenizeFailures);
            Assert.Equal(1, result.SqlTextSanitizeFailures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Sanitization_does_not_change_the_fingerprint()
    {
        const string sql = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";
        var rawPath = TempDb();
        var litPath = TempDb();
        try
        {
            string rawHash, litHash;
            using (var db = DuckDbProject.Open(rawPath))
            {
                new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                    .Ingest("logs/", [Batch(sql)]);
                rawHash = Scalar(db, "SELECT normalized_hash FROM executions");
            }
            using (var db = DuckDbProject.Open(litPath))
            {
                new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                        SqlText: SqlTextSanitization.Literals))
                    .Ingest("logs/", [Batch(sql)]);
                litHash = Scalar(db, "SELECT normalized_hash FROM executions");
            }
            Assert.Equal(rawHash, litHash);
        }
        finally
        {
            if (File.Exists(rawPath)) File.Delete(rawPath);
            if (File.Exists(litPath)) File.Delete(litPath);
        }
    }

    [Fact]
    public void Ingest_records_the_policy_on_the_run()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch("SELECT 1")]);

            Assert.Equal("literals",
                Scalar(db, "SELECT sql_text_policy FROM ingestion_runs WHERE run_id = " + result.RunId));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
