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
