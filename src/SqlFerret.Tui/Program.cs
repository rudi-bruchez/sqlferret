// src/SqlFerret.Tui/Program.cs
// Terminal.Gui v2.4.6 instance-model entry-point.
// Usage: SqlFerret.Tui <project-dir>
using SqlFerret.Core.Config;
using SqlFerret.Core.Project;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Shell;
using Terminal.Gui.App;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: sqlferret <project-dir>");
    return 1;
}

var ap = AuditProject.OpenOrCreate(args[0], Directory.GetCurrentDirectory());
// Item 5: surface corrupt-manifest recovery.
if (ap.RecoveredFromCorruptManifest)
    Console.Error.WriteLine("warning: project.json was unreadable and has been reinitialized; provenance (CreatedUtc) was reset.");

var uiStatePath = Path.Combine(AppContext.BaseDirectory, "uistate.json");
var ui = UiState.Load(uiStatePath);

using var project = ap.OpenDb();

using IApplication app = Application.Create();
app.Init();

// Wire the native clipboard via TG 2.4.6 IApplication.Clipboard.TrySetClipboardData.
// If the TG clipboard is unavailable, fall through to the file fallback (null delegate).
Func<string, bool>? trySet = app.Clipboard is not null
    ? app.Clipboard.TrySetClipboardData
    : null;

var clipboard = new NativeClipboard(new FileFallbackClipboard(Path.GetTempPath()), trySet);

var ctx = new TuiContext(app, project, ap.Config, ui, clipboard, uiStatePath);
var win = new MainWindow(ctx);

app.Run(win);
win.Dispose();

ui.Save(uiStatePath);
return 0;
