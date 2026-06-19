// tests/SqlFerret.Tui.Tests/TopSlowPresenterTests.cs
using SqlFerret.Tui.Presenters;

public class TopSlowPresenterTests : IDisposable
{
    private readonly SeededProject _db;

    public TopSlowPresenterTests()
    {
        _db = TestProject.SeedFrom(new[]
        {
            // sql_batch_completed: sql in batch_text
            ("sql_batch_completed", "SELECT * FROM dbo.Orders WHERE Id = 1",   (string?)null, 5_000L),
            ("sql_batch_completed", "SELECT * FROM dbo.Orders WHERE Id = 2",   (string?)null, 3_000L),
            ("sql_batch_completed", "SELECT * FROM dbo.Products WHERE Id = 9", (string?)null, 1_000L),
            // rpc_completed: sql in statement + explicit object_name
            ("rpc_completed", "EXEC dbo.GetOrder @id = 1", "dbo.GetOrder", 9_000L),
        });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Load_returns_rows_ordered_by_total()
    {
        var presenter = new TopSlowPresenter(_db.Project);
        var rows = presenter.Load();

        // dbo.Orders appears twice → total 8000; dbo.GetOrder once → 9000; dbo.Products once → 1000
        // Expect exactly 3 distinct normalized signatures, descending by total duration
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].TotalDurationUs >= rows[1].TotalDurationUs &&
                    rows[1].TotalDurationUs >= rows[2].TotalDurationUs);
    }

    [Fact]
    public void TextFilter_narrows_results()
    {
        var presenter = new TopSlowPresenter(_db.Project);
        presenter.SetTextFilter("Products");
        var rows = presenter.Load();

        Assert.All(rows, r => Assert.Contains("Products", r.NormalizedSql, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CycleSort_cycles_through_all_columns()
    {
        var presenter = new TopSlowPresenter(_db.Project);
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
