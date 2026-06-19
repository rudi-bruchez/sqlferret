// src/SqlFerret.Tui/Program.cs
// Adaptation note: Terminal.Gui v2 moved classes to sub-namespaces (App, ViewBase, Views, Input)
// and prefers the instance-based IApplication model over the legacy static Application methods.
// Key comparison and key.Handled work as-is; Key.WithCtrl exists.  Window is Terminal.Gui.Views.Window.
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

// Usage: SqlFerret.Tui <project.duckdb>   (project path required; mirrors the CLI's --project)
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: SqlFerret.Tui <project.duckdb>");
    return 1;
}

using IApplication app = Application.Create();
app.Init();

var win = new Window { Title = "SQLFerret" };
win.KeyDown += (_, key) =>
{
    if (key == Key.Q || key == Key.Q.WithCtrl) { app.RequestStop(); key.Handled = true; }
};
app.Run(win);
win.Dispose();
return 0;
