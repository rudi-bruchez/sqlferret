# Import Progress Gauge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a live double percentage gauge — per-file and overall — while SQLFerret ingests `.xel` files, in both the CLI `import` command and the TUI `ImportView`.

**Architecture:** Drive progress from *inside* the XELite read callback (the silent, dominant cost today), estimating each file's event count from its byte size with a self-calibrating bytes-per-event ratio. A host-agnostic `ImportProgressTracker` merges read-phase ticks and the existing ingest-phase `IngestionProgress` into one `ImportProgress`; an `ImportRunner` orchestrator wires resolve → reader (with callbacks) → service so both hosts share identical logic.

**Tech Stack:** .NET 10 / C# 14, xUnit + Xunit.SkippableFact, Microsoft.SqlServer.XEvent.XELite (the `.xel` reader). No new dependencies.

## Global Constraints

- **net10.0 / C# 14**, Nullable + ImplicitUsings on, LangVersion latest. Verbatim from spec.
- **KISS (spec §2):** no DI, no `IXxxService` interface (unless a real second impl exists), plain records / static utility classes / primary-constructor services. The only Core abstraction is `IXeEventData`.
- **Microseconds invariant:** durations stay `*_us` in Core. This feature adds no unit conversions — percentages and counts only.
- **Modern C# 14 baseline:** collection expressions `[]` (never `new[]{}` / `Array.Empty`), `record` for multi-field values, primary constructors for stateful services, `required`/`init` on DTOs. A bare `catch` only on deliberate fallback paths.
- **Build is 0 warnings.** Run `dotnet format` before every commit. `.editorconfig` is the style baseline.
- **Git:** branch is already `feat/import-progress-gauge` (do not work on `main`). Wrap git with `rtk`. Co-author every commit:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- All new Core types live in namespace `SqlFerret.Core.Ingestion`.

---

### Task 1: `EventCountEstimator` (self-calibrating bytes-per-event)

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/EventCountEstimator.cs`
- Test: `tests/SqlFerret.Core.Tests/EventCountEstimatorTests.cs`

**Interfaces:**
- Produces:
  - `EventCountEstimator(long seedBytesPerEvent = 1500)`
  - `long EstimateEvents(long fileBytes)` — predicted event count for a file of `fileBytes`.
  - `void Observe(long exactEvents, long fileBytes)` — refine the ratio from a fully-read file.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/EventCountEstimatorTests.cs
using SqlFerret.Core.Ingestion;

public class EventCountEstimatorTests
{
    [Fact]
    public void Seed_estimate_uses_seed_ratio()
    {
        var est = new EventCountEstimator(seedBytesPerEvent: 1000);
        Assert.Equal(15, est.EstimateEvents(15_000));
    }

    [Fact]
    public void Zero_or_negative_bytes_estimates_zero()
    {
        var est = new EventCountEstimator(seedBytesPerEvent: 1000);
        Assert.Equal(0, est.EstimateEvents(0));
        Assert.Equal(0, est.EstimateEvents(-5));
    }

    [Fact]
    public void Observe_recalibrates_ratio_for_subsequent_files()
    {
        var est = new EventCountEstimator(seedBytesPerEvent: 1000);
        est.Observe(exactEvents: 10, fileBytes: 30_000);   // real ratio = 3000 B/event
        Assert.Equal(10, est.EstimateEvents(30_000));
        Assert.Equal(20, est.EstimateEvents(60_000));
    }

    [Fact]
    public void Observe_ignores_empty_files_and_never_divides_by_zero()
    {
        var est = new EventCountEstimator(seedBytesPerEvent: 1000);
        est.Observe(exactEvents: 0, fileBytes: 0);          // no-op, must not throw
        Assert.Equal(15, est.EstimateEvents(15_000));       // ratio unchanged
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter EventCountEstimatorTests`
Expected: FAIL — `EventCountEstimator` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Ingestion/EventCountEstimator.cs
namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Estimates how many events a .xel file holds from its byte size, refining the
/// bytes-per-event ratio as real files complete. The first file uses the seed; every
/// file after a completed one is near-exact (captures are homogeneous per session).
/// </summary>
public sealed class EventCountEstimator(long seedBytesPerEvent = 1500)
{
    private long _bytesPerEvent = Math.Max(1, seedBytesPerEvent);
    private long _totalEvents;
    private long _totalBytes;

