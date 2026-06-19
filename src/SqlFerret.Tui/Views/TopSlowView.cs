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
    private readonly UiState _ui;
    private readonly string? _uiPath;
    private static readonly string[] Catalog = ["kind", "signature", "count", "avg", "p95", "max", "total"];
    private string[] _visible;

    public event Action<QueryStat>? DrillRequested;

    /// <summary>Number of data rows currently displayed (for tests and status bar).</summary>
    public int RowCount => _rows.Count;

    public TopSlowView(TopSlowPresenter presenter, string durationUnit, IApplication app, UiState? ui = null, string? uiPath = null)
    {
        _presenter = presenter;
        _unit = durationUnit;
        _app = app;
        _ui = ui ?? new UiState();
        _uiPath = uiPath;
        _visible = _ui.Views.TryGetValue("topSlow", out var vl) && vl.Columns.Length > 0
            ? vl.Columns
            : Catalog;

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
            Text = "Enter=drill  s=sort  /=filter  C=columns",
        };

        Add(_table, hint);

        _table.KeyDown += OnTableKeyDown;
    }

    public void Reload()
    {
        _rows = _presenter.Load();

        var selectors = new Dictionary<string, Func<QueryStat, object>>
        {
            ["kind"]      = s => s.StatementKind,
            ["signature"] = s => Truncate(s.NormalizedSql),
            ["count"]     = s => s.Count,
            ["avg"]       = s => DisplayFormat.Duration((long)s.AvgDurationUs, _unit),
            ["p95"]       = s => DisplayFormat.Duration(s.P95DurationUs, _unit),
            ["max"]       = s => DisplayFormat.Duration(s.MaxDurationUs, _unit),
            ["total"]     = s => DisplayFormat.Duration(s.TotalDurationUs, _unit),
        };

        var dt = new DataTable();
        foreach (var col in _visible)
            dt.Columns.Add(col);

        foreach (var s in _rows)
        {
            var row = dt.NewRow();
            foreach (var col in _visible)
                row[col] = selectors[col](s);
            dt.Rows.Add(row);
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
        else if (key == Keys.Cols)
        {
            ChooseColumns();
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

    private void ChooseColumns()
    {
        var chosen = ColumnChooser.Show(_app, Catalog, _visible);
        if (chosen is null) return;
        _visible = chosen.ToArray();
        _ui.Views["topSlow"] = new UiState.ViewLayout(_visible, _presenter.SortColumn);
        if (_uiPath is not null) _ui.Save(_uiPath);
        Reload();
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
