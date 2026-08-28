// src/SqlFerret.Tui/Views/ColumnChooserDialog.cs
// SSMS-style column chooser modal for TopSlowView.
// TG 2.4.6 notes:
//   - CheckBox.Value is CheckState (enum: Checked, UnChecked, None) — in Terminal.Gui.Views
//   - Pos is in Terminal.Gui.ViewBase
//   - Modal: app.Run(dlg, _ => false); close via dlg.RequestStop(); then dlg.Dispose()
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SqlFerret.Tui.Views;

public static class ColumnChooser
{
    /// <summary>
    /// Pure: filters catalog to selected, preserving catalog order.
    /// Empty selected -> returns full catalog (never zero columns).
    /// </summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> catalog, IReadOnlyList<string> selected)
    {
        if (selected.Count == 0) return catalog;
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return catalog.Where(set.Contains).ToList();
    }

    /// <summary>
    /// Modal column chooser. Returns new visible list or null on cancel.
    /// </summary>
    public static IReadOnlyList<string>? Show(IApplication app, IReadOnlyList<string> catalog, IReadOnlyList<string> current)
    {
        var checks = catalog
            .Select(c => new CheckBox
            {
                Text = c,
                Value = current.Contains(c) ? CheckState.Checked : CheckState.UnChecked,
            })
            .ToList();

        var dlg = new Dialog { Title = "Choose columns", Width = 30, Height = catalog.Count + 6 };

        for (int i = 0; i < checks.Count; i++)
        {
            checks[i].X = Pos.Absolute(1);
            checks[i].Y = Pos.Absolute(i);
            dlg.Add(checks[i]);
        }

        IReadOnlyList<string>? result = null;

        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepting += (_, _) =>
        {
            var sel = catalog.Where((c, i) => checks[i].Value == CheckState.Checked).ToList();
            result = Apply(catalog, sel);
            dlg.RequestStop();
        };

        var reset = new Button { Text = "Reset" };
        reset.Accepting += (_, _) =>
        {
            result = catalog;
            dlg.RequestStop();
        };

        dlg.Add(ok, reset);

        app.Run(dlg, _ => false);
        dlg.Dispose();

        return result;
    }
}
