// tests/SqlFerret.Tui.Tests/TopSlowPresenterTests.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Tui.Presenters;

public class TopSlowPresenterTests : IDisposable
{
    private readonly SqlFerret.Core.Storage.DuckDbProject _project;

    public TopSlowPresenterTests()
    {
        _project = TestProject.SeedFrom(new[]
        {
            // sql_batch_completed: sql in batch_text
            ("sql_batch_completed", "SELECT * FROM dbo.Orders WHERE Id = 1", 5_000L, false),
            ("sql_batch_completed", "SELECT * FROM dbo.Orders WHERE Id = 2", 3_000L, false),
            ("sql_batch_completed", "SELECT * FROM dbo.Products WHERE Id = 9", 1_000L, false),
            // rpc_completed: sql in statement + object_name set
            ("rpc_completed", "EXEC dbo.GetOrder @id = 1", 9_000L, true),
        });
    }

    public void Dispose() => _project.Dispose();

    [Fact]
    public void Load_returns_rows_ordered_by_total()
    {
        var presenter = new TopSlowPresenter(_project);
        var rows = presenter.Load();

        // dbo.Orders appears twice → total 8000; dbo.GetOrder once → 9000; dbo.Products once → 1000
        Assert.True(rows.Count >= 2);
        // First row should have the highest total duration
        Assert.True(rows[0].TotalDurationUs >= rows[1].TotalDurationUs);
    }

    [Fact]
    public void TextFilter_narrows_results()
    {
        var presenter = new TopSlowPresenter(_project);
        presenter.SetTextFilter("Products");
        var rows = presenter.Load();

        Assert.All(rows, r => Assert.Contains("Products", r.NormalizedSql, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CycleSort_cycles_through_all_columns()
    {
        var presenter = new TopSlowPresenter(_project);
        Assert.Equal("total_duration_us", presenter.SortColumn);

        presenter.CycleSort();
        Assert.Equal("p95_duration_us", presenter.SortColumn);

        presenter.CycleSort();
        Assert.Equal("max_duration_us", presenter.SortColumn);

        presenter.CycleSort();
        Assert.Equal("avg_duration_us", presenter.SortColumn);

        presenter.CycleSort();
        Assert.Equal("total_duration_us", presenter.SortColumn);
    }
}
