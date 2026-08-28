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

    public (ReplayScript Script, bool AnyRedacted) BuildReplay(long executionId)
    {
        var ev = q.LoadExecution(executionId);
        return (ReplayBuilder.Build(ev), ev.Parameters.Any(p => p.Redacted));
    }
}
