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
