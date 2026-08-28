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
            // host silently ignore a project's privacy setting. An invalid value must fail loudly,
            // the same as the CLI does, rather than silently falling back to the permissive Raw.
            // Validate the INPUT STRING against the defined enum names, not the parsed value:
            // Enum.TryParse("0", ...) succeeds and yields Raw, which IS a defined value, so
            // checking Enum.IsDefined on the parsed result alone lets numeric typos through.
            if (!Enum.GetNames<SqlTextSanitization>().Any(n =>
                    n.Equals(project.Config.SqlTextPolicy, StringComparison.OrdinalIgnoreCase))
                || !Enum.TryParse<SqlTextSanitization>(
                    project.Config.SqlTextPolicy, ignoreCase: true, out var sqlText))
            {
                throw new ArgumentException(
                    $"import: invalid ingest.sqlTextSanitization value '{project.Config.SqlTextPolicy}'. Valid: raw, literals");
            }

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
