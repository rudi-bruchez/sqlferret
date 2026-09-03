// src/SqlFerret.Core/Analysis/HealthDigest.cs
using System.Globalization;
using System.Text;
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>Les sept sections de la spec §9, dans son ordre, la couverture en tete.</summary>
public record HealthDigestResult(
    HealthCoverage Coverage, IReadOnlyList<string> Notes,
    IReadOnlyList<StateCount> NonCleanStates, IReadOnlyList<WaitDelta> TopWaits,
    IReadOnlyList<MemoryMovement> MemoryMovers, IReadOnlyList<HealthScalar> WorkerPressure,
    IReadOnlyList<(string Name, long Delta)> StabilitySignals,
    IReadOnlyList<PendingIoRow> WorstPendingIo,
    IReadOnlyList<DiagnosticsBlockingRow> DiagnosticsBlocking);

public record HealthDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, HealthDigestResult Digest);

/// <summary>
/// Le bloc de couverture vient en premier, avant toute conclusion : une capture system_health est
/// un anneau, et un classement lu sans lui passe pour un classement sur toute la periode.
/// <para>Les compteurs de cette source sont cumulatifs depuis le demarrage de l'instance. Chaque
/// chiffre presente comme une activite est donc un delta sur une portee, et la portee est imprimee
/// a cote.</para>
/// </summary>
public class HealthDigest(DuckDBConnection conn)
{
    public const int SchemaVersion = 1;

    private static readonly string[] WorkerGauges =
        ["pendingTasks", "workersIdle", "workersCreated", "maxWorkers",
         "oldestPendingTaskWaitingTimeUs"];

    private static readonly string[] StabilityCounters =
        ["spinlockBackoffs", "latchWarnings", "nonYieldingTasksReported", "pageFaults",
         "totalDumpRequests", "intervalDumpRequests", "writeAccessViolationCount",
         "outOfMemoryExceptions", "totalLongIos", "ioLatchTimeouts"];

    public HealthDigestEnvelope Build(int limit = 10)
    {
        var q = new HealthQueries(conn);
        var coverage = q.Coverage();
        var notes = new List<string>();

        if (coverage.Cycles == 0)
        {
            notes.Add("This project holds no diagnostics samples. Import a system_health capture, or "
                    + "check that the capture actually contains sp_server_diagnostics_component_result.");
            return new HealthDigestEnvelope(SchemaVersion, DateTime.UtcNow,
                new HealthDigestResult(coverage, notes, [], [], [], [], [], [], []));
        }

        if (coverage.Series.Count > 1)
            notes.Add($"{coverage.Series.Count} sampling series detected. Interval-scoped metrics "
                    + "(intervalLongIos, tasksCompletedWithinInterval) are not combined across them: "
                    + "each is scoped to its own session's interval. Rankings below use the longest "
                    + "series only.");

        // Une seule serie : un delta calcule a travers deux series entrelacees comparerait des
        // instants sans rapport.
        var main = coverage.Series.OrderByDescending(s => s.Cycles).First().SeriesKey;

        var nonClean = q.NonCleanStates();
        var waits = q.WaitDeltas(main, limit);
        var memory = q.MemoryMovers(main, limit);
        var workers = q.ScalarGauges(WorkerGauges);
        var stability = q.ScalarDeltas(main, StabilityCounters).Where(x => x.Delta > 0).ToList();
        var pendingIo = q.WorstPendingIo(limit);
        var diagBlocking = q.DiagnosticsBlocking(limit);

        if (waits.Count > 0)
            notes.Add("Wait counters are cumulative since instance start. The figures below are "
                    + "deltas over the span printed beside each row, not totals for the capture. "
                    + "Wait *times* are instance-lifetime values and nothing is ranked on them.");

        if (waits.Any(w => w.RestartIntervalsDropped > 0))
            notes.Add("A wait counter went backwards, which means the instance restarted inside the "
                    + "window. Those intervals are dropped from the delta and counted, not netted "
                    + "off — a restart must not read as a quiet period.");

        if (nonClean.Count == 0 && stability.Count == 0)
            notes.Add("No component left CLEAN and no stability signal moved. On a healthy server "
                    + "this is the expected result, not missing data.");

        if (pendingIo.Count > 0)
            notes.Add("Pending-I/O rows carry the server's own file paths. They are stored verbatim "
                    + "under every redaction mode and no flag removes them: this digest discloses "
                    + "instance and database file layout.");

        if (diagBlocking.Count > 0)
            notes.Add("Blocking below is what the server happened to be doing at a sampling instant, "
                    + "not a threshold-triggered report. It is not comparable with export-blocking.");

        return new HealthDigestEnvelope(SchemaVersion, DateTime.UtcNow,
            new HealthDigestResult(coverage, notes, nonClean, waits, memory, workers, stability,
                pendingIo, diagBlocking));
    }

    public static string ToMarkdown(HealthDigestEnvelope e)
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
            sb.AppendLine("| Wait type | delta waits | over (min) | per min | avg since instance start (ms) |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var w in d.TopWaits)
                sb.AppendLine($"| `{w.WaitType}` | {w.WaitsDelta} | {w.SpanMinutes.ToString("F1", inv)} "
                            + $"| {w.PerMinute.ToString("F1", inv)} "
                            + $"| {(w.LifetimeAvgWaitUs / 1000.0).ToString("F1", inv)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Memory").AppendLine();
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

        sb.AppendLine("## Worst pending I/O").AppendLine();
        if (d.WorstPendingIo.Count == 0) sb.AppendLine("None recorded.");
        else foreach (var i in d.WorstPendingIo)
            sb.AppendLine($"- {((i.DurationUs ?? 0) / 1000.0).ToString("F0", inv)} ms on "
                        + $"`{i.FilePath}` at {i.CapturedAt:u}");
        sb.AppendLine();

        sb.AppendLine("## Blocking seen in diagnostics cycles").AppendLine();
        if (d.DiagnosticsBlocking.Count == 0) sb.AppendLine("None.");
        else foreach (var b in d.DiagnosticsBlocking)
            sb.AppendLine($"- spid {b.BlockedSpid} blocked by {b.BlockingSpid} for "
                        + $"{((b.WaitTimeUs ?? 0) / 1000.0).ToString("F0", inv)} ms "
                        + $"({b.WaitResourceType}) at {b.CapturedAt:u}");

        return sb.ToString();
    }
}
