using SqlFerret.Core.Model;

namespace SqlFerret.Core.Replay;

public static class ReplayBuilder
{
    public static ReplayScript Build(ExecutionEvent ev)
    {
        if (ev.EventClass == EventClass.RpcCall &&
            ev.SqlTextRaw.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase))
            return new ReplayScript(ev.SqlTextRaw, ReplayKind.SpExecuteSql, 0.7);

        if (ev.EventClass == EventClass.RpcCall && ev.ObjectName is { Length: > 0 } && ev.Parameters.Count > 0)
        {
            var args = string.Join(", ", ev.Parameters.Select(p => $"{p.Name} = {p.ValueText}"));
            var confidence = ev.Parameters.Min(p => p.ParseConfidence);
            return new ReplayScript($"EXEC {ev.ObjectName} {args};", ReplayKind.ExecProc, confidence);
        }

        return new ReplayScript(ev.SqlTextRaw, ReplayKind.RawBatch, 1.0);
    }
}
