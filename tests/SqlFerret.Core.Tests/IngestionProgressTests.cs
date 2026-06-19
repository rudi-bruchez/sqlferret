// tests/SqlFerret.Core.Tests/IngestionProgressTests.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public class IngestionProgressTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    private static (IXeEventData, string, long) Batch(int i) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
            new Dictionary<string, object?> { ["batch_text"] = $"SELECT {i}", ["duration"] = (long)i },
            new Dictionary<string, object?>()), "s_0.xel", i);

    [Fact]
    public void Ingest_reports_progress_and_final_matches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, [], BatchSize: 2));

            // Progress<T> posts asynchronously; collect synchronously instead for a deterministic test:
            var captured = new List<IngestionProgress>();
            IProgress<IngestionProgress> sync = new SyncProgress(captured.Add);

            var result = svc.Ingest("logs/", Enumerable.Range(1, 5).Select(Batch), sync);

            Assert.Equal(5, result.Read);
            Assert.Equal(5, result.Mapped);
            Assert.NotEmpty(captured);                       // at least one progress tick
            Assert.Equal(result.Read, captured[^1].Read);    // last tick equals final counters
            Assert.Equal("s_0.xel", captured[^1].CurrentFile);
            Assert.True(captured.Select(p => p.Read).SequenceEqual(captured.Select(p => p.Read).OrderBy(x => x))); // monotonic
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class SyncProgress(Action<IngestionProgress> onReport) : IProgress<IngestionProgress>
    {
        public void Report(IngestionProgress value) => onReport(value);
    }
}
