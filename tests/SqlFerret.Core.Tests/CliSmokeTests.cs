// tests/SqlFerret.Core.Tests/CliSmokeTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public class CliSmokeTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    [Fact]
    public void End_to_end_ingest_then_query()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            var ev = new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
                new Dictionary<string, object?> { ["batch_text"] = "SELECT 1", ["duration"] = 10L },
                new Dictionary<string, object?>());
            svc.Ingest("logs/", new[] { ((IXeEventData)ev, "s_0.xel", 0L) });

            var top = new WorkloadQueries(db.Connection).TopSlow(10, "total_duration_us", Array.Empty<FilterRule>());
            Assert.Single(top);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
