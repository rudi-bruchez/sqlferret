using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Replay;

namespace SqlFerret.Tui.Presenters;

public sealed class DrillDownPresenter(WorkloadQueries q, QueryStat signature)
{
    public QueryStat Signature => signature;

    public IReadOnlyList<Occurrence> Occurrences(int limit = 200) =>
        q.Occurrences(signature.NormalizedHash, limit);

    public IReadOnlyList<ParamImpact> ParameterImpact(string paramName) =>
        q.ParameterImpact(signature.NormalizedHash, paramName);

    public ReplayScript BuildReplay(long executionId) =>
        ReplayBuilder.Build(q.LoadExecution(executionId));
}
