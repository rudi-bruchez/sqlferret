using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Tui.Presenters;

public sealed class ImportPresenter(DuckDbProject project)
{
    public Task<IngestionResult> RunAsync(
        string path, RedactionMode redaction, IProgress<IngestionProgress> progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var (files, _) = XelSource.Resolve(path);           // throws FileNotFoundException for a bad path
            var events = new XelReader().Read(files);
            var svc = new IngestionService(project, new IngestionOptions(redaction, []));
            return svc.Ingest(path, events, progress);
        }, ct);
    }
}
