using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Presenters;

public class ImportPresenterTests
{
    [SkippableFact]
    public async Task RunAsync_imports_real_sample_and_reports_progress()
    {
        var file = SampleFile.FindSmallest();
        Skip.If(file is null, "sample/ folder with a .xel trace not present");

        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var presenter = new ImportPresenter(db);
            var ticks = new List<ImportProgress>();
            IProgress<ImportProgress> sync = new ListProgress(ticks.Add);

            var result = await presenter.RunAsync(file!, RedactionMode.Masked, sync, CancellationToken.None);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);
            Assert.Equal(1.0, ticks[^1].OverallFraction, 3);   // reaches 100% at the end
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
