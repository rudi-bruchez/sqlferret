// tests/SqlFerret.Tui.Tests/ColumnChooserTests.cs
using SqlFerret.Tui.Views;

public class ColumnChooserTests
{
    [Fact]
    public void Apply_keeps_only_selected_in_catalog_order()
    {
        string[] all = ["kind", "signature", "count", "avg", "p95", "max", "total"];
        var chosen = ColumnChooser.Apply(all, selected: ["signature", "total", "kind"]);
        Assert.Equal(["kind", "signature", "total"], chosen);
    }

    [Fact]
    public void Apply_empty_selection_falls_back_to_all()
    {
        string[] all = ["kind", "signature", "total"];
        Assert.Equal(all, ColumnChooser.Apply(all, selected: []));
    }
}
