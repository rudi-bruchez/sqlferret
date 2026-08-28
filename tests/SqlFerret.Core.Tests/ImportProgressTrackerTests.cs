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

    [Fact]
    public void File_complete_updates_throttle_state_for_subsequent_reads()
    {
        var (t, ticks) = Make(("a.xel", 100_000));   // est = 100 events
        t.OnRead("a.xel", 50);                       // 50%, 50% overall
        t.OnFileComplete("a.xel", 100);              // 100%, 100% overall
        Assert.Equal(1.0, ticks[^1].FileFraction, 3);
        Assert.Equal(1.0, ticks[^1].OverallFraction, 3);

        int afterComplete = ticks.Count;
        // A different read percent must emit a new tick (proving throttle state is live).
        t.OnRead("a.xel", 50);                       // Again 50% on same file
        Assert.True(ticks.Count > afterComplete, "Expected new tick after OnFileComplete throttle update");
        Assert.Equal(0.5, ticks[^1].FileFraction, 3);
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
