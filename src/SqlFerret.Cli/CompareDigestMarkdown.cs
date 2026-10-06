// src/SqlFerret.Cli/CompareDigestMarkdown.cs
using System.Globalization;
using System.Text;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;

namespace SqlFerret.Cli;

/// <summary>Rendu Markdown de compare, dans l'hote : Core ne rend que des microsecondes.</summary>
public static class CompareDigestMarkdown
{
    private static string Safe(string? v) => MarkdownText.Safe(v);
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string N(double? v) => v is { } x ? N(x) : "-";

    public static string Render(CompareDigestEnvelope e, string durationUnit)
    {
        string Dur(double us) => DisplayFormat.Duration((long)us, durationUnit);
        string DurN(double? us) => us is { } x ? Dur(x) : "-";
        var d = e.Digest;
        var sb = new StringBuilder();
        sb.AppendLine("# Project comparison").AppendLine();

        sb.AppendLine("## Coverage").AppendLine();
        var (b, t) = (d.Coverage.Base, d.Coverage.Target);
        sb.AppendLine("| | Base | Target |").AppendLine("|---|---|---|");
        sb.AppendLine($"| Project | {Safe(b.ProjectDir)} | {Safe(t.ProjectDir)} |");
        sb.AppendLine($"| Active span | {Dur(b.ActiveSpanUs)} | {Dur(t.ActiveSpanUs)} |");
        sb.AppendLine($"| Largest gap inside a run | {DurN(b.LargestGapUs)}{(b.LargestGapRunId is { } br ? $" (run {br})" : "")} | {DurN(t.LargestGapUs)}{(t.LargestGapRunId is { } tr ? $" (run {tr})" : "")} |");
        sb.AppendLine($"| Executions | {b.Executions} | {t.Executions} |");
        sb.AppendLine($"| Executions without a duration, left out | {b.ExecutionsWithoutDuration} | {t.ExecutionsWithoutDuration} |");
        sb.AppendLine($"| Distinct statements | {b.DistinctStatements} | {t.DistinctStatements} |");
        sb.AppendLine($"| Databases | {Safe(string.Join(", ", b.Databases))}{(b.OtherDatabases > 0 ? $" (+{b.OtherDatabases})" : "")} | {Safe(string.Join(", ", t.Databases))}{(t.OtherDatabases > 0 ? $" (+{t.OtherDatabases})" : "")} |");
        sb.AppendLine($"| Normalizer versions | {string.Join(", ", b.NormalizerVersions)} | {string.Join(", ", t.NormalizerVersions)} |");
        sb.AppendLine($"| Redaction | {Safe(string.Join(", ", b.RedactionPolicies))} | {Safe(string.Join(", ", t.RedactionPolicies))} |");
        sb.AppendLine($"| SQL text | {Safe(string.Join(", ", b.SqlTextPolicies))} | {Safe(string.Join(", ", t.SqlTextPolicies))} |");
        sb.AppendLine($"| Smallest duration | {DurN(b.MinDurationUs)} | {DurN(t.MinDurationUs)} |");
        sb.AppendLine($"| Executions with query_hash | {N(b.QueryHashShare * 100)} % | {N(t.QueryHashShare * 100)} % |");
        sb.AppendLine($"| Plan profiles used / left out (before --database) | {b.EligiblePlanProfiles} / {b.ExcludedPlanProfiles} | {t.EligiblePlanProfiles} / {t.ExcludedPlanProfiles} |");
        sb.AppendLine();
        foreach (var (label, side) in (ReadOnlySpan<(string, CompareSideCoverage)>)[("Base", b), ("Target", t)])
        {
            sb.AppendLine($"{label} runs:").AppendLine();
            sb.AppendLine("| Run | First | Last | Span | Executions |").AppendLine("|---|---|---|---|---|");
            foreach (var r in side.Runs)
                sb.AppendLine($"| {r.RunId} | {r.First.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} | {r.Last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} | {Dur(r.SpanUs)} | {r.Executions} |");
            sb.AppendLine();
        }
        foreach (var n in d.Coverage.Notes) sb.AppendLine($"- {Safe(n)}");
        sb.AppendLine();

        sb.AppendLine("## Cost per execution").AppendLine();
        CostTable(sb, "Regressions", d.Regressions, "No regression above the thresholds.", Dur, DurN);
        CostTable(sb, "Gains", d.Gains, "No gain above the thresholds.", Dur, DurN);

        sb.AppendLine("## Load per hour").AppendLine();
        if (!d.LoadComputed) sb.AppendLine("Per-hour figures not computed: an active span is under the threshold.").AppendLine();
        else
        {
            LoadTable(sb, "Increases", d.LoadIncreases, "No load increase.", Dur);
            LoadTable(sb, "Decreases", d.LoadDecreases, "No load decrease.", Dur);
        }

        sb.AppendLine("## Appeared and disappeared").AppendLine();
        OneSide(sb, "Appeared (target only)", d.Appeared, Dur);
        OneSide(sb, "Disappeared (base only)", d.Disappeared, Dur);

        sb.AppendLine("## Plans").AppendLine();
        if (d.Plans.Skipped) sb.AppendLine($"Skipped: {Safe(d.Plans.SkipReason)}.").AppendLine();
        else if (d.Plans.Rows.Count == 0) sb.AppendLine("No plan or finding change among single-statement plans.").AppendLine();
        else
        {
            sb.AppendLine($"{d.Plans.Rows.Count} of {d.Plans.Total} shown. Single-statement plans only.").AppendLine();
            sb.AppendLine("| Query hash | Plan changed | Findings appeared | Findings gone | Median base | Median target | Linked statement hash |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in d.Plans.Rows)
                sb.AppendLine($"| {Safe(r.QueryHash)} | {(r.PlanChanged ? "yes" : "no")} | {Safe(string.Join(", ", r.AppearedKinds))} | {Safe(string.Join(", ", r.DisappearedKinds))} | {DurN(r.BaseMedianUs)} | {DurN(r.TargetMedianUs)} | {Safe(r.LinkedNormalizedHash ?? "-")} |");
            sb.AppendLine();
        }
        if (d.Plans.UnlinkedExcluded > 0)
            sb.AppendLine($"{d.Plans.UnlinkedExcluded} plan profiles left out: not linked to an execution of the filtered database.").AppendLine();
        return sb.ToString();
    }

    private static void CostTable(StringBuilder sb, string title, IReadOnlyList<CostRow> rows, string empty,
        Func<double, string> dur, Func<double?, string> durN)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (rows.Count == 0) { sb.AppendLine(empty).AppendLine(); return; }
        sb.AppendLine("| Ratio | Avg base | Avg target | p95 base | p95 target | CPU base | CPU target | Reads base | Reads target | Count base/target | Statement |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {(r.Ratio is { } x ? N(x) : "new cost")} | {dur(r.BaseAvgUs)} | {dur(r.TargetAvgUs)} | {dur(r.BaseP95Us)} | {dur(r.TargetP95Us)} | {durN(r.BaseAvgCpuUs)} | {durN(r.TargetAvgCpuUs)} | {N(r.BaseAvgReads)} | {N(r.TargetAvgReads)} | {r.BaseCount}/{r.TargetCount} | `{Safe(r.NormalizedSql)}` |");
        sb.AppendLine();
    }

