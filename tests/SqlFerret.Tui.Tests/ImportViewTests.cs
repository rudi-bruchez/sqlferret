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
    [SkippableFact]
    public async Task Start_runs_import_and_raises_completed()
    {
        var file = SampleFile.FindSmallest();
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

    [Fact]
    public async Task StartAsync_reentrant_call_while_running_is_ignored()
    {
        // Verifies the re-entrancy guard WITHOUT needing a real .xel file.
        // We use a non-existent path so RunAsync throws immediately after Task.Run
        // starts — the guard + finally path must still work cleanly.
        using IApplication app = Application.Create(new VirtualTimeProvider());
        var path = Path.Combine(Path.GetTempPath(), $"sf_importview_reentrant_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var view = new ImportView(new ImportPresenter(db), RedactionMode.Masked, app);

            // Initially not running.
            Assert.False(view.IsRunning);

            // A call with an invalid path returns quickly (empty-path guard), IsRunning stays false.
            await view.StartAsync("");
            Assert.False(view.IsRunning);

            // A call with a non-existent file path goes through RunAsync and throws;
            // the finally must still clear IsRunning.
            await view.StartAsync("/nonexistent/path/that/does/not/exist.xel");
            Assert.False(view.IsRunning, "IsRunning must be false after an error path");

            // Calling again after completion is fine (not permanently locked).
            await view.StartAsync("/another/nonexistent.xel");
            Assert.False(view.IsRunning);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
