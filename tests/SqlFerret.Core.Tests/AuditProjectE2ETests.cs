using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Project;
using Xunit;

public class AuditProjectE2ETests
{
    [Fact]
    public void Open_project_then_ingest_then_query()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ape2e_{Guid.NewGuid():N}");
        try
        {
            var project = AuditProject.OpenOrCreate(dir);
            using var db = project.OpenDb();
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, []));
            var ev = new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
                new Dictionary<string, object?> { ["batch_text"] = "SELECT 1", ["duration"] = 10L },
                new Dictionary<string, object?>());
            svc.Ingest("logs/", [((IXeEventData)ev, "s_0.xel", 0L)]);

            var top = new WorkloadQueries(db.Connection).TopSlow(10, "total_duration_us", []);
            Assert.Single(top);
            Assert.True(File.Exists(project.DuckDbPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
