// src/SqlFerret.Tui/Presenters/TopSlowPresenter.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Storage;

namespace SqlFerret.Tui.Presenters;

public class TopSlowPresenter(DuckDbProject project)
{
    private static readonly string[] SortCycle =
        ["total_duration_us", "p95_duration_us", "max_duration_us", "avg_duration_us"];

    public int Limit { get; set; } = 50;
    public string SortColumn { get; private set; } = "total_duration_us";
    public string TextFilter { get; private set; } = "";
    public IReadOnlyList<FilterRule> Filters { get; set; } = [];

    public IReadOnlyList<QueryStat> Load()
    {
        return new WorkloadQueries(project.Connection)
            .TopSlow(Limit, SortColumn, Filters, string.IsNullOrEmpty(TextFilter) ? null : TextFilter);
    }

    public void CycleSort()
    {
        int idx = Array.IndexOf(SortCycle, SortColumn);
        SortColumn = SortCycle[(idx + 1) % SortCycle.Length];
    }

    public void SetTextFilter(string text) => TextFilter = text ?? "";
}
