// tests/SqlFerret.Core.Tests/SqlTextPolicyGateTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;
using Xunit;

public class SqlTextPolicyGateTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Batch(string sql) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
            new Dictionary<string, object?> { ["batch_text"] = sql, ["duration"] = 1000L },
            new Dictionary<string, object?> { ["database_name"] = "Sales", ["session_id"] = 1 }),
         "s_0.xel", 0L);

    [Fact]
    public void LoadExecution_projects_the_run_policy()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch("SELECT 1")]);

            var ev = new WorkloadQueries(db.Connection).LoadExecution(1);
            Assert.Equal("literals", ev.SqlTextPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoadExecution_reports_raw_for_an_unsanitized_run()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Batch("SELECT 1")]);

            Assert.Equal("raw", new WorkloadQueries(db.Connection).LoadExecution(1).SqlTextPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The refusal must happen before any connection attempt: the connection string below is
    // unusable, so a SqlException instead of our InvalidOperationException means the gate is
    // in the wrong place.
    [Fact]
    public async Task CaptureAsync_refuses_a_sanitized_execution_before_connecting()
    {
        var ev = new ExecutionEvent
        {
            EventName = "sql_batch_completed",
            SqlTextRaw = "select * from dbo.Customers where Email = ?",
            XeFileName = "s_0.xel",
            SqlTextPolicy = "literals",
        };
        var svc = new EstimatedPlanService("Server=(invalid);Connect Timeout=1", Path.GetTempPath());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureAsync(ev, "plan1"));

        Assert.Contains("--sanitize-sql-text literals", ex.Message);
        Assert.Contains("not executable", ex.Message);
    }

    [Fact]
    public async Task CaptureAsync_does_not_refuse_when_the_policy_is_raw_or_unknown()
    {
        var ev = new ExecutionEvent
        {
            EventName = "sql_batch_completed",
            SqlTextRaw = "SELECT 1",
            XeFileName = "s_0.xel",
            SqlTextPolicy = null,       // legacy project
        };
        var svc = new EstimatedPlanService("Server=(invalid);Connect Timeout=1", Path.GetTempPath());

        // Reaches the connection attempt and fails there. The assertion is that the failure is
        // NOT our refusal — a legacy (null policy) execution must still be attempted.
        // This is the one network-dependent assertion in an otherwise offline suite. If it proves
        // slow or flaky in practice, assert on the exception TYPE instead of driving a real
        // connection — the point is only that InvalidOperationException with our message is absent.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => svc.CaptureAsync(ev, "plan1"));
        Assert.DoesNotContain("not executable", ex.Message);
    }
}
