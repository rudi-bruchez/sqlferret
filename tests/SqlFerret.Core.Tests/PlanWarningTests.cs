using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

public class PlanWarningTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static PlanProfile P() => new()
    {
        PlanHash = "A",
        PlanHashSource = "queryplanhash",
        FileStem = "p_A",
        StatementCount = 1,
        CapturedAt = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
        DurationUs = 1,
    };

    private static FakeEvent Rpc(string? queryHash) => new("rpc_completed",
        new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
        new Dictionary<string, object?> { ["statement"] = "SELECT 1", ["duration"] = 1000L },
        queryHash is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["query_hash"] = queryHash });

    [Fact]
    public void Warns_when_plans_exist_but_no_execution_carries_query_hash()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("t.xel", [((IXeEventData)Rpc(null), "t.xel", 0L)]);
            db.InsertPlanProfileBatch(result.RunId, [new PreparedPlanProfile(P(), PlanWriteOutcome.WroteFirst)]);

            var warning = db.PlanCorrelationWarning(result.RunId);

            Assert.NotNull(warning);
            Assert.Contains("sqlserver.query_hash", warning);
            Assert.Contains("docs/capture-session.md", warning);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Silent_when_executions_carry_query_hash()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("t.xel", [((IXeEventData)Rpc("0xAABB"), "t.xel", 0L)]);
            db.InsertPlanProfileBatch(result.RunId, [new PreparedPlanProfile(P(), PlanWriteOutcome.WroteFirst)]);

            Assert.Null(db.PlanCorrelationWarning(result.RunId));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Une trace ne capturant QUE des plans reprocherait sinon l'absence d'une action sur
    // des événements qui ne sont pas dans la trace : juste sur le fond, trompeur sur la cause.
    [Fact]
    public void Silent_when_the_run_has_no_execution_at_all()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("t.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [new PreparedPlanProfile(P(), PlanWriteOutcome.WroteFirst)]);

            Assert.Null(db.PlanCorrelationWarning(runId));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Silent_when_the_run_has_no_plan()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Off, []))
                .Ingest("t.xel", [((IXeEventData)Rpc(null), "t.xel", 0L)]);

            Assert.Null(db.PlanCorrelationWarning(result.RunId));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
