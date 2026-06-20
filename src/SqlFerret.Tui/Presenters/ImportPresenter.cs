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
