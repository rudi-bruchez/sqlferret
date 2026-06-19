// tests/SqlFerret.Core.Tests/IngestionServiceTests.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class IngestionServiceTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    private static (IXeEventData, string, long) Batch(string sql, long offset, string db = "Sales") =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026,1,1),
            new Dictionary<string, object?> { ["batch_text"]=sql, ["duration"]=1000L },
            new Dictionary<string, object?> { ["database_name"]=db, ["session_id"]=1 }), "s_0.xel", offset);

    [Fact]
    public void Ingests_maps_normalizes_and_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var opts = new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>(), BatchSize: 2);
            var svc = new IngestionService(project, opts);

            var events = new[] {
                Batch("SELECT * FROM dbo.Users WHERE Id = 1", 0),
                Batch("SELECT * FROM dbo.Users WHERE Id = 2", 1),     // same signature
                (new FakeEvent("login", new DateTime(2026,1,1), new Dictionary<string,object?>(),
                    new Dictionary<string,object?>()), "s_0.xel", 2L), // unmapped
            };

            var result = svc.Ingest("logs/", events);

            Assert.Equal(3, result.Read);
            Assert.Equal(2, result.Mapped);
            Assert.Equal(1, result.Unmapped);

            using var c = project.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM normalized_queries";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar())); // grouped
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ingest_filter_drops_and_counts_cleaned()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var rule = new FilterRule("noise","database_name","eq",null,"tempdb","ingest","exclude",true);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, new[]{rule}));
            var result = svc.Ingest("logs/", new[] { Batch("SELECT 1", 0, db:"tempdb") });
            Assert.Equal(1, result.Cleaned);
            Assert.Equal(0, result.Mapped);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
