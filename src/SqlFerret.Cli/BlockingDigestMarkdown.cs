// src/SqlFerret.Cli/BlockingDigestMarkdown.cs
using System.Text;
using SqlFerret.Core.Analysis;

namespace SqlFerret.Cli;

public static class BlockingDigestMarkdown
{
    public static string Render(BlockingDigestResult d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Blocking digest").AppendLine();
        sb.AppendLine($"- reports: {d.Overview.ReportCount}  deadlocks: {d.Overview.DeadlockCount}");
        sb.AppendLine($"- window: {d.Overview.FirstAt:u} → {d.Overview.LastAt:u}");
        sb.AppendLine($"- wait (us): p50={d.WaitTimes.P50Us} p95={d.WaitTimes.P95Us} max={d.WaitTimes.MaxUs}").AppendLine();
        sb.AppendLine("## Locality (blocked wait-resource type)");
        foreach (var l in d.Locality) sb.AppendLine($"- {l.WaitResourceType}: {l.Count} ({l.Pct:0.0}%)");
        sb.AppendLine().AppendLine("## Top objects");
        foreach (var o in d.TopObjects) sb.AppendLine($"- {o.Key}: {o.Count}");
        sb.AppendLine().AppendLine("## Top blockers");
        foreach (var b in d.TopBlockers) sb.AppendLine($"- [{b.Count}] `{Trim(b.NormalizedSql)}` ({b.Fingerprint})");
        sb.AppendLine().AppendLine("## Top blocked");
        foreach (var b in d.TopBlocked) sb.AppendLine($"- [{b.Count}] `{Trim(b.NormalizedSql)}`");
        sb.AppendLine().AppendLine("## Lock modes");
        foreach (var lm in d.LockModes) sb.AppendLine($"- {lm.Key}: {lm.Count}");
        sb.AppendLine().AppendLine("## Isolation levels");
        foreach (var il in d.IsolationLevels) sb.AppendLine($"- {il.Key}: {il.Count}");
        sb.AppendLine().AppendLine("## Chains");
        foreach (var ch in d.Chains) sb.AppendLine($"- loop {ch.MonitorLoop}: depth={ch.Depth} head_spid={ch.HeadSpid} edges={ch.EdgeCount}");
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length <= 100 ? s : s[..97] + "...";
}
