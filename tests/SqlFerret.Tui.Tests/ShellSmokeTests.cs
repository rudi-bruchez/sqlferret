// tests/SqlFerret.Tui.Tests/ShellSmokeTests.cs
// Headless smoke test for MainWindow — verifies construction does not throw.
// NO app.Init() call: Application.Create(VirtualTimeProvider) is the headless entry-point.
using SqlFerret.Core.Config;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Shell;
using Terminal.Gui.App;
using Terminal.Gui.Time;

public class ShellSmokeTests
{
    [Fact]
    public void MainWindow_constructs_without_throwing()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());

        var config = SqlFerretConfig.Load(null);
        var ui = new UiState();
        var uiPath = Path.GetTempFileName();

        using var seeded = TestProject.SeedFrom(
        [
            ("rpc_completed", "EXEC dbo.GetOrder @id = 1", (string?)"dbo.GetOrder", 9_000L),
        ]);

        var clipboard = new NativeClipboard(new FileFallbackClipboard(Path.GetTempPath()), null);
        var ctx = new TuiContext(app, seeded.Project, config, ui, clipboard, uiPath);

        var win = new MainWindow(ctx);
        Assert.NotNull(win);
        win.Dispose();
    }
}