    private static void LoadTable(StringBuilder sb, string title, IReadOnlyList<LoadRow> rows, string empty, Func<double, string> dur)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (rows.Count == 0) { sb.AppendLine(empty).AppendLine(); return; }
        sb.AppendLine("| Delta per hour | Base per hour | Target per hour | Executions/h base | Executions/h target | Statement |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {dur(r.DeltaUsPerHour)} | {dur(r.BaseUsPerHour)} | {dur(r.TargetUsPerHour)} | {N(r.BaseExecPerHour)} | {N(r.TargetExecPerHour)} | `{Safe(r.NormalizedSql)}` |");
        sb.AppendLine();
    }

    private static void OneSide(StringBuilder sb, string title, OneSideList list, Func<double, string> dur)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (list.Total == 0) { sb.AppendLine("None.").AppendLine(); return; }
        sb.AppendLine($"{list.Rows.Count} of {list.Total} shown.").AppendLine();
        sb.AppendLine("| Executions | Total duration | Per hour | Statement |").AppendLine("|---|---|---|---|");
        foreach (var r in list.Rows)
            sb.AppendLine($"| {r.Executions} | {dur(r.TotalDurationUs)} | {(r.UsPerHour is { } h ? dur(h) : "-")} | `{Safe(r.NormalizedSql)}` |");
        sb.AppendLine();
    }
}
