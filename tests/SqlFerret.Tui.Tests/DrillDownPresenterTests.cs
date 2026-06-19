using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Replay;
using SqlFerret.Tui.Presenters;

public class DrillDownPresenterTests
{
    [Fact]
    public void Occurrences_and_replay_for_rpc()
    {
        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 123", "dbo.GetOrder", 4000),
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 999", "dbo.GetOrder", 6000),
        ]);
        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var p = new DrillDownPresenter(q, sig);

        var occ = p.Occurrences();
        Assert.Equal(2, occ.Count);

        var replay = p.BuildReplay(occ[0].ExecutionId);
        Assert.Equal(ReplayKind.ExecProc, replay.Kind);
        Assert.StartsWith("EXEC dbo.GetOrder @OrderId = ", replay.Sql);
    }

    [Fact]
    public void ParameterImpact_groups_by_value_slowest_first()
    {
        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 1", "dbo.GetOrder", 1000),
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 2", "dbo.GetOrder", 9000),
        ]);
        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var p = new DrillDownPresenter(q, sig);
        var impact = p.ParameterImpact("@OrderId");
        Assert.Equal(2, impact.Count);
        Assert.True(impact[0].AvgDurationUs >= impact[1].AvgDurationUs); // slowest value-set first
    }
}
