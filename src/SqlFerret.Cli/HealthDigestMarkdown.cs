// src/SqlFerret.Cli/HealthDigestMarkdown.cs
using System.Globalization;
using System.Text;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;

namespace SqlFerret.Cli;

/// <summary>
/// Rendu Markdown du digest de sante, dans l'HOTE et non dans Core, comme
/// <see cref="BlockingDigestMarkdown"/>. L'invariant est explicite : « Formatting to ms/s happens
/// only in hosts, via DisplayFormat. Never convert units inside Core. » Une premiere version
/// divisait par 1000 dans Core.
/// </summary>
public static class HealthDigestMarkdown
{
    public static string Render(HealthDigestEnvelope e)
    {
        var d = e.Digest;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Health digest").AppendLine();

        sb.AppendLine("## Coverage").AppendLine();
        if (d.Coverage.Cycles == 0)
        {
            sb.AppendLine("No diagnostics samples.").AppendLine();
        }
        else
        {
            sb.AppendLine($"- Cycles: {d.Coverage.Cycles}");
            sb.AppendLine($"- Window: {d.Coverage.First:u} to {d.Coverage.Last:u} "
                        + $"({d.Coverage.SpanMinutes.ToString("F1", inv)} min)");
            sb.AppendLine($"- Median interval: {d.Coverage.MedianIntervalMin.ToString("F2", inv)} min; "
                        + $"largest gap: {d.Coverage.LargestGapMin.ToString("F2", inv)} min");
            sb.AppendLine($"- Events in the capture that are not diagnostics: "
                        + $"{(d.Coverage.NonDiagnosticsShare * 100).ToString("F1", inv)} %");
            sb.AppendLine($"- Sampling series: {d.Coverage.Series.Count}");
            foreach (var s in d.Coverage.Series)
                sb.AppendLine($"  - `{s.SeriesKey}`: {s.Cycles} cycles, median "
                            + $"{s.MedianIntervalMin.ToString("F2", inv)} min");
            sb.AppendLine();
        }

        foreach (var n in d.Notes) sb.AppendLine($"> {n}").AppendLine();

        sb.AppendLine("## Non-clean states").AppendLine();
        if (d.NonCleanStates.Count == 0) sb.AppendLine("Every component reported CLEAN.");
        else foreach (var s in d.NonCleanStates)
            sb.AppendLine($"- `{s.Component}` = {s.State} in {s.Cycles} cycle(s)");
        sb.AppendLine();

        sb.AppendLine("## Waits").AppendLine();
        if (d.TopWaits.Count == 0)
        {
            sb.AppendLine("None recorded.");
        }
        else
        {
            sb.AppendLine("Ranked by count delta. Wait times are since instance start.").AppendLine();
            sb.AppendLine("| Wait type | delta waits | over (min) | per min | avg since instance start |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var w in d.TopWaits)
                sb.AppendLine($"| `{w.WaitType}` | {w.WaitsDelta} | {w.SpanMinutes.ToString("F1", inv)} "
                            + $"| {w.PerMinute.ToString("F1", inv)} "
                            + $"| {DisplayFormat.Duration(w.LifetimeAvgWaitUs, "ms")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Memory").AppendLine();
        foreach (var s in d.MemoryPressure) sb.AppendLine($"- `{s.Name}`: +{s.Delta}");
        if (d.MemoryPressure.Count == 0) sb.AppendLine("No memory pressure counter moved.");
        sb.AppendLine();
        if (d.MemoryMovers.Count == 0) sb.AppendLine("No memory report entries.");
        else foreach (var m in d.MemoryMovers)
            sb.AppendLine($"- `{m.Description}` ({m.ReportName}): {m.First.ToString("F0", inv)} to "
                        + $"{m.Last.ToString("F0", inv)} ({m.Change.ToString("+#;-#;0", inv)} {m.Unit})");
        sb.AppendLine();

        sb.AppendLine("## Worker pressure").AppendLine();
        if (d.WorkerPressure.Count == 0) sb.AppendLine("Not recorded.");
        else foreach (var s in d.WorkerPressure)
            sb.AppendLine($"- `{s.Name}`: min {s.Min}, median {s.Median}, p95 {s.P95}, max {s.Max} "
                        + $"({s.Samples} samples)");
        sb.AppendLine();

        sb.AppendLine("## Stability signals").AppendLine();
        if (d.StabilitySignals.Count == 0) sb.AppendLine("Nothing moved.");
        else foreach (var (name, delta) in d.StabilitySignals) sb.AppendLine($"- `{name}`: +{delta}");
        sb.AppendLine();

        sb.AppendLine("## I/O").AppendLine();
        if (d.IoCounters.Count == 0) sb.AppendLine("No I/O counter moved.");
        else foreach (var s in d.IoCounters) sb.AppendLine($"- `{s.Name}`: +{s.Delta}");
        sb.AppendLine();

        sb.AppendLine("## Worst pending I/O").AppendLine();
        if (d.WorstPendingIo.Count == 0) sb.AppendLine("None recorded.");
        else foreach (var i in d.WorstPendingIo)
            sb.AppendLine($"- {DisplayFormat.Duration(i.DurationUs ?? 0, "ms")} on "
                        + $"`{i.FilePath}` at {i.CapturedAt:u}");
        sb.AppendLine();

        sb.AppendLine("## Blocking seen in diagnostics cycles").AppendLine();
        if (d.DiagnosticsBlocking.Count == 0) sb.AppendLine("None.");
        else foreach (var b in d.DiagnosticsBlocking)
            sb.AppendLine($"- spid {b.BlockedSpid} blocked by {b.BlockingSpid} for "
                        + $"{DisplayFormat.Duration(b.WaitTimeUs ?? 0, "ms")} "
                        + $"({b.WaitResourceType}) at {b.CapturedAt:u}");

        return sb.ToString();
    }
}
