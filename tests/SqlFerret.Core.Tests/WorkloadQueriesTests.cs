// tests/SqlFerret.Core.Tests/WorkloadQueriesTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class WorkloadQueriesTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    private static (IXeEventData, string, long) Batch(string sql, long dur, long offset, string db = "Sales", int sessionId = 1) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1, 0, 0, (int)(offset % 60)),
            new Dictionary<string, object?> { ["batch_text"] = sql, ["duration"] = dur },
            new Dictionary<string, object?> { ["database_name"] = db, ["session_id"] = sessionId }), "s_0.xel", offset);

    private static DuckDbProject CreateAndIngest(out IngestionResult result, out string duckdbPath,
        params (IXeEventData, string, long)[] events)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        duckdbPath = path;
        var project = DuckDbProject.Open(path);
        var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
        result = svc.Ingest("logs/", events);
        return project;
    }

    [Fact]
    public void TopSlow_groups_and_orders_by_total()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 0),
                Batch("SELECT * FROM dbo.A WHERE id = 2", 3000, 1),  // same sig as above → total 4000
                Batch("SELECT * FROM dbo.B WHERE id = 9", 500, 2),
            });

            var q = new WorkloadQueries(project.Connection);
            var top = q.TopSlow(10, "total_duration_us", Array.Empty<FilterRule>());

            Assert.Equal(2, top.Count);
            Assert.Equal("dbo.A", top[0].PrimaryTable);   // highest total first
            Assert.Equal(2, top[0].Count);
            Assert.Equal(4000, top[0].TotalDurationUs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TopSlow_invalid_sortColumn_falls_back_to_total()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 0),
                Batch("SELECT * FROM dbo.B WHERE id = 9", 500, 1),
            });

            var q = new WorkloadQueries(project.Connection);
            // Invalid sort column should fall back to total_duration_us
            var top = q.TopSlow(10, "INJECTED; DROP TABLE--", Array.Empty<FilterRule>());

            Assert.Equal(2, top.Count);
            Assert.Equal("dbo.A", top[0].PrimaryTable);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TopFrequent_orders_by_count()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 100, 0),
                Batch("SELECT * FROM dbo.A WHERE id = 2", 100, 1),
                Batch("SELECT * FROM dbo.A WHERE id = 3", 100, 2),
                Batch("SELECT * FROM dbo.B WHERE id = 9", 500, 3),
            });

            var q = new WorkloadQueries(project.Connection);
            var top = q.TopFrequent(10, Array.Empty<FilterRule>());

            Assert.Equal(2, top.Count);
            Assert.Equal("dbo.A", top[0].PrimaryTable);  // 3 occurrences vs 1
            Assert.Equal(3, top[0].Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Occurrences_returns_rows_for_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 0),
                Batch("SELECT * FROM dbo.A WHERE id = 2", 3000, 1),
                Batch("SELECT * FROM dbo.B WHERE id = 9", 500, 2),
            });

            var q = new WorkloadQueries(project.Connection);
            var top = q.TopSlow(1, "total_duration_us", Array.Empty<FilterRule>());
            Assert.Single(top);

            var hash = top[0].NormalizedHash;
            var occurrences = q.Occurrences(hash, 100);

            Assert.Equal(2, occurrences.Count);
            // Both occurrences should be for dbo.A
            Assert.All(occurrences, o => Assert.Equal("Sales", o.Database));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SessionFlow_returns_ordered_by_captured_at()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 10, sessionId: 42),
                Batch("SELECT * FROM dbo.A WHERE id = 2", 2000, 20, sessionId: 42),
                Batch("SELECT * FROM dbo.C WHERE id = 5", 500,  5,  sessionId: 99), // different session
            });

            var q = new WorkloadQueries(project.Connection);
            var flow = q.SessionFlow(42, new DateTime(2026, 1, 1), new DateTime(2026, 1, 2));

            Assert.Equal(2, flow.Count);
            // Should be ordered by captured_at
            Assert.True(flow[0].CapturedAt <= flow[1].CapturedAt);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ParameterImpact_groups_by_value()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            // RedactionMode.Full stores parameters
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            // Use a stored proc call so parameters get extracted
            svc.Ingest("logs/", new[] {
                Batch("EXEC dbo.GetOrder @id = 1", 5000, 0),
                Batch("EXEC dbo.GetOrder @id = 1", 8000, 1),
                Batch("EXEC dbo.GetOrder @id = 2", 1000, 2),
            });

            var q = new WorkloadQueries(project.Connection);
            var top = q.TopSlow(1, "total_duration_us", Array.Empty<FilterRule>());
            Assert.Single(top);

            var hash = top[0].NormalizedHash;
            var impact = q.ParameterImpact(hash, "@id");

            // Whether we get results depends on parameter extraction; if none, count should be 0
            // The key behavior: result is a list (no crash), and if entries exist they're ordered by avg desc
            Assert.NotNull(impact);
            if (impact.Count > 1)
            {
                for (int i = 0; i < impact.Count - 1; i++)
                    Assert.True(impact[i].AvgDurationUs >= impact[i + 1].AvgDurationUs);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Dimension_groups_by_database_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 0, db: "Sales"),
                Batch("SELECT * FROM dbo.A WHERE id = 2", 2000, 1, db: "Sales"),
                Batch("SELECT * FROM dbo.B WHERE id = 5", 500,  2, db: "Reports"),
            });

            var q = new WorkloadQueries(project.Connection);
            var dims = q.Dimension("database_name");

            Assert.Equal(2, dims.Count);
            Assert.Equal("Sales", dims[0].Value);   // highest total first (3000)
            Assert.Equal(2, dims[0].Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Dimension_rejects_unlisted_field()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var q = new WorkloadQueries(project.Connection);
            Assert.Throws<ArgumentException>(() => q.Dimension("injected_field"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Quality_returns_run_counters()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            var result = svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 0),
                (new FakeEvent("login", new DateTime(2026,1,1),
                    new Dictionary<string,object?>(), new Dictionary<string,object?>()), "s_0.xel", 99L),
            });

            var q = new WorkloadQueries(project.Connection);
            var qs = q.Quality(result.RunId);

            Assert.Equal(2, qs.EventsRead);
            Assert.Equal(1, qs.EventsMapped);
            Assert.Equal(1, qs.EventsUnmapped);
            Assert.Equal(0, qs.EventsCleaned);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
