// tests/SqlFerret.Core.Tests/CliSmokeTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public class CliSmokeTests
{
    [Fact]
    public void End_to_end_ingest_then_query()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, []));
            var ev = new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
                new Dictionary<string, object?> { ["batch_text"] = "SELECT 1", ["duration"] = 10L },
                new Dictionary<string, object?>());
            svc.Ingest("logs/", [((IXeEventData)ev, "s_0.xel", 0L)]);

            var top = new WorkloadQueries(db.Connection).TopSlow(10, "total_duration_us", []);
            Assert.Single(top);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void Import_sequence_on_a_sample_trace_produces_a_run_folder_whose_paths_all_exist()
    {
        var sampleDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample");
        var sample = Directory.Exists(sampleDir)
            ? Directory.EnumerateFiles(sampleDir, "*.xel", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        Skip.If(sample is null, "sample/ absent — test ignoré hors poste de développement");

        var projectDir = Path.Combine(Path.GetTempPath(), $"sf_cli_{Guid.NewGuid():N}");
        try
        {
            var project = SqlFerret.Core.Project.AuditProject.OpenOrCreate(projectDir);
            using var db = project.OpenDb();
            long runId = db.PeekNextRunId();
            var options = new IngestionOptions(RedactionMode.Off, [],
                PlanProfileDir: project.PlanProfileRunFolder(runId));
            var result = ImportRunner.Run(db, options, sample!, null);

            db.FinalizePlanRun(result.RunId);
            Skip.If(result.PlanProfiles == 0, "l'échantillon ne contient aucun plan");

            var rows = db.ReadPlanDigestRows(result.RunId);
            var writer = new SqlFerret.Core.Plans.PlanArtifactWriter(project.PlanProfileRunFolder(result.RunId));
            writer.WriteDigests(rows);
            writer.WriteIndex(rows);

            var dir = project.PlanProfileRunFolder(result.RunId);
            Assert.True(File.Exists(Path.Combine(dir, "index.json")));
            Assert.NotEmpty(Directory.GetFiles(dir, "*.sqlplan"));
            foreach (var r in rows)
            {
                if (r.First is { } f) Assert.True(File.Exists(Path.Combine(dir, f.Path)), f.Path);
                if (r.Worst is { } w) Assert.True(File.Exists(Path.Combine(dir, w.Path)), w.Path);
            }
        }
        finally { if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true); }
    }
}
