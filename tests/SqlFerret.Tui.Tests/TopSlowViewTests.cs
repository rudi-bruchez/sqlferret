// tests/SqlFerret.Tui.Tests/TopSlowViewTests.cs
// Headless render test: TopSlowView builds its DataTable from the presenter.
// Pattern mirrors ShellSmokeTests.cs — Application.Create(VirtualTimeProvider), no app.Init().
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Time;

public class TopSlowViewTests
{
    [Fact]
    public void View_builds_rows_from_presenter()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var seeded = TestProject.SeedFrom(
        [
            ("sql_batch_completed", "SELECT * FROM dbo.Users WHERE Id = 1", (string?)null, 5_000L),
        ]);

        var presenter = new TopSlowPresenter(seeded.Project);
        var view = new TopSlowView(presenter, "ms", app);
        view.Reload();

        Assert.Equal(1, view.RowCount);

        view.Dispose();
    }
}
