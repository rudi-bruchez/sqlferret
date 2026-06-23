// src/SqlFerret.Cli/Program.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
var config = SqlFerretConfig.Load(Path.Combine(Directory.GetCurrentDirectory(), "sqlferret.config.json"));

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? "";
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: import <path> --project f.duckdb | top-slow --project f.duckdb | export-blocking --project f.duckdb [--format json|md|both] [--samples N] [--full] [--out file]");
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
            var project = Arg("--project", "workload.duckdb");
            var redactionStr = Arg("--redaction", config.RedactionPolicy);
            if (!Enum.TryParse<RedactionMode>(redactionStr, ignoreCase: true, out var redaction))
            {
                Console.Error.WriteLine($"import: invalid --redaction value '{redactionStr}'. Valid: off, hash, masked, full");
                return 1;
            }

            using var db = DuckDbProject.Open(project);
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
            var project = Arg("--project", "workload.duckdb");
            var limit = int.TryParse(Arg("--limit", "20"), out var l) ? l : 20;
            using var db = DuckDbProject.Open(project);
            var q = new WorkloadQueries(db.Connection);
            var rows = q.TopSlow(limit, "total_duration_us", Array.Empty<FilterRule>());
            foreach (var s in rows)
                Console.WriteLine(
                    $"{s.StatementKind,-7} {s.Count,8}  total={DisplayFormat.Duration(s.TotalDurationUs, config.DurationUnit),-12}  {Trim(s.NormalizedSql)}");
            return 0;
        }
    case "export-blocking":
        {
            var project = Arg("--project", "workload.duckdb");
            var format = Arg("--format", "both");           // json | md | both
            var samples = int.TryParse(Arg("--samples", "5"), out var sv) ? sv : 5;
            var full = Array.IndexOf(args, "--full") >= 0;
            var outPath = Arg("--out", "");
            if (outPath.Length > 0 && outPath.Contains(".."))
            { Console.Error.WriteLine("export-blocking: invalid --out path"); return 1; }

            using var db = DuckDbProject.Open(project);

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
    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";

file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
