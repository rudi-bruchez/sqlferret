// src/SqlFerret.Tui/Views/TopSlowView.cs
// TableView bound to TopSlowPresenter.
// TG 2.4.6 notes:
//   - TableView.Table setter takes ITableSource (use DataTableSource)
//   - Selection via _table.Value?.SelectedCell.Y (TableSelection.SelectedCell is Point)
//   - Modal dialog: app.Run(dlg, ex => false); close with dlg.RequestStop()
//   - KeyDown is EventHandler<Key>; set key.Handled = true to swallow
using System.Data;
using System.Drawing;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Shell;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SqlFerret.Tui.Views;

public sealed class TopSlowView : View
{
    private readonly TopSlowPresenter _presenter;
    private readonly string _unit;
    private readonly IApplication _app;
    private readonly TableView _table;
    private IReadOnlyList<QueryStat> _rows = [];

    public event Action<QueryStat>? DrillRequested;

    /// <summary>Number of data rows currently displayed (for tests and status bar).</summary>
    public int RowCount => _rows.Count;

    public TopSlowView(TopSlowPresenter presenter, string durationUnit, IApplication app)
    {
        _presenter = presenter;
        _unit = durationUnit;
        _app = app;

        Width = Dim.Fill();
        Height = Dim.Fill();

        _table = new TableView
        {
            X = Pos.Absolute(0),
            Y = Pos.Absolute(0),
            Width = Dim.Fill(),
            Height = Dim.Fill(Dim.Absolute(1)),
            FullRowSelect = true,
        };

        var hint = new Label
        {
            X = Pos.Absolute(0),
            Y = Pos.AnchorEnd(1),
            Text = "Enter=drill  s=sort  /=filter",
        };

        Add(_table, hint);

        _table.KeyDown += OnTableKeyDown;
    }

    public void Reload()
    {
        _rows = _presenter.Load();

        var dt = new DataTable();
        dt.Columns.Add("kind");
        dt.Columns.Add("signature");
        dt.Columns.Add("count");
        dt.Columns.Add("avg");
        dt.Columns.Add("p95");
        dt.Columns.Add("max");
        dt.Columns.Add("total");

        foreach (var s in _rows)
        {
            dt.Rows.Add(
                s.StatementKind,
                Truncate(s.NormalizedSql),
                s.Count,
                DisplayFormat.Duration((long)s.AvgDurationUs, _unit),
                DisplayFormat.Duration(s.P95DurationUs, _unit),
                DisplayFormat.Duration(s.MaxDurationUs, _unit),
                DisplayFormat.Duration(s.TotalDurationUs, _unit));
        }

        _table.Table = new DataTableSource(dt);
    }

    private void OnTableKeyDown(object? sender, Key key)
    {
        if (key == Keys.Sort)
        {
            _presenter.CycleSort();
            Reload();
            key.Handled = true;
        }
        else if (key == Keys.Filter)
        {
            PromptFilter();
            key.Handled = true;
        }
        else if (key == Key.Enter)
        {
            int row = SelectedRow();
            if (row >= 0 && row < _rows.Count)
            {
                DrillRequested?.Invoke(_rows[row]);
                key.Handled = true;
            }
        }
    }

    private int SelectedRow()
    {
        Point? cell = _table.Value?.SelectedCell;
        return cell.HasValue ? cell.Value.Y : -1;
    }

    private void PromptFilter()
    {
        var input = new TextField
        {
            X = Pos.Absolute(1),
            Y = Pos.Absolute(1),
            Width = Dim.Absolute(38),
        };

        var ok = new Button
        {
            Text = "OK",
            IsDefault = true,
            X = Pos.Absolute(1),
            Y = Pos.Absolute(3),
        };

        var dlg = new Dialog
        {
            Title = "Filter",
            Width = Dim.Absolute(44),
            Height = Dim.Absolute(7),
        };

        ok.Accepting += (_, _) =>
        {
            _presenter.SetTextFilter(input.Text?.ToString() ?? "");
            dlg.RequestStop();
        };

        dlg.Add(new Label { X = Pos.Absolute(1), Y = Pos.Absolute(0), Text = "Contains:" }, input, ok);

        _app.Run(dlg, _ => false);
        dlg.Dispose();

        Reload();
    }

    private static string Truncate(string s) =>
        s.Length <= 60 ? s : s[..57] + "...";
}
