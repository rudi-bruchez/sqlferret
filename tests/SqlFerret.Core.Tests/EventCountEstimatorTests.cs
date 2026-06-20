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
