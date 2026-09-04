// src/SqlFerret.Core/Analysis/HealthDigest.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>Les sept sections de la spec §9, dans son ordre, la couverture en tete.</summary>
public record HealthDigestResult(
    HealthCoverage Coverage, IReadOnlyList<string> Notes,
    IReadOnlyList<StateCount> NonCleanStates, IReadOnlyList<WaitDelta> TopWaits,
    IReadOnlyList<MemoryMovement> MemoryMovers, IReadOnlyList<HealthScalar> WorkerPressure,
    IReadOnlyList<ScalarDelta> StabilitySignals,
    IReadOnlyList<ScalarDelta> MemoryPressure,
    IReadOnlyList<HealthScalar> MemoryGauges,
    IReadOnlyList<ScalarDelta> IoCounters,
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
        ["pendingTasks", "workersIdle", "maxWorkers", "oldestPendingTaskWaitingTimeUs"];

    /// <summary>Spec §9 item 4 : « from SYSTEM ». Uniquement des scalaires du composant SYSTEM.</summary>
    private static readonly string[] StabilityCounters =
        ["spinlockBackoffs", "latchWarnings", "nonYieldingTasksReported", "pageFaults",
         "totalDumpRequests", "writeAccessViolationCount",
         "BadPagesDetected", "BadPagesFixed"];

    /// <summary>Rattaches a l'intervalle du cycle : sommes, jamais differencies.</summary>
    private static readonly string[] StabilityIntervalCounters = ["intervalDumpRequests"];

    /// <summary>Spec §9 item 3, la partie scalaire, du composant RESOURCE.</summary>
    private static readonly string[] MemoryCounters =
        ["outOfMemoryExceptions"];

    /// <summary>
    /// Ni des compteurs ni des durees a cumuler. <c>isAnyPoolOutOfMemory</c> est un drapeau 0/1 :
    /// un delta dessus n'a pas de sens, et bloque a 1 il rendait zero puis disparaissait du
    /// digest. <c>processOutOfMemoryPeriodUs</c> a une semantique que Microsoft Learn ne definit
    /// pas — une distribution reste juste qu'il soit cumulatif (le max est la valeur finale) ou
    /// instantane, ce qu'un delta ne serait pas.
    /// </summary>
    private static readonly string[] MemoryGaugeNames =
        ["processOutOfMemoryPeriodUs", "isAnyPoolOutOfMemory"];

    /// <summary>Spec §9 item 6, la partie scalaire, du composant IO_SUBSYSTEM.</summary>
    private static readonly string[] IoCounterNames =
        ["totalLongIos", "ioLatchTimeouts"];

    /// <summary>Rattaches a l'intervalle du cycle : sommes, jamais differencies.</summary>
    private static readonly string[] IoIntervalCounters = ["intervalLongIos"];

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
                new HealthDigestResult(coverage, notes, [], [], [], [], [], [], [], [], [], []));
        }

        if (coverage.CadenceIsIrregular)
            notes.Add("The interval between cycles is not homogeneous. The usual cause is that this "
                    + "capture folder holds more than one session recording the same server. "
                    + "Interval-scoped counters (intervalLongIos, intervalDumpRequests) are "
                    + "suppressed below: each is scoped to its own session's interval, summing "
                    + "them across interleaved sessions would double-count, and this tool cannot "
                    + "tell which cycle belongs to which session. Everything else below is "
                    + "unaffected: the "
                    + "counters are cumulative per instance and the gauges are point-in-time, so a "
                    + "last-minus-first across interleaved samples of one server stays correct.");

        var main = coverage.Series.Count > 0 ? coverage.Series[0].SeriesKey : "1";

        var nonClean = q.NonCleanStates();
        var waits = q.WaitDeltas(main, limit);
        var memory = q.MemoryMovers(main, limit);
        var workers = q.ScalarGauges(WorkerGauges);
        // Les compteurs d'intervalle sont rattaches a l'intervalle de LEUR session. Quand la
        // cadence est irreguliere, on ne sait pas a quelle session chaque cycle appartient : on
        // les tait plutot que de rendre un chiffre dont personne ne peut dire ce qu'il mesure.
        // Les sommer a travers deux sessions entrelacees compterait deux fois.
        IReadOnlyList<ScalarDelta> Interval(string[] names) => coverage.CadenceIsIrregular
            ? [] : q.IntervalSums(main, names).Where(x => x.Delta > 0).ToList();

        var stability = q.ScalarDeltas(main, StabilityCounters).Where(x => x.Delta > 0)
            .Concat(Interval(StabilityIntervalCounters)).ToList();
        var memoryPressure = q.ScalarDeltas(main, MemoryCounters).Where(x => x.Delta > 0).ToList();
        var memoryGauges = q.ScalarGauges(MemoryGaugeNames);
        var ioCounters = q.ScalarDeltas(main, IoCounterNames).Where(x => x.Delta > 0)
            .Concat(Interval(IoIntervalCounters)).ToList();
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

        // Cette note parle au nom de tout le digest : elle doit donc regarder tout le digest.
        // Elle ne consultait que trois des sept sections, et un serveur qui n'avait que des E/S
        // longues ou du blocage la recevait quand meme.
        bool anyPoolOom = memoryGauges.Any(g => g.Max is > 0);
        if (nonClean.Count == 0 && stability.Count == 0 && memoryPressure.Count == 0
            && ioCounters.Count == 0 && pendingIo.Count == 0 && diagBlocking.Count == 0
            && !anyPoolOom)
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
                memoryPressure, memoryGauges, ioCounters, pendingIo, diagBlocking));
    }
}
