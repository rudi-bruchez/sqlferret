// src/SqlFerret.Tui/Views/ImportView.cs
// Import form: .xel path + redaction picker + Start button + live progress label.
//
// TG 2.4.6 notes:
//   - RadioGroup does not exist; use OptionSelector<TEnum> (Terminal.Gui.Views)
//   - Button.Accepting is the click event (EventHandler<CommandEventArgs>)
//   - app.Invoke (instance) for UI-thread marshaling; wrapped in try/catch so
//     a missing message loop during headless tests doesn't break the import
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
        var start = new Button
        {
            X = Pos.Absolute(12),
            Y = Pos.Absolute(7),
            Text = "Start",
        };
        start.Accepting += (_, _) => { _ = StartAsync(_path.Text?.ToString() ?? ""); };

        // ── Progress label ───────────────────────────────────────────────────
        _progress = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(9),
            Width = Dim.Fill(),
            Text = "",
        };

        Add(pathLabel, _path, redactionLabel, _redaction, start, _progress);
    }

    /// <summary>
    /// Runs the import asynchronously. Safe to call from tests without a running message loop.
    /// </summary>
    public async Task StartAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _progress.Text = "Path is required.";
            return;
        }

        var redaction = _redaction.Value ?? RedactionMode.Masked;

        var progress = new Progress<IngestionProgress>(p =>
        {
            try
            {
                _app.Invoke(() =>
                    _progress.Text =
                        $"read={p.Read} mapped={p.Mapped} unmapped={p.Unmapped} " +
                        $"cleaned={p.Cleaned} failures={p.TokenizeFailures}  [{p.CurrentFile}]");
            }
            catch
            {
                // No message loop running (e.g. headless tests) — update directly
                _progress.Text =
                    $"read={p.Read} mapped={p.Mapped} unmapped={p.Unmapped} " +
                    $"cleaned={p.Cleaned} failures={p.TokenizeFailures}  [{p.CurrentFile}]";
            }
        });

        try
        {
            var result = await _presenter.RunAsync(path, redaction, progress, CancellationToken.None);

            _progress.Text = $"Done. read={result.Read} mapped={result.Mapped}";
            Completed?.Invoke(result);
        }
        catch (Exception ex)
        {
            _progress.Text = $"Import failed: {ex.Message}";
        }
    }
}
