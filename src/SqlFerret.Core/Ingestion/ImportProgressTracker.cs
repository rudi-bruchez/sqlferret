namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Merges read-phase ticks (OnRead/OnFileComplete) and ingest-phase IngestionProgress into
/// one ImportProgress stream. Read ticks are throttled to integer-percent changes; the file
/// fraction is clamped to ≤0.99 during read and snaps to 1.0 only on completion. The overall
/// fraction is byte-weighted: completed file bytes + the current file's fraction of its bytes.
/// All callbacks run on the single ingest thread, so no locking is required.
/// </summary>
public sealed class ImportProgressTracker : IProgress<IngestionProgress>
{
    private readonly IReadOnlyList<(string name, long bytes)> _files;
    private readonly long _bytesTotal;
    private readonly EventCountEstimator _estimator;
    private readonly IProgress<ImportProgress>? _sink;
    private readonly Dictionary<string, (int index, long bytes)> _lookup;

    private long _completedBytes;
    private int _completedCount;

    // Display state — what the next emitted ImportProgress reports.
    private int _dispIndex;
    private string _dispName = "";
    private double _dispFileFrac;
    private double _dispOverallFrac;

    // Detail counters from the ingest phase.
    private long _read, _mapped, _unmapped, _cleaned, _failures;

    // Throttle: last emitted integer percents + file index.
    private int _lastFilePct = -1, _lastOverallPct = -1, _lastIndex = -1;

    public ImportProgressTracker(
        IReadOnlyList<(string name, long bytes)> files, long bytesTotal,
        EventCountEstimator estimator, IProgress<ImportProgress>? sink)
    {
        _files = files;
        _bytesTotal = bytesTotal;
        _estimator = estimator;
        _sink = sink;
        _lookup = new Dictionary<string, (int, long)>(files.Count);
        for (int i = 0; i < files.Count; i++)
            _lookup[files[i].name] = (i + 1, files[i].bytes);
    }

    /// <summary>Called from the read phase as each event is collected from a file.</summary>
    public void OnRead(string fileName, long eventsInFile)
    {
        if (_lookup.TryGetValue(fileName, out var info))
        {
            _dispIndex = info.index;
            _dispName = fileName;
            long est = _estimator.EstimateEvents(info.bytes);
            double frac = est <= 0 ? 0.0 : (double)eventsInFile / est;
            _dispFileFrac = Math.Min(0.99, frac);
            _dispOverallFrac = Overall(_completedBytes + _dispFileFrac * info.bytes);
        }

        int fp = Pct(_dispFileFrac), op = Pct(_dispOverallFrac);
        if (fp == _lastFilePct && op == _lastOverallPct && _dispIndex == _lastIndex) return;
        _lastFilePct = fp; _lastOverallPct = op; _lastIndex = _dispIndex;
        Emit();
    }

    /// <summary>Called once when a file is fully read (exact event count known).</summary>
    public void OnFileComplete(string fileName, long exactEvents)
    {
        if (_lookup.TryGetValue(fileName, out var info))
        {
            _estimator.Observe(exactEvents, info.bytes);
            _completedBytes += info.bytes;
            _completedCount++;
            _dispIndex = info.index;
            _dispName = fileName;
            _dispFileFrac = 1.0;
            _dispOverallFrac = Overall(_completedBytes);
        }
        Emit();
    }

    /// <summary>IProgress&lt;IngestionProgress&gt; — detail counters from the ingest phase.</summary>
    public void Report(IngestionProgress value)
    {
        _read = value.Read; _mapped = value.Mapped; _unmapped = value.Unmapped;
        _cleaned = value.Cleaned; _failures = value.TokenizeFailures;
        Emit();
    }

    private double Overall(double weightedBytes) =>
        _bytesTotal <= 0
            ? (_completedCount >= _files.Count ? 1.0 : 0.0)
            : Math.Clamp(weightedBytes / _bytesTotal, 0.0, 1.0);

    private static int Pct(double f) => Math.Clamp((int)(f * 100), 0, 100);

    private void Emit() =>
        _sink?.Report(new ImportProgress(
            _dispIndex, _files.Count, _dispName, _dispFileFrac, _dispOverallFrac,
            _read, _mapped, _unmapped, _cleaned, _failures));
}
