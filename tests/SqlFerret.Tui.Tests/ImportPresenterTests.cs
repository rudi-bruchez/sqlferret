using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Presenters;

public class ImportPresenterTests
{
    // Walk up from the test bin dir to find the gitignored sample/ folder of .xel traces.
    // Uses the smallest .xel overall — the presenter just needs Read > 0 + progress; a workload trace is not required.
    private static string? FindSampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sample = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(sample))
            {
                var allXel = Directory.GetFiles(sample, "*.xel");
                if (allXel.Length > 0) return allXel.OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
            }
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public async Task RunAsync_imports_real_sample_and_reports_progress()
    {
        var file = FindSampleFile();
        Skip.If(file is null, "sample/ folder with a .xel trace not present");

        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var presenter = new ImportPresenter(db);
            var ticks = new List<IngestionProgress>();
            IProgress<IngestionProgress> sync = new ListProgress(ticks.Add);

            var result = await presenter.RunAsync(file!, RedactionMode.Masked, sync, CancellationToken.None);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);
            Assert.Equal(result.Read, ticks[^1].Read);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ListProgress(Action<IngestionProgress> a) : IProgress<IngestionProgress>
    { public void Report(IngestionProgress value) => a(value); }
}
