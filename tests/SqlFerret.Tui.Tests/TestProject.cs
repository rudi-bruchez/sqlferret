// tests/SqlFerret.Tui.Tests/TestProject.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public sealed class SeededProject(DuckDbProject project, string path) : IDisposable
{
    public DuckDbProject Project => project;
    public DuckDB.NET.Data.DuckDBConnection Connection => project.Connection;
    public void Dispose() { project.Dispose(); try { File.Delete(path); } catch { /* best-effort */ } }
}

public static class TestProject
{
    private sealed record FakeEvent(
        string Name,
        DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    /// <summary>
    /// Opens a temp DuckDB, ingests the supplied rows via IngestionService with
    /// RedactionMode.Full, and returns a <see cref="SeededProject"/> (caller disposes).
    /// </summary>
    /// <param name="rows">
    /// Each tuple: (name, sql, objectName, durationUs).
    /// name containing "batch" → sql in batch_text field.
    /// otherwise (rpc/statement) → sql in statement field; objectName sets object_name.
    /// </param>
    public static SeededProject SeedFrom(
        IEnumerable<(string name, string sql, string? objectName, long durationUs)> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_tui_{Guid.NewGuid():N}.duckdb");
        var project = DuckDbProject.Open(path);

        var events = rows.Select((r, i) => BuildEvent(r.name, r.sql, r.objectName, r.durationUs, i)).ToList();

        var svc = new IngestionService(project,
            new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));

        svc.Ingest("test/", events);

        return new SeededProject(project, path);
    }

    private static (IXeEventData, string, long) BuildEvent(
        string eventName, string sql, string? objectName, long durationUs, int index)
    {
        var fields = new Dictionary<string, object?>();
        if (eventName.Contains("batch", StringComparison.OrdinalIgnoreCase))
        {
            fields["batch_text"] = sql;
        }
        else
        {
            fields["statement"] = sql;
            if (objectName is not null)
                fields["object_name"] = objectName;
        }
        fields["duration"] = durationUs;

        var actions = new Dictionary<string, object?>
        {
            ["database_name"] = "TestDb",
            ["session_id"] = 1,
        };

        var ts = new DateTime(2026, 1, 1, 0, 0, index % 60);
        return (new FakeEvent(eventName, ts, fields, actions), "test_0.xel", (long)index);
    }
}
