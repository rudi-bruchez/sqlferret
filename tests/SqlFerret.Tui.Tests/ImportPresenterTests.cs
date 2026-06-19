using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Presenters;
using Xunit;

public class ImportPresenterTests
{
    // Walk up from the test bin dir to find the gitignored sample/ folder of real .xel traces.
    private static string? FindSampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sample = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(sample))
            {
                var perf = Directory.GetFiles(sample, "performances_*.xel");
                if (perf.Length > 0) return perf.OrderBy(f => new FileInfo(f).Length).First();
            }
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public async Task RunAsync_imports_real_sample_and_reports_progress()
    {
        var file = FindSampleFile();
        Skip.If(file is null, "sample/ folder with a performances_*.xel trace not present");

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
