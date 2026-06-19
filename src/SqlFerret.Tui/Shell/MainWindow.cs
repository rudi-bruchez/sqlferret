// src/SqlFerret.Tui/Shell/MainWindow.cs
// Master/detail TUI shell: left view-rail + right content host + status bar.
//
// TG 2.4.6 notes:
//   - Window is Terminal.Gui.Views.Window (extends Runnable, not ViewBase.Window)
//   - StatusBar takes IEnumerable<Shortcut>; no StatusItem exists
//   - ListWrapper<T> wraps ObservableCollection<T>
//   - Dim.Fill(Dim.Absolute(n)) not Dim.Fill(n)
//   - Pos.AnchorEnd(n) for bottom anchor
//   - lv.ValueChanged: EventHandler<ValueChangedEventArgs<int?>>
using System.Collections.ObjectModel;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SqlFerret.Tui.Shell;

/// <summary>
/// Context record passed through all TUI views.
/// Named TuiContext (not AppContext) to avoid collision with System.AppContext.
/// Uses the project-local IClipboard (SqlFerret.Tui.Clipboard.IClipboard) to avoid
/// ambiguity with Terminal.Gui.App.IClipboard.
/// </summary>
public record TuiContext(
    IApplication App,
    DuckDbProject Project,
    SqlFerretConfig Config,
    UiState Ui,
    SqlFerret.Tui.Clipboard.IClipboard Clipboard,
    string UiStatePath);

/// <summary>
/// Root TUI window: view-rail on the left, content host on the right, status bar at bottom.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TuiContext _ctx;
    private readonly FrameView _contentHost;

    public MainWindow(TuiContext ctx)
    {
        _ctx = ctx;

        // Title shows the database path / name
        Title = $"SqlFerret — {ctx.Project.Connection.Database}";

        // ── Status bar ───────────────────────────────────────────────────────
        var statusBar = new StatusBar(new[]
        {
            new Shortcut(Keys.Quit, "Quit", () => ctx.App.RequestStop()),
        });
        statusBar.X = Pos.Absolute(0);
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        statusBar.Height = Dim.Absolute(1);

        // ── Left rail ────────────────────────────────────────────────────────
        var rail = new FrameView { Title = "Views" };
        rail.X = Pos.Absolute(0);
        rail.Y = Pos.Absolute(0);
        rail.Width = Dim.Absolute(18);
        rail.Height = Dim.Fill(Dim.Absolute(1));   // leave 1 row for status bar

        var items = new ObservableCollection<string> { "Top Slow", "Import" };
        var lv = new ListView { Source = new ListWrapper<string>(items) };
        lv.Width = Dim.Fill();
        lv.Height = Dim.Fill();
        lv.ValueChanged += (_, e) => Show(e.NewValue ?? 0);
        rail.Add(lv);

        // ── Right content host ───────────────────────────────────────────────
        _contentHost = new FrameView { Title = "Content" };
        _contentHost.X = Pos.Right(rail);
        _contentHost.Y = Pos.Absolute(0);
        _contentHost.Width = Dim.Fill();
        _contentHost.Height = Dim.Fill(Dim.Absolute(1));   // leave 1 row for status bar

        // Add all children
        Add(rail, _contentHost, statusBar);

        // Show initial view
        Show(0);
    }

    /// <summary>
    /// Swaps the content host to display the view at <paramref name="railIndex"/>.
    /// Task 11 will replace the Import placeholder with a real view.
    /// </summary>
    public void Show(int railIndex)
    {
        _contentHost.RemoveAll();

        if (railIndex == 0)
        {
            // Top Slow view — bound to TopSlowPresenter
            var view = new TopSlowView(
                new TopSlowPresenter(_ctx.Project),
                _ctx.Config.DurationUnit,
                _ctx.App);
            view.DrillRequested += OpenDrillDown;
            view.Reload();
            _contentHost.Title = "Top Slow";
            _contentHost.Add(view);
            view.SetFocus();
            return;
        }

        _contentHost.Title = "Content";
        _contentHost.Add(new Label { Text = $"(view {railIndex})" });
    }

    // Stub — Task 10 replaces with the real drill-down view.
    private void OpenDrillDown(QueryStat s) { }
}
