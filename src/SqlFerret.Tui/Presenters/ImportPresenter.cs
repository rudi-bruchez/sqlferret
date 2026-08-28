using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
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
            // The configured level applies in the TUI too: leaving it on Raw here would make the
            // host silently ignore a project's privacy setting.
            var sqlText = Enum.TryParse<SqlTextSanitization>(
                project.Config.SqlTextPolicy, ignoreCase: true, out var parsed)
                ? parsed
                : SqlTextSanitization.Raw;

            var options = new IngestionOptions(redaction, [],
                PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()),
                SqlText: sqlText);
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
