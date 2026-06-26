// src/SqlFerret.Cli/Program.cs
using Microsoft.Data.SqlClient;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Project;
using SqlFerret.Core.Server;

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? "";
}

AuditProject? OpenProject()
{
    var dir = Arg("--project");
    if (string.IsNullOrWhiteSpace(dir))
    {
        Console.Error.WriteLine("--project <dir> is required");
        return null;
    }
    AuditProject ap;
    try { ap = AuditProject.OpenOrCreate(dir, Directory.GetCurrentDirectory()); }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
    {
        // File-at-path guard, malformed sqlferret.config.json, or a permission/IO error:
        // present a clean message instead of an unhandled stack trace.
        Console.Error.WriteLine($"--project: {ex.Message}");
        return null;
    }
    // Surface corrupt-manifest recovery so the user knows provenance was reset.
    if (ap.ManifestWarning is { } warning)
        Console.Error.WriteLine(warning);
    return ap;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: import <path> --project <dir> | top-slow --project <dir> | export-blocking --project <dir> [...] | query-store-import --project <dir> [--conn <s>] [--database <db>] [--no-plans] [--from <dt> --to <dt> | --last <N>{h|d}] | export-events --project <dir> --out <dir> [--kind blocking|deadlock|both] [--from <dt> --to <dt> | --last <N>{h|d}] [--fingerprint <hash>] [--database <id>] [--limit <n>]");
    return 1;
}

