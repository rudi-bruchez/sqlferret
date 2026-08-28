using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Plans;
using SqlFerret.Core.Project;
using SqlFerret.Core.Storage;

namespace SqlFerret.Tui.Presenters;

public sealed class ImportPresenter(DuckDbProject db, AuditProject project)
{
    public Task<IngestionResult> RunAsync(
        string path, RedactionMode redaction, IProgress<ImportProgress> progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var options = new IngestionOptions(redaction, [],
                PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()));
            // throws FileNotFoundException for a bad path
            var result = ImportRunner.Run(db, options, path, progress);

            db.FinalizePlanRun(result.RunId);
            if (result.PlanProfiles > 0)
            {
                var digestRows = db.ReadPlanDigestRows(result.RunId);
                var writer = new PlanArtifactWriter(project.PlanProfileRunFolder(result.RunId));
                writer.WriteDigests(digestRows);
                writer.WriteIndex(digestRows);
            }

            return result;
        }, ct);
    }
}
