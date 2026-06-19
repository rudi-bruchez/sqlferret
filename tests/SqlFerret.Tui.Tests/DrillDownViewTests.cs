// tests/SqlFerret.Tui.Tests/DrillDownViewTests.cs
// Headless render test: DrillDownView builds its DataTable from the presenter.
// Pattern mirrors TopSlowViewTests — Application.Create(VirtualTimeProvider), no app.Init().
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Parameters;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Time;

public class DrillDownViewTests
{
    [Fact]
    public void View_lists_occurrences_and_copies_replay()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 1", "dbo.GetOrder", 4000L),
        ]);

        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var dir = Directory.CreateTempSubdirectory().FullName;
        var view = new DrillDownView(
            new DrillDownPresenter(q, sig),
            new FileFallbackClipboard(dir),
            "ms");

        view.Reload();
        Assert.Equal(1, view.OccurrenceCount);

        var res = view.CopySelectedReplay();
        Assert.True(File.Exists(res.FilePath));
        Assert.Contains("EXEC dbo.GetOrder", File.ReadAllText(res.FilePath!));

        view.Dispose();
    }

    [Fact]
    public void CopySelectedReplay_description_contains_redaction_warning_when_params_are_hashed()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 123", "dbo.GetOrder", 4000L),
        ], RedactionMode.Hash);

        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var dir = Directory.CreateTempSubdirectory().FullName;
        var view = new DrillDownView(
            new DrillDownPresenter(q, sig),
            new FileFallbackClipboard(dir),
            "ms");

        view.Reload();
        var res = view.CopySelectedReplay();

        Assert.Contains("WARNING: parameter values are redacted", res.Description);

        view.Dispose();
    }
}
