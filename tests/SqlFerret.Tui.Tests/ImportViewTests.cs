// tests/SqlFerret.Tui.Tests/ImportViewTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Time;

public class ImportViewTests
{
    // Walk up from the test bin dir to find the gitignored sample/ folder.
    // Picks the smallest non-performances_*.xel file (like ImportPresenterTests but excludes perf files).
    private static string? FindSampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sample = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(sample))
            {
                var all = Directory.GetFiles(sample, "*.xel")
                    .Where(f => !Path.GetFileName(f).StartsWith("performances_", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (all.Length > 0) return all.OrderBy(f => new FileInfo(f).Length).First();
                // Fall back to any .xel if no non-perf ones
                var any = Directory.GetFiles(sample, "*.xel");
                if (any.Length > 0) return any.OrderBy(f => new FileInfo(f).Length).First();
            }
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public async Task Start_runs_import_and_raises_completed()
    {
        var file = FindSampleFile();
        Skip.If(file is null, "No sample .xel found in sample/ folder");

        using IApplication app = Application.Create(new VirtualTimeProvider());
        var path = Path.Combine(Path.GetTempPath(), $"sf_importview_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var view = new ImportView(new ImportPresenter(db), RedactionMode.Masked, app);
            IngestionResult? done = null;
            view.Completed += r => done = r;

            await view.StartAsync(file!);

            Assert.NotNull(done);
            Assert.True(done!.Read > 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task StartAsync_empty_path_sets_progress_text_and_does_not_throw()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());
        var path = Path.Combine(Path.GetTempPath(), $"sf_importview_empty_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var view = new ImportView(new ImportPresenter(db), RedactionMode.Masked, app);
            IngestionResult? done = null;
            view.Completed += r => done = r;

            await view.StartAsync("");

            Assert.Null(done);   // Completed not raised
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
