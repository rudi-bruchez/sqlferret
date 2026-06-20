// src/SqlFerret.Tui/Views/ImportView.cs
// Import form: .xel path + redaction picker + Start button + live progress label.
//
// TG 2.4.6 notes:
//   - RadioGroup does not exist; use OptionSelector<TEnum> (Terminal.Gui.Views)
//   - Button.Accepting is the click event (EventHandler<CommandEventArgs>)
//   - app.Invoke (instance) for UI-thread marshaling; wrapped in try/catch so
//     a missing message loop during headless tests doesn't break the import
//
// Concurrency contract:
//   - _running is only ever read/written on the UI thread (StartAsync is awaited
//     on the UI thread; the guard check + set happen before the Task.Run).
//   - ImportStarted is raised BEFORE the await; ImportFinished is raised AFTER
//     the await (success or failure) and BEFORE Completed, so that MainWindow
//     clears _importing before Completed triggers Show(0).
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Tui.Presenters;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SqlFerret.Tui.Views;

public sealed class ImportView : View
{
    private readonly ImportPresenter _presenter;
    private readonly IApplication _app;
    private readonly TextField _path;
    private readonly OptionSelector<RedactionMode> _redaction;
    private readonly Label _progress;
    private readonly Button _start;

    private bool _running;

    /// <summary>True while an import is in progress.</summary>
    public bool IsRunning => _running;

    /// <summary>Raised on the UI thread just before the import awaits (i.e. when the worker starts).</summary>
    public event Action? ImportStarted;

    /// <summary>Raised on the UI thread after the import finishes (success or failure), before Completed.</summary>
    public event Action? ImportFinished;

    public event Action<IngestionResult>? Completed;

    public ImportView(ImportPresenter presenter, RedactionMode defaultRedaction, IApplication app)
    {
        _presenter = presenter;
        _app = app;

        Width = Dim.Fill();
        Height = Dim.Fill();

        // ── Path row ────────────────────────────────────────────────────────
        var pathLabel = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(0),
            Text = "Path:",
        };
        _path = new TextField
        {
            X = Pos.Absolute(12),
            Y = Pos.Absolute(0),
            Width = Dim.Fill(Dim.Absolute(2)),
        };

        // ── Redaction picker ─────────────────────────────────────────────────
        var redactionLabel = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(2),
            Text = "Redaction:",
        };
        _redaction = new OptionSelector<RedactionMode>
        {
            X = Pos.Absolute(12),
            Y = Pos.Absolute(2),
            Value = defaultRedaction,
        };

        // ── Start button ─────────────────────────────────────────────────────
        _start = new Button
        {
            X = Pos.Absolute(12),
            Y = Pos.Absolute(7),
            Text = "Start",
        };
        _start.Accepting += (_, _) => { _ = StartAsync(_path.Text?.ToString() ?? ""); };

        // ── Progress label ───────────────────────────────────────────────────
        _progress = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(9),
            Width = Dim.Fill(),
            Text = "",
        };

        Add(pathLabel, _path, redactionLabel, _redaction, _start, _progress);
    }

    /// <summary>
    /// Runs the import asynchronously. Safe to call from tests without a running message loop.
    /// Re-entrant calls (while _running) are silently ignored.
    /// </summary>
    public async Task StartAsync(string path)
    {
        // Re-entrancy guard — checked and set on the UI thread before any await.
        if (_running) return;

        if (string.IsNullOrWhiteSpace(path))
        {
            _progress.Text = "Path is required.";
            return;
        }

        _running = true;
        _start.Enabled = false;

        // Notify MainWindow so it can block rail navigation while we run.
        ImportStarted?.Invoke();

        var redaction = _redaction.Value ?? RedactionMode.Masked;

        var progress = new Progress<ImportProgress>(p =>
        {
            try
            {
                _app.Invoke(() => _progress.Text = ImportProgressText.Render(p));
            }
            catch
            {
                // Message loop unavailable (teardown / headless) — drop this tick.
            }
        });

        IngestionResult? result = null;
        Exception? error = null;
        try
        {
            result = await _presenter.RunAsync(path, redaction, progress, CancellationToken.None);
            _progress.Text = $"Done. read={result.Read} mapped={result.Mapped}";
        }
        catch (Exception ex)
        {
            error = ex;
            _progress.Text = $"Import failed: {ex.Message}";
        }
        finally
        {
            // Always clear running state and re-enable button …
            _running = false;
            _start.Enabled = true;

            // … then unblock navigation (MainWindow clears _importing) …
            ImportFinished?.Invoke();

            // … then raise Completed so Show(0) in MainWindow is NOT blocked.
            if (error is null && result is not null)
                Completed?.Invoke(result);
        }
    }
}