switch (args[0])
{
    case "import":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("import: missing <path> argument");
                return 1;
            }
            var path = args[1];
            var project = OpenProject();
            if (project is null) return 1;

            var redactionStr = Arg("--redaction", project.Config.RedactionPolicy);
            if (!Enum.TryParse<RedactionMode>(redactionStr, ignoreCase: true, out var redaction))
            {
                Console.Error.WriteLine($"import: invalid --redaction value '{redactionStr}'. Valid: off, hash, masked, full");
                return 1;
            }

            using var db = project.OpenDb();
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
                $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures} " +
                $"blocking={result.Blocking} deadlocks={result.Deadlocks} blockingParseFailures={result.BlockingParseFailures}");
            return 0;
        }
    case "top-slow":
        {
            var project = OpenProject();
            if (project is null) return 1;
            var limit = int.TryParse(Arg("--limit", "20"), out var l) ? l : 20;
            using var db = project.OpenDb();
            var q = new WorkloadQueries(db.Connection);
            var rows = q.TopSlow(limit, "total_duration_us", Array.Empty<FilterRule>());
            foreach (var s in rows)
                Console.WriteLine(
                    $"{s.StatementKind,-7} {s.Count,8}  total={DisplayFormat.Duration(s.TotalDurationUs, project.Config.DurationUnit),-12}  {Trim(s.NormalizedSql)}");
            return 0;
        }
    case "export-blocking":
        {
            var project = OpenProject();
            if (project is null) return 1;
            var format = Arg("--format", "both");           // json | md | both
            var samples = int.TryParse(Arg("--samples", "5"), out var sv) ? sv : 5;
            var full = Array.IndexOf(args, "--full") >= 0;
            var outPath = Arg("--out", "");
            if (outPath.Length > 0 && SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outPath))
            { Console.Error.WriteLine("export-blocking: invalid --out path"); return 1; }

            using var db = project.OpenDb();

            if (full)
            {
                // NDJSON dump of every report (bounded by file, not context)
                var q = new BlockingQueries(db.Connection);
                using var w = outPath.Length > 0 ? new StreamWriter(outPath) : null;
                TextWriter o = w ?? Console.Out;
                foreach (var b in q.TopBlockers(int.MaxValue))
                    foreach (var rep in q.SampleReports(b.Fingerprint, int.MaxValue))
                        o.WriteLine(System.Text.Json.JsonSerializer.Serialize(rep));
                return 0;
            }

            var digest = new BlockingDigest(db.Connection).Build(samplesPerPattern: samples);
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

            string json = System.Text.Json.JsonSerializer.Serialize(
                new BlockingDigestEnvelope(BlockingDigest.SchemaVersion, digest), jsonOpts);
            string md = SqlFerret.Cli.BlockingDigestMarkdown.Render(digest);

            string payload = format switch
            {
                "json" => json,
                "md" => md,
                _ => md + "\n\n```json\n" + json + "\n```\n"
            };
            if (outPath.Length > 0) File.WriteAllText(outPath, payload); else Console.WriteLine(payload);
            return 0;
        }
    case "query-store-import":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var connOverride = Arg("--conn", "");
            var connStr = connOverride.Length > 0 ? connOverride : project.Config.ConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                Console.Error.WriteLine("query-store-import: no connection string (set server.connectionString in config/.env, or pass --conn)");
                return 1;
            }

            var database = Arg("--database", "");
            var noPlans = Array.IndexOf(args, "--no-plans") >= 0;

            QueryStoreWindow window;
            try
            {
                window = QueryStoreWindow.Parse(
                    NullIfEmpty(Arg("--from", "")), NullIfEmpty(Arg("--to", "")), NullIfEmpty(Arg("--last", "")),
                    DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine($"query-store-import: {ex.Message}"); return 1; }

            if (!noPlans && !string.Equals(project.Config.RedactionPolicy, "off", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(
                    $"warning: --plans writes raw showplan XML; .sqlplan files may contain literal values not " +
                    $"covered by redaction (policy={project.Config.RedactionPolicy}). Use --no-plans to skip.");

            using var db = project.OpenDb();
            var svc = new QueryStoreImportService(connStr!, db, project.PlansFolder);
            var opts = new QueryStoreImportOptions(NullIfEmpty(database), WritePlans: !noPlans, Window: window);

            var showGauge = !Console.IsErrorRedirected;
            var progress = new SyncProgress<string>(s => { if (showGauge) Console.Error.Write("\r" + s.PadRight(60)); });

            QdsImportResult result;
            try { result = svc.Import(opts, progress); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
            {
                if (showGauge) Console.Error.WriteLine();
                Console.Error.WriteLine($"query-store-import: {ex.Message}");
                return 1;
            }
            if (showGauge) Console.Error.WriteLine();

            Console.WriteLine(
                $"qds run {result.RunId}: queries={result.QueriesCount} queryText={result.QueryTextCount} " +
                $"plans={result.PlansCount} runtimeRows={result.RuntimeStatRows} waitRows={result.WaitStatRows} " +
                $"plansWritten={result.PlanFilesWritten} planFailures={result.PlanWriteFailures}");
            return 0;
        }
    case "export-events":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var outDir = Arg("--out");
            if (string.IsNullOrWhiteSpace(outDir))
            { Console.Error.WriteLine("export-events: --out <dir> is required"); return 1; }
            if (SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outDir))
            { Console.Error.WriteLine("export-events: invalid --out path"); return 1; }

            var kindStr = Arg("--kind", "both");
            if (!Enum.TryParse<EventKind>(kindStr, ignoreCase: true, out var kind))
            { Console.Error.WriteLine($"export-events: invalid --kind '{kindStr}'. Valid: blocking, deadlock, both"); return 1; }

            QueryStoreWindow window;
            try
            {
                window = QueryStoreWindow.Parse(
                    NullIfEmpty(Arg("--from", "")), NullIfEmpty(Arg("--to", "")), NullIfEmpty(Arg("--last", "")),
                    DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine($"export-events: {ex.Message}"); return 1; }

            int? dbId = int.TryParse(Arg("--database", ""), out var dv) ? dv : null;
            var fingerprint = NullIfEmpty(Arg("--fingerprint", ""));
            var limit = int.TryParse(Arg("--limit", "100"), out var lv) ? lv : 100;

            if (kind == EventKind.Deadlock && (dbId is not null || fingerprint is not null))
                Console.Error.WriteLine("export-events: --fingerprint/--database are ignored for deadlocks");

            using var db = project.OpenDb();
            var svc = new EventExportService(db.Connection);
            var opts = new EventExportOptions(outDir, kind, window, fingerprint, dbId, limit);

            EventExportResult result;
            try { result = svc.Export(opts); }
            catch (ArgumentException ex) { Console.Error.WriteLine($"export-events: {ex.Message}"); return 1; }

            if (result.BlockingWritten == 0 && result.DeadlockWritten == 0 &&
                (result.BlockingSkipped > 0 || result.DeadlockSkipped > 0))
                Console.Error.WriteLine(
                    "export-events: nothing written; matching runs were imported with redaction != off. " +
                    "Re-import with --redaction off to export XML.");

            var summary = new
            {
                outDir = result.OutDir,
                indexPath = result.IndexPath,
                blocking = new { written = result.BlockingWritten, skipped = result.BlockingSkipped },
                deadlock = new { written = result.DeadlockWritten, skipped = result.DeadlockSkipped },
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
            return 0;
        }
    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";

static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
