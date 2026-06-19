// src/SqlFerret.Tui/Views/DrillDownView.cs
// Shows a query signature's occurrences and supports build-for-SSMS (copy replay to clipboard).
//
// TG 2.4.6 notes:
//   - No FakeDriver; headless tests use Application.Create(VirtualTimeProvider)
//   - TableView selection: _occ.Value?.SelectedCell.Y  (Point? from TableSelection)
//   - TableView.Table setter takes ITableSource; use DataTableSource(DataTable)
//   - KeyDown is EventHandler<Key>; key.Handled = true swallows the event
//   - IClipboard disambiguation: alias SqlFerret.Tui.Clipboard.IClipboard to avoid
//     ambiguity with Terminal.Gui.App.IClipboard
using System.Data;
using System.Drawing;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Shell;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using IClipboard = SqlFerret.Tui.Clipboard.IClipboard;

namespace SqlFerret.Tui.Views;

public sealed class DrillDownView : View
{
    private readonly DrillDownPresenter _p;
    private readonly IClipboard _clip;
    private readonly string _unit;
    private readonly TableView _occ;
    private readonly Label _result;
    private IReadOnlyList<Occurrence> _rows = [];

    public event Action? BackRequested;
    public int OccurrenceCount => _rows.Count;

    public DrillDownView(DrillDownPresenter presenter, IClipboard clipboard, string durationUnit)
    {
        _p = presenter;
        _clip = clipboard;
        _unit = durationUnit;

        Width = Dim.Fill();
        Height = Dim.Fill();

        var header = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(0),
            Width = Dim.Fill(),
            Height = Dim.Absolute(3),
            Text = BuildHeaderText(),
        };

        _occ = new TableView
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(3),
            Width = Dim.Fill(),
            Height = Dim.Fill(Dim.Absolute(1)),
            FullRowSelect = true,
        };

        _result = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = "Esc=back  c=copy build-for-SSMS",
        };

        Add(header, _occ, _result);

        _occ.KeyDown += OnOccKeyDown;
    }

    public void Reload()
    {
        _rows = _p.Occurrences();

        var dt = new DataTable();
        dt.Columns.Add("time");
        dt.Columns.Add("db");
        dt.Columns.Add("login");
        dt.Columns.Add("duration");
        dt.Columns.Add("sql");

        foreach (var o in _rows)
        {
            dt.Rows.Add(
                o.CapturedAt.ToString("HH:mm:ss"),
                o.Database ?? "",
                o.Login ?? "",
                o.DurationUs is { } d ? DisplayFormat.Duration(d, _unit) : "",
                Trim(o.SqlTextRaw));
        }

        _occ.Table = new DataTableSource(dt);
    }

    /// <summary>
    /// Copies the replay script for the currently selected occurrence to the clipboard.
    /// Returns the <see cref="SqlFerret.Tui.Clipboard.ClipboardResult"/> so tests can inspect it.
    /// </summary>
    public SqlFerret.Tui.Clipboard.ClipboardResult CopySelectedReplay()
    {
        int i = SelectedRow();
        if (i < 0 || i >= _rows.Count)
            return new SqlFerret.Tui.Clipboard.ClipboardResult(false, null, "no row selected");

        var script = _p.BuildReplay(_rows[i].ExecutionId);
        var res = _clip.Copy(script.Sql, $"exec-{_rows[i].ExecutionId}");
        string note = script.Confidence < 1.0 ? $" (confidence {script.Confidence:0.0})" : "";
        return res with { Description = $"{script.Kind}: {res.Description}{note}" };
    }

    private void OnOccKeyDown(object? sender, Key key)
    {
        if (key == Keys.Copy)
        {
            var r = CopySelectedReplay();
            _result.Text = r.Description;
            key.Handled = true;
        }
        else if (key == Keys.Back)
        {
            BackRequested?.Invoke();
            key.Handled = true;
        }
    }

    private int SelectedRow()
    {
        Point? cell = _occ.Value?.SelectedCell;
        return cell.HasValue ? cell.Value.Y : 0;
    }

    private string BuildHeaderText()
    {
        var sig = _p.Signature;
        return $"{sig.StatementKind}  {sig.NormalizedSql}\n"
             + $"count={sig.Count}  avg={DisplayFormat.Duration((long)sig.AvgDurationUs, _unit)}"
             + $"  p95={DisplayFormat.Duration(sig.P95DurationUs, _unit)}";
    }

    private static string Trim(string s) => s.Length <= 50 ? s : s[..47] + "...";
}