    public long EstimateEvents(long fileBytes) =>
        fileBytes <= 0 ? 0 : Math.Max(1, fileBytes / _bytesPerEvent);

    public void Observe(long exactEvents, long fileBytes)
    {
        if (exactEvents <= 0 || fileBytes <= 0) return;
        _totalEvents += exactEvents;
        _totalBytes += fileBytes;
        _bytesPerEvent = Math.Max(1, _totalBytes / _totalEvents);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter EventCountEstimatorTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Ingestion/EventCountEstimator.cs tests/SqlFerret.Core.Tests/EventCountEstimatorTests.cs
rtk git commit -m "feat(core): self-calibrating EventCountEstimator for import progress

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `ImportProgress` record + `ImportProgressTracker`

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/ImportProgress.cs`
- Create: `src/SqlFerret.Core/Ingestion/ImportProgressTracker.cs`
- Test: `tests/SqlFerret.Core.Tests/ImportProgressTrackerTests.cs`

**Interfaces:**
- Consumes: `EventCountEstimator` (Task 1); existing `IngestionProgress(long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures, string CurrentFile)`.
- Produces:
  - `record ImportProgress(int FileIndex, int FileCount, string CurrentFile, double FileFraction, double OverallFraction, long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures)`
  - `ImportProgressTracker(IReadOnlyList<(string name, long bytes)> files, long bytesTotal, EventCountEstimator estimator, IProgress<ImportProgress>? sink)` — implements `IProgress<IngestionProgress>`.
    - `void OnRead(string fileName, long eventsInFile)`
    - `void OnFileComplete(string fileName, long exactEvents)`
    - `void Report(IngestionProgress value)` (the `IProgress<IngestionProgress>` member)

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ImportProgressTrackerTests.cs
using SqlFerret.Core.Ingestion;

public class ImportProgressTrackerTests
{
    private static (ImportProgressTracker t, List<ImportProgress> ticks) Make(
        params (string name, long bytes)[] files)
    {
        var ticks = new List<ImportProgress>();
        var total = files.Sum(f => f.bytes);
        var t = new ImportProgressTracker(files, total,
            new EventCountEstimator(seedBytesPerEvent: 1000),
            new ListProgress(ticks.Add));
        return (t, ticks);
    }

    [Fact]
    public void Per_file_fraction_advances_with_events_and_is_clamped_below_one()
    {
        var (t, ticks) = Make(("a.xel", 100_000));   // est = 100 events
        t.OnRead("a.xel", 50);                       // 50/100 = 0.5
        Assert.Equal(0.5, ticks[^1].FileFraction, 3);

        t.OnRead("a.xel", 500);                      // 5.0 -> clamped to 0.99
        Assert.Equal(0.99, ticks[^1].FileFraction, 3);
        Assert.True(ticks[^1].FileFraction < 1.0);
    }

    [Fact]
    public void File_complete_snaps_fraction_to_one()
    {
        var (t, ticks) = Make(("a.xel", 100_000));
        t.OnRead("a.xel", 50);
        t.OnFileComplete("a.xel", 100);
        Assert.Equal(1.0, ticks[^1].FileFraction, 3);
    }

    [Fact]
    public void Overall_fraction_is_byte_weighted_across_files()
    {
        var (t, ticks) = Make(("a.xel", 100_000), ("b.xel", 300_000)); // total 400k
        t.OnRead("a.xel", 50);                       // a half-read: 50k/400k = 0.125
        Assert.Equal(0.125, ticks[^1].OverallFraction, 3);

        t.OnFileComplete("a.xel", 100);              // a done: 100k/400k = 0.25
        Assert.Equal(0.25, ticks[^1].OverallFraction, 3);

        t.OnRead("b.xel", 150);                      // b half-read: (100k+150k)/400k = 0.625
        Assert.Equal(0.625, ticks[^1].OverallFraction, 3);
        Assert.Equal(2, ticks[^1].FileIndex);
        Assert.Equal("b.xel", ticks[^1].CurrentFile);

        t.OnFileComplete("b.xel", 300);              // all done
        Assert.Equal(1.0, ticks[^1].OverallFraction, 3);
    }

    [Fact]
    public void Repeated_read_at_same_percent_does_not_emit()
    {
        var (t, ticks) = Make(("a.xel", 100_000));   // est = 100
        t.OnRead("a.xel", 1);                        // 1% -> emits
        int after = ticks.Count;
        t.OnRead("a.xel", 1);                        // same 1% -> no emit
        Assert.Equal(after, ticks.Count);
    }

    [Fact]
    public void Ingest_report_supplies_detail_counters()
    {
        var (t, ticks) = Make(("a.xel", 100_000));
        t.OnRead("a.xel", 50);
        ((IProgress<IngestionProgress>)t).Report(
            new IngestionProgress(40, 38, 2, 0, 1, "a.xel"));
        Assert.Equal(40, ticks[^1].Read);
        Assert.Equal(38, ticks[^1].Mapped);
        Assert.Equal(1, ticks[^1].TokenizeFailures);
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ImportProgressTrackerTests`
Expected: FAIL — `ImportProgress` / `ImportProgressTracker` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Ingestion/ImportProgress.cs
namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Unified gauge model for an import: per-file fraction, byte-weighted overall fraction,
/// and the running ingest detail counters. FileIndex is 1-based; 0 before the first file.
/// </summary>
public record ImportProgress(
    int FileIndex, int FileCount, string CurrentFile,
    double FileFraction, double OverallFraction,
    long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures);
```

```csharp
// src/SqlFerret.Core/Ingestion/ImportProgressTracker.cs
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ImportProgressTrackerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Ingestion/ImportProgress.cs src/SqlFerret.Core/Ingestion/ImportProgressTracker.cs tests/SqlFerret.Core.Tests/ImportProgressTrackerTests.cs
rtk git commit -m "feat(core): ImportProgress + byte-weighted ImportProgressTracker

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `ImportProgressText` — host-agnostic line formatter

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/ImportProgressText.cs`
- Test: `tests/SqlFerret.Core.Tests/ImportProgressTextTests.cs`

**Interfaces:**
- Consumes: `ImportProgress` (Task 2).
- Produces:
  - `static string ImportProgressText.Render(ImportProgress p)` — the single canonical one-line, plain-ASCII gauge string used by both hosts.
  - `static string ImportProgressText.Abbrev(long n)` — `"0"`, `"999"`, `"1k"`, `"812k"`, `"1.2M"`.

Rationale for living in Core (not a host): both CLI and TUI render the identical line — keeping one formatter is DRY and lets it be unit-tested without a terminal or message loop. It formats counts/percentages only (no unit conversion), so the microseconds-in-hosts rule is untouched.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ImportProgressTextTests.cs
using SqlFerret.Core.Ingestion;

public class ImportProgressTextTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(812_345, "812k")]
    [InlineData(999_999, "999k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_250_000, "1.2M")]
    public void Abbrev_formats_counts(long n, string expected) =>
        Assert.Equal(expected, ImportProgressText.Abbrev(n));

    [Fact]
    public void Render_produces_canonical_ascii_line()
    {
        var p = new ImportProgress(
            FileIndex: 2, FileCount: 5, CurrentFile: "perf_3.xel",
            FileFraction: 0.4748, OverallFraction: 0.612,
            Read: 812_345, Mapped: 790_000, Unmapped: 21_000, Cleaned: 0, TokenizeFailures: 0);

        Assert.Equal(
            "[2/5] perf_3.xel  file 47%  overall 61%  " +
            "read=812k mapped=790k unmapped=21k cleaned=0 failures=0",
            ImportProgressText.Render(p));
    }

    [Fact]
    public void Render_without_files_omits_index_header()
    {
        var p = new ImportProgress(0, 0, "", 0, 0, 0, 0, 0, 0, 0);
        Assert.StartsWith("  file 0%  overall 0%", ImportProgressText.Render(p));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ImportProgressTextTests`
Expected: FAIL — `ImportProgressText` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Ingestion/ImportProgressText.cs
using System.Globalization;

namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Renders an ImportProgress as one plain-ASCII line shared by the CLI and TUI hosts.
/// Counts are abbreviated (k / M); percentages are floored to integers.
/// </summary>
public static class ImportProgressText
{
    public static string Render(ImportProgress p)
    {
        string head = p.FileCount > 0 ? $"[{p.FileIndex}/{p.FileCount}] {p.CurrentFile}" : p.CurrentFile;
        return $"{head}  file {Pct(p.FileFraction)}%  overall {Pct(p.OverallFraction)}%  " +
               $"read={Abbrev(p.Read)} mapped={Abbrev(p.Mapped)} unmapped={Abbrev(p.Unmapped)} " +
               $"cleaned={Abbrev(p.Cleaned)} failures={Abbrev(p.TokenizeFailures)}";
    }

    public static string Abbrev(long n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000) return (n / 1000).ToString(CultureInfo.InvariantCulture) + "k";
        return (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    private static int Pct(double f) => Math.Clamp((int)(f * 100), 0, 100);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ImportProgressTextTests`
Expected: PASS (9 cases).

- [ ] **Step 5: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Ingestion/ImportProgressText.cs tests/SqlFerret.Core.Tests/ImportProgressTextTests.cs
rtk git commit -m "feat(core): shared plain-ASCII ImportProgressText line formatter

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `XelReader` read-phase callbacks

**Files:**
- Modify: `src/SqlFerret.Core/Ingestion/XelReader.cs:14-41` (the `Read` method)
- Test: `tests/SqlFerret.Core.Tests/XelReaderTests.cs` (add one skippable test)

**Interfaces:**
- Produces (new overload-by-default-args, source-compatible with existing `Read(files)`):
  - `IEnumerable<(IXeEventData ev, string fileName, long offset)> Read(IReadOnlyList<string> files, Action<string, long>? onRead = null, Action<string, long>? onFileComplete = null)`
  - `onRead(fileName, eventsCollectedSoFar)` fires per event during XELite collection; `onFileComplete(fileName, exactEventCount)` fires once when a file is fully read, before its events are yielded.

- [ ] **Step 1: Write the failing test**

Add this test to `tests/SqlFerret.Core.Tests/XelReaderTests.cs` (reuse the existing `FindSampleDir()` helper in that file):

```csharp
    [SkippableFact]
    public void Read_invokes_progress_callbacks_per_event_and_on_file_complete()
    {
        var sampleDir = FindSampleDir();
        Skip.If(sampleDir is null, "sample/ folder not present (real .xel traces are gitignored / not on CI)");

        var chosen = Directory.GetFiles(sampleDir!, "*.xel")
            .Select(f => new FileInfo(f)).OrderBy(f => f.Length).First();

        long lastRead = 0; int readCalls = 0;
        long completeCount = -1; int completeCalls = 0;

        var events = new XelReader().Read(
            [chosen.FullName],
            onRead: (_, n) => { lastRead = n; readCalls++; },
            onFileComplete: (_, n) => { completeCount = n; completeCalls++; }).ToList();

        Assert.NotEmpty(events);
        Assert.True(readCalls > 0, "onRead must fire at least once");
        Assert.Equal(1, completeCalls);                  // exactly one file completed
        Assert.Equal(events.Count, completeCount);       // exact count matches yielded events
        Assert.Equal(events.Count, lastRead);            // last running count == total
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter XelReaderTests`
Expected: FAIL to compile — `Read` has no 3-arg overload. (If `sample/` is absent the new test would skip, but the compile error blocks the run first — that is the expected red.)

- [ ] **Step 3: Write minimal implementation**

Replace the `Read` method (lines 14-41) with:

```csharp
    public IEnumerable<(IXeEventData ev, string fileName, long offset)> Read(
        IReadOnlyList<string> files,
        Action<string, long>? onRead = null,
        Action<string, long>? onFileComplete = null)
    {
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            long ordinal = 0;
            var collected = new List<IXeEventData>();

            var streamer = new XEFileEventStreamer(file);

            // XELite is push/async; ReadEventStream takes:
            //   Func<Task>         headerReadCallback  (called once when header is parsed)
            //   Func<IXEvent,Task> eventCallback       (called per event)
            //   CancellationToken
            streamer.ReadEventStream(
                () => Task.CompletedTask,
                xevent =>
                {
                    collected.Add(new XeEventDataAdapter(xevent));
                    onRead?.Invoke(name, collected.Count);   // live read-phase progress
                    return Task.CompletedTask;
                },
                CancellationToken.None).GetAwaiter().GetResult();

            onFileComplete?.Invoke(name, collected.Count);   // exact count for calibration

            foreach (var ev in collected)
                yield return (ev, name, ordinal++);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter XelReaderTests`
Expected: PASS — `Empty_file_list_yields_no_events` passes; the two `[SkippableFact]` tests PASS if `sample/` is present, else SKIP. Either way, green.

- [ ] **Step 5: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Ingestion/XelReader.cs tests/SqlFerret.Core.Tests/XelReaderTests.cs
rtk git commit -m "feat(core): XelReader read-phase progress callbacks (onRead/onFileComplete)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: `ImportRunner` orchestrator

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/ImportRunner.cs`
- Test: `tests/SqlFerret.Core.Tests/ImportRunnerTests.cs`

**Interfaces:**
- Consumes: `XelSource.Resolve` → `(IReadOnlyList<string> files, long bytesTotal)`; `XelReader.Read(files, onRead, onFileComplete)` (Task 4); `ImportProgressTracker` (Task 2); `IngestionService(project, options)` with `Ingest(sourcePath, events, IProgress<IngestionProgress>?)`.
- Produces:
  - `static IngestionResult ImportRunner.Run(DuckDbProject project, IngestionOptions options, string path, IProgress<ImportProgress>? progress = null)`

This is the single seam both hosts call — keeping the resolve→read→ingest wiring DRY. `IngestionService` stays decoupled from `XelReader`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ImportRunnerTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public class ImportRunnerTests
{
    private static string? FindSampleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.xel").Length > 0)
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public void Run_imports_sample_and_emits_monotonic_overall_ending_at_full()
    {
        var sampleDir = FindSampleDir();
        Skip.If(sampleDir is null, "sample/ folder not present");

        var chosen = Directory.GetFiles(sampleDir!, "*.xel")
            .Select(f => new FileInfo(f)).OrderBy(f => f.Length).First();

        var dbPath = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(dbPath);
            var ticks = new List<ImportProgress>();
            var sink = new ListProgress(ticks.Add);

            var result = ImportRunner.Run(db,
                new IngestionOptions(RedactionMode.Masked, []), chosen.FullName, sink);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);

            // Overall fraction is monotonic non-decreasing …
            var overall = ticks.Select(t => t.OverallFraction).ToList();
            Assert.True(overall.SequenceEqual(overall.OrderBy(x => x)), "overall must be monotonic");

            // … and reaches 1.0 by the end (single-file import).
            Assert.Equal(1.0, ticks[^1].OverallFraction, 3);
            Assert.Equal(1, ticks[^1].FileCount);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ImportRunnerTests`
Expected: FAIL — `ImportRunner` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Ingestion/ImportRunner.cs
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Ingestion;

/// <summary>
/// One-call import orchestration shared by every host: resolve the path, wire a progress
/// tracker across the read and ingest phases, and run the ingestion. Throws
/// FileNotFoundException (from XelSource.Resolve) for a missing path — hosts handle it.
/// </summary>
public static class ImportRunner
{
    public static IngestionResult Run(
        DuckDbProject project, IngestionOptions options, string path,
        IProgress<ImportProgress>? progress = null)
    {
        var (files, bytesTotal) = XelSource.Resolve(path);
        var sizes = files
            .Select(f => (name: Path.GetFileName(f), bytes: new FileInfo(f).Length))
            .ToList();

        var tracker = new ImportProgressTracker(sizes, bytesTotal, new EventCountEstimator(), progress);
        var events = new XelReader().Read(files, tracker.OnRead, tracker.OnFileComplete);
        var svc = new IngestionService(project, options);
        return svc.Ingest(path, events, tracker);   // tracker is the IProgress<IngestionProgress>
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SqlFerret.Core.Tests --filter ImportRunnerTests`
Expected: PASS if `sample/` present, else SKIP (green either way).

- [ ] **Step 5: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Ingestion/ImportRunner.cs tests/SqlFerret.Core.Tests/ImportRunnerTests.cs
rtk git commit -m "feat(core): ImportRunner orchestrator wiring read+ingest progress

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: CLI — live in-place gauge line

**Files:**
- Modify: `src/SqlFerret.Cli/Program.cs` (the `case "import":` block, lines ~42-52, and add a `file`-scoped helper class at the end)

**Interfaces:**
- Consumes: `ImportRunner.Run` (Task 5), `ImportProgressText.Render` (Task 3).

No new unit test: the formatting is covered by Task 3; `CliSmokeTests` calls `IngestionService` directly and is unaffected. Verification is the manual run in Step 3.

- [ ] **Step 1: Replace the import body**

In `src/SqlFerret.Cli/Program.cs`, replace the block that currently reads:

```csharp
            IReadOnlyList<string> files;
            try { (files, _) = XelSource.Resolve(path); }
            catch (FileNotFoundException) { Console.Error.WriteLine($"import: path not found: {path}"); return 1; }
            using var db = DuckDbProject.Open(project);
            var svc = new IngestionService(db, new IngestionOptions(redaction, Array.Empty<FilterRule>()));
            var result = svc.Ingest(path, new XelReader().Read(files));
            Console.WriteLine(
                $"run {result.RunId}: read={result.Read} mapped={result.Mapped} " +
                $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures}");
            return 0;
```

with:

```csharp
            using var db = DuckDbProject.Open(project);
            var options = new IngestionOptions(redaction, Array.Empty<FilterRule>());

            // Live in-place gauge on stderr (kept off stdout so the summary stays clean and
            // pipe-friendly). Synchronous IProgress so carriage-return updates stay ordered.
            var showGauge = !Console.IsErrorRedirected;
            var progress = new SyncProgress<ImportProgress>(p =>
            {
                if (showGauge)
                    Console.Error.Write("\r" + ImportProgressText.Render(p).PadRight(100));
            });

            IngestionResult result;
            try { result = ImportRunner.Run(db, options, path, progress); }
            catch (FileNotFoundException) { Console.Error.WriteLine($"import: path not found: {path}"); return 1; }

            if (showGauge) Console.Error.WriteLine();   // terminate the in-place line

            Console.WriteLine(
                $"run {result.RunId}: read={result.Read} mapped={result.Mapped} " +
                $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures}");
            return 0;
```

Then add this helper at the very end of `Program.cs` (after all top-level statements):

```csharp
file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: 0 warnings, 0 errors. (`XelReader`/`XelSource` are no longer referenced directly in the import case — that is fine; other cases and `using`s remain valid.)

- [ ] **Step 3: Manual verification against a sample (if `sample/` present)**

Run:
```bash
dotnet run --project src/SqlFerret.Cli -- import sample/$(ls sample | grep -m1 '\.xel$') --project /tmp/wl_gauge.duckdb
```
Expected: a single line updates in place on stderr, e.g.
`[1/1] performances_....xel  file 73%  overall 73%  read=120k mapped=118k unmapped=2k cleaned=0 failures=0`
then a newline and the final `run N: read=… mapped=…` summary on stdout. (Skip this step if `sample/` is absent.)

- [ ] **Step 4: Run the full suite (no regressions)**

Run: `dotnet test`
Expected: all pass (1 skipped = the env-gated live-SQL test; sample-gated tests pass or skip).

- [ ] **Step 5: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Cli/Program.cs
rtk git commit -m "feat(cli): live in-place double progress gauge during import

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: TUI — percentage gauge in `ImportView`

**Files:**
- Modify: `src/SqlFerret.Tui/Presenters/ImportPresenter.cs:9-19`
- Modify: `src/SqlFerret.Tui/Views/ImportView.cs:127-140`
- Modify: `tests/SqlFerret.Tui.Tests/ImportPresenterTests.cs`

**Interfaces:**
- Consumes: `ImportRunner.Run` (Task 5), `ImportProgress` (Task 2), `ImportProgressText.Render` (Task 3).
- Produces: `ImportPresenter.RunAsync(string path, RedactionMode redaction, IProgress<ImportProgress> progress, CancellationToken ct)` — progress type changes from `IngestionProgress` to `ImportProgress`.

- [ ] **Step 1: Update the presenter test to the new progress type (red)**

Replace the body of `tests/SqlFerret.Tui.Tests/ImportPresenterTests.cs` with:

```csharp
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using SqlFerret.Tui.Presenters;

public class ImportPresenterTests
{
    [SkippableFact]
    public async Task RunAsync_imports_real_sample_and_reports_progress()
    {
        var file = SampleFile.FindSmallest();
        Skip.If(file is null, "sample/ folder with a .xel trace not present");

        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var presenter = new ImportPresenter(db);
            var ticks = new List<ImportProgress>();
            IProgress<ImportProgress> sync = new ListProgress(ticks.Add);

            var result = await presenter.RunAsync(file!, RedactionMode.Masked, sync, CancellationToken.None);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);
            Assert.Equal(1.0, ticks[^1].OverallFraction, 3);   // reaches 100% at the end
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlFerret.Tui.Tests --filter ImportPresenterTests`
Expected: FAIL to compile — `RunAsync` still takes `IProgress<IngestionProgress>`.

- [ ] **Step 3: Update the presenter**

Replace the body of `src/SqlFerret.Tui/Presenters/ImportPresenter.cs` with:

```csharp
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Tui.Presenters;

public sealed class ImportPresenter(DuckDbProject project)
{
    public Task<IngestionResult> RunAsync(
        string path, RedactionMode redaction, IProgress<ImportProgress> progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var options = new IngestionOptions(redaction, []);
            return ImportRunner.Run(project, options, path, progress);   // throws FileNotFoundException for a bad path
        }, ct);
    }
}
```

- [ ] **Step 4: Update the view's progress wiring**

In `src/SqlFerret.Tui/Views/ImportView.cs`, replace the progress block (lines 127-140):

```csharp
        var progress = new Progress<IngestionProgress>(p =>
        {
            try
            {
                _app.Invoke(() =>
                    _progress.Text =
                        $"read={p.Read} mapped={p.Mapped} unmapped={p.Unmapped} " +
                        $"cleaned={p.Cleaned} failures={p.TokenizeFailures}  [{p.CurrentFile}]");
            }
            catch
            {
                // Message loop unavailable (teardown / headless) — drop this tick.
            }
        });
```

with:

```csharp
        var progress = new Progress<ImportProgress>(p =>
        {
            try
            {
                _app.Invoke(() => _progress.Text = ImportProgressText.Render(p));
            }
            catch
            {
                // Message loop unavailable (teardown / headless) — drop this tick.
            }
        });
```

(`using SqlFerret.Core.Ingestion;` is already present at the top of the file, so `ImportProgress` / `ImportProgressText` resolve. The `_progress.Text = $"Done. read=…"` line and the rest of `StartAsync` are unchanged.)

- [ ] **Step 5: Run the TUI tests to verify they pass**

Run: `dotnet test tests/SqlFerret.Tui.Tests --filter "ImportPresenterTests|ImportViewTests"`
Expected: PASS — `ImportPresenterTests` passes or skips (sample-gated); the three `ImportViewTests` pass (they never referenced the progress type: the empty-path and re-entrant tests use bad paths and exercise the guard / `FileNotFoundException` path unchanged).

- [ ] **Step 6: Full suite + build (no regressions)**

Run: `dotnet build && dotnet test`
Expected: 0 warnings; all tests pass (1 skipped = env-gated live-SQL; sample-gated tests pass or skip).

- [ ] **Step 7: Commit**

```bash
dotnet format
rtk git add src/SqlFerret.Tui/Presenters/ImportPresenter.cs src/SqlFerret.Tui/Views/ImportView.cs tests/SqlFerret.Tui.Tests/ImportPresenterTests.cs
rtk git commit -m "feat(tui): per-file + overall percentage gauge in ImportView

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes for the implementer

- **Seed value (`1500` bytes/event):** a deliberate, non-critical ballpark — self-calibration corrects it after the first file completes. If you want a tighter seed, measure once from a sample (`bytes ÷ event-count` of a `performances_*.xel`) and update the default in `EventCountEstimator`; the unit tests inject their own seed so they stay stable regardless.
- **Why progress only moves during read, then counters during ingest:** `XelReader` buffers a whole file before yielding, so for each file the percentage ramps during the (dominant) read, then the detail counters (`read=/mapped=/…`) catch up during the fast ingest tail. This is expected, not a bug.
- **`ingestion_runs.files_count` / `bytes_total` accuracy** for folder imports remains a Plan-2 deferral (CLAUDE.md) — out of scope here; `IngestionService.BeginRun` is untouched.

## Self-review

- **Spec coverage:** both hosts (Tasks 6, 7) ✓; drive from XELite read callback (Task 4) ✓; per-file % + byte-weighted overall % (Task 2) ✓; self-calibrating estimator with seed (Task 1) ✓; ≤0.99 clamp + snap-to-1.0 (Task 2) ✓; zero-byte / divide-by-zero guards (Tasks 1, 2) ✓; throttle on integer-percent change (Task 2) ✓; plain-ASCII line, counts abbreviated (Task 3) ✓; no `XelReader` streaming refactor / no two-pass (collect-then-yield kept, Task 4) ✓; additive overloads, existing signatures preserved (Tasks 4, 5; `IngestionService.Ingest(events)` untouched) ✓; testing matrix — estimator, tracker, formatter pure-unit + XelReader/ImportRunner skippable + host formatter via Task 3 ✓.
- **Type consistency:** `ImportProgress` field names/order identical across Tasks 2, 3, 5, 7; `ImportProgressTracker` ctor + `OnRead`/`OnFileComplete`/`Report` signatures match their use in Task 5; `XelReader.Read(files, onRead, onFileComplete)` matches Task 5's call; `ImportRunner.Run(project, options, path, progress)` matches Tasks 6 & 7; `ImportProgressText.Render`/`Abbrev` match Tasks 6 & 7. ✓
- **Placeholder scan:** no TBD/TODO; every code step shows complete code; every test step shows real assertions. ✓
