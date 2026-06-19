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
    Console.WriteLine("usage: import <path> --project f.duckdb | top-slow --project f.duckdb");
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
        var redaction = Enum.Parse<RedactionMode>(redactionStr, ignoreCase: true);

        var (files, _) = XelSource.Resolve(path);
        using var db = DuckDbProject.Open(project);
        var svc = new IngestionService(db, new IngestionOptions(redaction, Array.Empty<FilterRule>()));
        var result = svc.Ingest(path, new XelReader().Read(files));
        Console.WriteLine(
            $"run {result.RunId}: read={result.Read} mapped={result.Mapped} " +
            $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures}");
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
    default:
        Console.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";
