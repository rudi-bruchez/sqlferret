// tests/SqlFerret.Core.Tests/SqlTextPolicyStorageTests.cs
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Storage;
using Xunit;

public class SqlTextPolicyStorageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    [Fact]
    public void BeginRun_records_the_policy_and_the_sanitizer_version()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked", SqlTextSanitization.Literals);

            using var c = db.Connection.CreateCommand();
            c.CommandText =
                "SELECT sql_text_policy, sql_text_sanitizer_version FROM ingestion_runs WHERE run_id = " + run;
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("literals", r.GetString(0));
            Assert.Equal(SqlTextSanitizer.Version, r.GetInt32(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BeginRun_defaults_to_raw()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked");

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT sql_text_policy FROM ingestion_runs WHERE run_id = " + run;
            Assert.Equal("raw", (string)c.ExecuteScalar()!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FinishRun_records_the_sanitize_failure_count()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked", SqlTextSanitization.Literals);
            db.FinishRun(run, read: 1, mapped: 1, unmapped: 0, cleaned: 0, tokenizeFailures: 1,
                blocking: 0, deadlocks: 0, blockingParseFailures: 0, sqlTextSanitizeFailures: 1);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT sql_text_sanitize_failures FROM ingestion_runs WHERE run_id = " + run;
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Simulates a project created before this change: drop the three columns from an open
    // project, insert a legacy-shaped row, close, reopen. Initialize must re-add them and the
    // historical row must read as NULL — which callers interpret as "raw, pre-versioning".
    [Fact]
    public void Reopening_a_project_without_the_columns_migrates_and_reads_null()
    {
        var path = TempDb();
        try
        {
            long run;
            using (var db = DuckDbProject.Open(path))
            {
                run = db.BeginRun("logs/", 1, 0, "masked");
                using var drop = db.Connection.CreateCommand();
                drop.CommandText = """
                  ALTER TABLE ingestion_runs DROP COLUMN sql_text_policy;
                  ALTER TABLE ingestion_runs DROP COLUMN sql_text_sanitizer_version;
                  ALTER TABLE ingestion_runs DROP COLUMN sql_text_sanitize_failures;
                  """;
                drop.ExecuteNonQuery();
            }

            using var reopened = DuckDbProject.Open(path);
            using var c = reopened.Connection.CreateCommand();
            c.CommandText =
                "SELECT sql_text_policy, sql_text_sanitizer_version FROM ingestion_runs WHERE run_id = " + run;
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.True(r.IsDBNull(0));
            Assert.True(r.IsDBNull(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
