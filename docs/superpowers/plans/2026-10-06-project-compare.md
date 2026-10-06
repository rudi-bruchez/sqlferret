# `sqlferret compare` Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

Goal: a `compare` CLI command that reads two existing SQLFerret projects read-only and emits a markdown or JSON digest of what changed between them: coverage, cost per execution, load per hour, statements present on one side only, and plan changes.

Architecture: one Core class, `SqlFerret.Core.Analysis.ProjectComparison`, opens an in-memory DuckDB connection, attaches both `sqlferret.duckdb` files `READ_ONLY` as `base` and `target`, checks preconditions, and runs every section as DuckDB SQL. Records live in `CompareResults.cs`. The CLI host parses flags, reads the display unit with `SqlFerretConfig.Load`, and renders markdown through `CompareDigestMarkdown`. Nothing is written into either project directory.

Tech stack: .NET 10 / C# 14, DuckDB.NET.Data.Full 1.5.3, xUnit.

Spec: `docs/superpowers/specs/2026-10-06-project-compare-design.md` (revision 2). Read it before any task; every task argues from it, and the section numbers below (§n) are its sections.

## Global Constraints

- Core stays host-agnostic and returns microseconds only; formatting through `DisplayFormat.Duration` happens in the CLI (`CLAUDE.md`, invariants).
- All aggregation is DuckDB SQL. C# only maps rows, compares small lists, and sanitizes printed text (§7).
- DuckDB.NET parameter names carry no leading `$`: SQL uses `$name`, the `Add` helper does `name.TrimStart('$')`.
- The only interpolated user text is the `ATTACH` path, single quotes doubled (`CLAUDE.md`, SQL safety, second exception). `--database` is always a bound parameter. Aliases `base` and `target` are constants.
- The CLI host never calls `AuditProject.OpenOrCreate` for `compare` (§3).
- No interface, no DI, no repository; primary constructors, records, collection expressions `[]`, raw string literals for SQL (`CLAUDE.md`, KISS and C# baseline).
- Test fixtures use only the anonymous vocabulary: `AppDb`, `AppSchema`, `WidgetRecalc`, `@WidgetId`, `@GadgetCode`, `@TenantId`, `@Code`.
- Exit codes: `0` success, `1` any error or refusal (`docs/cli-reference.md`).
- Commit messages: prose explaining why, no bold, no em dash, and no attribution trailer of any kind (no `Co-Authored-By`, no `Generated with`).
- Run `dotnet format` on the files a task touched before its commit; `dotnet build` must stay at 0 warnings.

## How to run tests in this plan

Every test step gives a filter and an exact expected count. Any other count, a higher one included, means the filter or the work is wrong: stop and say so. Setup and test run in one shell invocation, because shell state does not survive between calls.

```bash
cd <worktree> && dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5
```

Every task has a step that breaks the new code on purpose and watches the test fail. Reporting "2 of 3 failed" there is the success of that step, not a failure. If what you measure contradicts this plan (a count, a message, a behavior of DuckDB), stop and say so; do not bend the code to fit the plan.

## Review Focus

Inputs the spec implies that no section's happy path exercises, most likely first. Each has its test in the owning task.

1. A project whose first import was `raw` and a later one `literals`: no printed text may carry the value (Task 3, Task 7 end-to-end).
2. A project with two imports a day apart: per-hour figures use the sum of run spans, not the gap (Task 4).
3. A query hash with a leading zero: the plan link still joins (Task 6).
4. A base average of zero: the ratio is NULL and the JSON serializes (Task 5, Task 7).
5. `--base` and `--target` naming the same directory: every ranking empty, no error (Task 7).

---

### Task 1: Results records, attach, and preconditions

Files:
- Create: `src/SqlFerret.Core/Analysis/CompareResults.cs`
- Create: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Create: `tests/SqlFerret.Core.Tests/CompareFixture.cs`
- Create: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `DuckDbProject.Open(string)`, `DuckDbProject.BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy, SqlTextSanitization sqlText = SqlTextSanitization.Raw)`, `DuckDbProject.InsertBatch(long runId, IReadOnlyList<PreparedRow> rows)`, `DuckDbProject.InsertPlanProfileBatch(long runId, IReadOnlyList<PreparedPlanProfile> rows)`.
- Produces: every record below; `ProjectComparison(string baseDbPath, string targetDbPath)`; `public void Check(CompareOptions options)`; `CompareRefusedException`; `internal static string ProjectComparison.AttachSql(string path, string alias)`; `internal static int ProjectComparison.FingerprintGeneration(int version)`; `internal static string ProjectComparison.DescribeAttachFailure(string dbPath, Exception ex)`; the private `Open()` / `CheckPreconditions(...)` used by later tasks; the test helper `CompareFixture`.

- [ ] Step 1: Write `CompareResults.cs` in full. Later tasks fill these; they are defined once here so every task sees the same names.

```csharp
// src/SqlFerret.Core/Analysis/CompareResults.cs
namespace SqlFerret.Core.Analysis;

/// <summary>Seuils de compare. Tous en microsecondes ou en comptes : l'invariant de Core.</summary>
public record CompareThresholds(
    int MinExecutions = 5,
    long MinAvgDurationUs = 1_000,
    long MinActiveSpanUs = 600_000_000);

public record CompareOptions(int Limit, string? Database, CompareThresholds Thresholds);

public record CompareRunSpan(long RunId, DateTime First, DateTime Last, long SpanUs, long Executions);

public record CompareSideCoverage(
    string ProjectDir,
    IReadOnlyList<CompareRunSpan> Runs,
    long ActiveSpanUs,
    long? LargestGapUs,
    long? LargestGapRunId,
    long Executions,
    long DistinctStatements,
    IReadOnlyList<string> Databases,
    long OtherDatabases,
    IReadOnlyList<int> NormalizerVersions,
    IReadOnlyList<string> RedactionPolicies,
    IReadOnlyList<string> SqlTextPolicies,
    long? MinDurationUs,
    double QueryHashShare,
    long EligiblePlanProfiles,
    long ExcludedPlanProfiles);

public record CompareCoverage(CompareSideCoverage Base, CompareSideCoverage Target, IReadOnlyList<string> Notes);

/// <param name="Ratio">Moyenne cible sur moyenne de base ; NULL quand la base vaut zero (§7).</param>
public record CostRow(
    string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql,
    long BaseCount, long TargetCount,
    double BaseAvgUs, double TargetAvgUs, double BaseP95Us, double TargetP95Us,
    double? BaseAvgCpuUs, double? TargetAvgCpuUs, double? BaseAvgReads, double? TargetAvgReads,
    double? Ratio);

public record LoadRow(
    string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql,
    double BaseExecPerHour, double TargetExecPerHour,
    double BaseUsPerHour, double TargetUsPerHour, double DeltaUsPerHour);

public record OneSideRow(
    string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql,
    long Executions, long TotalDurationUs, double? UsPerHour);

public record OneSideList(IReadOnlyList<OneSideRow> Rows, long Total);

public record PlanChangeRow(
    string QueryHash,
    IReadOnlyList<string> BasePlanHashes, IReadOnlyList<string> TargetPlanHashes, bool PlanChanged,
    IReadOnlyList<string> AppearedKinds, IReadOnlyList<string> DisappearedKinds,
    double? BaseMedianUs, double? TargetMedianUs, string? LinkedNormalizedHash);

public record PlanSection(
    bool Skipped, string? SkipReason, IReadOnlyList<PlanChangeRow> Rows, long Total, long UnlinkedExcluded);

public record CompareDigestResult(
    CompareCoverage Coverage,
    IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains,
    bool LoadComputed, IReadOnlyList<LoadRow> LoadIncreases, IReadOnlyList<LoadRow> LoadDecreases,
    OneSideList Appeared, OneSideList Disappeared,
    PlanSection Plans);

public record CompareDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, CompareDigestResult Digest);

/// <summary>Un refus du §5 : le message est destine tel quel a l'utilisateur.</summary>
public sealed class CompareRefusedException(string message) : Exception(message);
```

- [ ] Step 2: Write the test fixture. It builds a project directory holding only `sqlferret.duckdb`, through the real insert paths.

```csharp
// tests/SqlFerret.Core.Tests/CompareFixture.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

/// <summary>
/// Un projet de test : un dossier temporaire et son sqlferret.duckdb, rempli par les chemins
/// d'insertion reels. Rien d'autre n'est cree dans le dossier, ce que le test de lecture seule
/// de la CLI verifie.
/// </summary>
public sealed class CompareFixture : IDisposable
{
    public string Dir { get; }
    public string DbPath => Path.Combine(Dir, "sqlferret.duckdb");

    /// <param name="suffix">Ajoute au nom du dossier, pour un chemin qui contient une apostrophe.</param>
    public CompareFixture(string suffix = "")
    {
        Dir = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}{suffix}");
        Directory.CreateDirectory(Dir);
    }

    public record Exec(string Hash, string Sql, long DurationUs, DateTime At,
        string Db = "AppDb", string? QueryHash = null, long? CpuUs = null, long? Reads = null);

    /// <summary>Un import : un run, ses executions. Rend le run_id.</summary>
    public long Import(IEnumerable<Exec> execs, SqlTextSanitization policy = SqlTextSanitization.Raw,
        string redaction = "off")
    {
        using var p = DuckDbProject.Open(DbPath);
        var run = p.BeginRun("logs/", 1, 100, redaction, policy);
        var rows = execs.Select(e => new PreparedRow(
            new ExecutionEvent
            {
                EventName = "rpc_completed",
                EventClass = EventClass.RpcCall,
                ObjectName = "AppSchema.WidgetRecalc",
                SqlTextRaw = e.Sql,
                DatabaseName = e.Db,
                SessionId = 52,
                DurationUs = e.DurationUs,
                CpuTimeUs = e.CpuUs,
                LogicalReads = e.Reads,
                QueryHash = e.QueryHash,
                CapturedAt = e.At,
                XeFileName = "s_0.xel",
            },
            new NormalizedQuery(e.Sql, e.Hash, "EXEC", "AppSchema.WidgetRecalc", null, false),
            [])).ToList();
        p.InsertBatch(run, rows);
        return run;
    }

    /// <summary>Ajoute des plan profiles a un run existant.</summary>
    public void Plans(long runId, params PlanProfile[] profiles)
    {
        using var p = DuckDbProject.Open(DbPath);
        p.InsertPlanProfileBatch(runId, profiles.Select(x => new PreparedPlanProfile(x, PlanWriteOutcome.WroteFirst)).ToList());
    }

    public static PlanProfile Plan(string queryHash, string planHash, long durationUs,
        string source = "queryplanhash", params PlanFinding[] findings) => new()
    {
        PlanHash = planHash,
        PlanHashSource = source,
        FileStem = "p_" + planHash,
        StatementCount = source == "multi" ? 2 : 1,
        QueryHash = queryHash,
        StatementType = "SELECT",
        StatementText = "SELECT * FROM AppSchema.WidgetRecalc WHERE WidgetId = 42",
        StatementTextLength = 56,
        CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        DurationUs = durationUs,
        Findings = findings,
    };

    /// <summary>Ecriture directe, pour simuler un etat ancien (version, colonne manquante).</summary>
    public void Sql(string sql)
    {
        using var p = DuckDbProject.Open(DbPath);
        using var c = p.Connection.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
}
```

If `ExecutionEvent` does not expose `CpuTimeUs`, `LogicalReads` or `QueryHash` as init properties, stop and report: `EventMapper.Map` sets them (`EventMapper.cs`), so they should exist.

- [ ] Step 3: Write the failing tests for this task.

```csharp
// tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Normalization;
using Xunit;

public class ProjectComparisonTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CompareOptions Opt = new(10, null, new CompareThresholds());

    private static IEnumerable<CompareFixture.Exec> Burst(string hash, int n, long durationUs, DateTime start, int stepSeconds = 60) =>
        Enumerable.Range(0, n).Select(i => new CompareFixture.Exec(hash, $"exec AppSchema.WidgetRecalc @WidgetId = ?", durationUs, start.AddSeconds(i * stepSeconds)));

    [Fact]
    public void Attach_sql_doubles_single_quotes_and_nothing_else()
    {
        Assert.Equal("ATTACH '/tmp/it''s base/sqlferret.duckdb' AS base (READ_ONLY)",
            ProjectComparison.AttachSql("/tmp/it's base/sqlferret.duckdb", "base"));
        Assert.Equal(@"ATTACH 'C:\new\sqlferret.duckdb' AS target (READ_ONLY)",
            ProjectComparison.AttachSql(@"C:\new\sqlferret.duckdb", "target"));
    }

    [Fact]
    public void A_project_compares_with_itself_through_a_path_holding_a_quote()
    {
        using var f = new CompareFixture("_it's");
        f.Import(Burst("h1", 1, 5_000, T0));
        new ProjectComparison(f.DbPath, f.DbPath).Check(Opt);   // must not throw
    }

    [Fact]
    public void A_missing_database_file_is_refused_with_its_path()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        var missing = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}", "sqlferret.duckdb");
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, missing).Check(Opt));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void A_project_missing_a_migrated_column_is_refused_and_told_how_to_migrate()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        b.Sql("ALTER TABLE ingestion_runs DROP COLUMN sql_text_policy");
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, b.DbPath).Check(Opt));
        Assert.Contains("sql_text_policy", ex.Message);
        Assert.Contains("top-slow", ex.Message);
    }

    [Fact]
    public void An_empty_side_after_the_database_filter_is_refused()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        var ex = Assert.Throws<CompareRefusedException>(() =>
            new ProjectComparison(a.DbPath, b.DbPath).Check(Opt with { Database = "NoSuchDb" }));
        Assert.Contains("no execution", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fingerprint_generations_v3_and_v4_are_comparable_and_v2_is_not()
    {
        Assert.Equal(ProjectComparison.FingerprintGeneration(3), ProjectComparison.FingerprintGeneration(4));
        Assert.NotEqual(ProjectComparison.FingerprintGeneration(2), ProjectComparison.FingerprintGeneration(4));
    }

    [Fact]
    public void Different_generations_across_sides_are_refused_even_after_reclassify()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        // Ce que laisse un reclassify sur un import ancien : normalized_queries a jour, le run non.
        b.Sql("UPDATE ingestion_runs SET normalizer_version = 2");
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, b.DbPath).Check(Opt));
        Assert.Contains("re-import", ex.Message);
        Assert.DoesNotContain("reclassify", ex.Message);
    }

    [Fact]
    public void A_v3_side_against_a_v4_side_is_accepted()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        b.Sql("UPDATE ingestion_runs SET normalizer_version = 3");
        new ProjectComparison(a.DbPath, b.DbPath).Check(Opt);   // must not throw
    }

    [Fact]
    public void A_lock_error_is_described_as_another_process_holding_the_project()
    {
        var msg = ProjectComparison.DescribeAttachFailure("/p/sqlferret.duckdb",
            new Exception("IO Error: Could not set lock on file \"/p/sqlferret.duckdb\": Conflicting lock is held in /usr/bin/x (PID 3)"));
        Assert.Contains("/p/sqlferret.duckdb", msg);
        Assert.Contains("another SQLFerret process", msg);
    }
}
```

`Check(CompareOptions)` is a public method that attaches and runs the §5 preconditions; it returns normally when the pair is comparable and throws `CompareRefusedException` otherwise.

- [ ] Step 4: Run the tests and watch them fail to compile.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: build error, `ProjectComparison` does not exist.

- [ ] Step 5: Write `ProjectComparison.cs` with attach and preconditions.

```csharp
// src/SqlFerret.Core/Analysis/ProjectComparison.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Normalization;

namespace SqlFerret.Core.Analysis;

/// <summary>
/// Compare deux projets, en lecture seule : une connexion en memoire, les deux fichiers attaches
/// READ_ONLY sous les alias constants base et target. Spec :
/// docs/superpowers/specs/2026-10-06-project-compare-design.md.
/// </summary>
public sealed class ProjectComparison(string baseDbPath, string targetDbPath)
{
    public const int SchemaVersion = 1;

    /// <summary>Colonnes lues par les sections. Un projet anterieur a une migration n'en a pas
    /// certaines, et l'attache READ_ONLY ne la fait pas tourner (§5).</summary>
    private static readonly (string Table, string Column)[] RequiredColumns =
    [
        ("executions", "run_id"), ("executions", "captured_at"), ("executions", "normalized_hash"),
        ("executions", "duration_us"), ("executions", "cpu_time_us"), ("executions", "logical_reads"),
        ("executions", "database_name"), ("executions", "query_hash"),
        ("normalized_queries", "normalized_sql"), ("normalized_queries", "statement_kind"),
        ("normalized_queries", "primary_table"),
        ("ingestion_runs", "run_id"), ("ingestion_runs", "normalizer_version"),
        ("ingestion_runs", "redaction_policy"), ("ingestion_runs", "sql_text_policy"),
        ("plan_profiles", "plan_profile_id"), ("plan_profiles", "plan_hash"),
        ("plan_profiles", "plan_hash_source"), ("plan_profiles", "query_hash"),
        ("plan_profiles", "duration_us"),
        ("plan_findings", "plan_profile_id"), ("plan_findings", "kind"),
    ];

    /// <summary>Le chemin est la seule entree interpolee : exception de CLAUDE.md, SQL safety.
    /// ATTACH refuse un parametre lie sur DuckDB.NET 1.5.3 ; une chaine DuckDB ne traite pas
    /// l'antislash comme un echappement, doubler l'apostrophe suffit.</summary>
    internal static string AttachSql(string path, string alias) =>
        $"ATTACH '{path.Replace("'", "''")}' AS {alias} (READ_ONLY)";

    /// <summary>
    /// Generation d'empreinte d'une version du normaliseur. La version couvre aussi la
    /// classification, donc deux versions peuvent produire les memes empreintes. Le commentaire
    /// de QueryNormalizer.Version dit que v4 laisse NormalizedSql, donc le fingerprint, inchange
    /// par rapport a v3. Une version qui change la reecriture des tokens ouvre une generation.
    /// </summary>
    internal static int FingerprintGeneration(int version) => version is 3 or 4 ? 3 : version;

    internal static string DescribeAttachFailure(string dbPath, Exception ex) =>
        ex.Message.Contains("Could not set lock", StringComparison.OrdinalIgnoreCase)
            ? $"compare: {dbPath} is open in another SQLFerret process (a TUI, an import); close it first"
            : $"compare: cannot attach {dbPath}: {ex.Message}";

    /// <summary>Attache les deux projets et verifie le §5 ; leve CompareRefusedException sinon.</summary>
    public void Check(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
    }

    private DuckDBConnection Open()
    {
        foreach (var p in (string[])[baseDbPath, targetDbPath])
            if (!File.Exists(p)) throw new CompareRefusedException($"compare: no project database at {p}");

        var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        foreach (var (path, alias) in (ReadOnlySpan<(string, string)>)[(baseDbPath, "base"), (targetDbPath, "target")])
        {
            try { Exec(conn, AttachSql(path, alias)); }
            catch (DuckDBException ex)
            {
                conn.Dispose();
                throw new CompareRefusedException(DescribeAttachFailure(path, ex));
            }
        }
        return conn;
    }

    private void CheckPreconditions(DuckDBConnection conn, CompareOptions options)
    {
        foreach (var (alias, path) in (ReadOnlySpan<(string, string)>)[("base", baseDbPath), ("target", targetDbPath)])
        {
            var have = new HashSet<(string, string)>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT table_name, column_name FROM duckdb_columns() WHERE database_name = $db";
                Add(c, "$db", alias);
                using var r = c.ExecuteReader();
                while (r.Read()) have.Add((r.GetString(0), r.GetString(1)));
            }
            foreach (var need in RequiredColumns)
                if (!have.Contains(need))
                    throw new CompareRefusedException(
                        $"compare: {path} predates column {need.Table}.{need.Column}; run any other command on it once (for example top-slow) to migrate it");
        }

        foreach (var (alias, path) in (ReadOnlySpan<(string, string)>)[("base", baseDbPath), ("target", targetDbPath)])
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"SELECT count(*) FROM {alias}.executions e WHERE e.normalized_hash IS NOT NULL{DbWhere(options)}";
            AddDb(c, options);
            if (Convert.ToInt64(c.ExecuteScalar()) == 0)
                throw new CompareRefusedException($"compare: {path} has no execution{(options.Database is null ? "" : $" in database {options.Database}")}");
        }

        var versions = new List<(string Path, int Version)>();
        foreach (var (alias, path) in (ReadOnlySpan<(string, string)>)[("base", baseDbPath), ("target", targetDbPath)])
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"""
                SELECT DISTINCT coalesce(r.normalizer_version, -1)
                FROM {alias}.ingestion_runs r
                WHERE r.run_id IN (SELECT e.run_id FROM {alias}.executions e)
                """;
            using var rd = c.ExecuteReader();
            while (rd.Read()) versions.Add((path, rd.GetInt32(0)));
        }
        if (versions.Select(v => FingerprintGeneration(v.Version)).Distinct().Count() > 1 || versions.Any(v => v.Version < 0))
            throw new CompareRefusedException(
                "compare: the two projects hold fingerprints from different normalizer generations ("
                + string.Join(", ", versions.Distinct().Select(v => $"{v.Path}: v{v.Version}"))
                + "); re-import the older capture, since its stored hashes cannot be recomputed in place");
    }

    // L'alias e et la colonne database_name sont constants ; la valeur est liee.
    private static string DbWhere(CompareOptions o) => o.Database is null ? "" : " AND e.database_name = $db";

    private static void AddDb(System.Data.IDbCommand c, CompareOptions o)
    {
        if (o.Database is not null) Add(c, "$db", o.Database);
    }

    private static void Exec(DuckDBConnection conn, string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
```

The using of `SqlFerret.Core.Normalization` is for Task 3; if the analyzer flags it as unused at this task, remove it here and add it back in Task 3.

- [ ] Step 6: Run the tests.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: 9 passed, 0 failed.

- [ ] Step 7: Break it on purpose. Change `FingerprintGeneration` to `=> version;` and rerun the same command.
Expected: exactly 2 failed (`Fingerprint_generations_v3_and_v4_are_comparable_and_v2_is_not`, `A_v3_side_against_a_v4_side_is_accepted`), 7 passed. That is the success of this step. Restore the code and rerun: 9 passed.

- [ ] Step 8: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/CompareResults.cs src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/CompareFixture.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/CompareResults.cs src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/CompareFixture.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): attach two projects read-only and refuse what cannot be compared" -m "The comparison is only worth reading when both sides hold fingerprints of one generation, and reclassify rewrites the version without the hashes, so the check reads ingestion_runs. A project held open elsewhere or predating a migration is refused with a message that says what to do."
```

---

### Task 2: Coverage (§6)

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Open()`, `CheckPreconditions`, `DbWhere`, `AddDb` from Task 1.
- Produces: `internal CompareCoverage Coverage(DuckDBConnection conn, CompareOptions options)`; a public test seam `public CompareCoverage CoverageOnly(CompareOptions options)` that opens, checks and returns the coverage (used by tests only until Task 7, then kept).

- [ ] Step 1: Add the failing tests to `ProjectComparisonTests`.

```csharp
    [Fact]
    public void Active_span_is_the_sum_of_run_spans_not_the_gap_between_imports()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 2, 5_000, T0));             // 0 and +60 s
        a.Import(Burst("h1", 2, 5_000, T0.AddDays(1)));  // a day later, 0 and +60 s
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(2, cov.Base.Runs.Count);
        Assert.Equal(120_000_000L, cov.Base.ActiveSpanUs);
        Assert.Equal(4L, cov.Base.Executions);
    }

    [Fact]
    public void A_short_active_span_adds_the_per_hour_note()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 3, 5_000, T0));             // 120 s, under 10 minutes
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Contains(cov.Notes, n => n.Contains("per-hour", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_large_gap_inside_one_run_is_named()
    {
        using var a = new CompareFixture();
        var run = a.Import(
            Burst("h1", 2, 5_000, T0, 300).Concat(Burst("h1", 2, 5_000, T0.AddHours(3), 300)));
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(run, cov.Base.LargestGapRunId);
        Assert.Contains(cov.Notes, n => n.Contains($"run {run}"));
    }

    [Fact]
    public void Coverage_reports_policies_and_plan_eligibility()
    {
        using var a = new CompareFixture();
        var run = a.Import(Burst("h1", 2, 5_000, T0), SqlTextSanitization.Literals, "hash");
        a.Plans(run,
            CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000),
            CompareFixture.Plan("0A1B2C3D4E5F6071", "M1", 1_000, "multi"));
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(["literals"], cov.Base.SqlTextPolicies);
        Assert.Equal(["hash"], cov.Base.RedactionPolicies);
        Assert.Equal(1L, cov.Base.EligiblePlanProfiles);
        Assert.Equal(1L, cov.Base.ExcludedPlanProfiles);
        Assert.Contains(cov.Notes, n => n.Contains("predicates", StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] Step 2: Run and watch them fail to compile.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: build error, `CoverageOnly` does not exist.

- [ ] Step 3: Implement. Add to `ProjectComparison`:

```csharp
    public CompareCoverage CoverageOnly(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
        return Coverage(conn, options);
    }

    /// <summary>Executions retenues d'un cote, apres le filtre --database. alias est constant.</summary>
    private static string Execs(string alias, CompareOptions o) =>
        $"(SELECT * FROM {alias}.executions e WHERE e.normalized_hash IS NOT NULL{DbWhere(o)})";

    internal CompareCoverage Coverage(DuckDBConnection conn, CompareOptions options)
    {
        var b = Side(conn, "base", baseDbPath, options);
        var t = Side(conn, "target", targetDbPath, options);
        var notes = new List<string>();
        foreach (var s in (CompareSideCoverage[])[b, t])
        {
            if (s.ActiveSpanUs < options.Thresholds.MinActiveSpanUs)
                notes.Add($"{s.ProjectDir}: active span under the threshold, per-hour figures are not computed");
            if (s.LargestGapUs is { } gap && s.LargestGapRunId is { } runId)
            {
                var span = s.Runs.First(r => r.RunId == runId).SpanUs;
                if (span > 0 && gap * 4 > span)
                    notes.Add($"{s.ProjectDir}: run {runId} has a gap of more than a quarter of its span; it probably holds several disjoint captures, and its per-hour figures understate the load");
            }
            if (s.EligiblePlanProfiles == 0)
                notes.Add($"{s.ProjectDir}: no single-statement plan profile, the plan section is skipped");
        }
        notes.Add("SQLFerret does not know the predicates of the capture sessions; two captures with different duration thresholds have per-hour loads that cannot be compared");
        return new CompareCoverage(b, t, notes);
    }

    private static CompareSideCoverage Side(DuckDBConnection conn, string alias, string dbPath, CompareOptions o)
    {
        var x = Execs(alias, o);
        var runs = new List<CompareRunSpan>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT run_id, min(captured_at), max(captured_at),
                       epoch_us(max(captured_at)) - epoch_us(min(captured_at)), count(*)
                FROM {x} GROUP BY run_id ORDER BY run_id
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            while (r.Read())
                runs.Add(new CompareRunSpan(r.GetInt64(0), r.GetDateTime(1), r.GetDateTime(2), r.GetInt64(3), r.GetInt64(4)));
        }

        long? gapUs = null, gapRun = null;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT run_id, gap FROM (
                  SELECT run_id, epoch_us(captured_at) - lag(epoch_us(captured_at))
                         OVER (PARTITION BY run_id ORDER BY captured_at) AS gap
                  FROM {x})
                WHERE gap IS NOT NULL ORDER BY gap DESC, run_id LIMIT 1
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            if (r.Read()) { gapRun = r.GetInt64(0); gapUs = r.GetInt64(1); }
        }

        long execs, distinct; long? minDur; double qhShare;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT count(*), count(DISTINCT normalized_hash), min(duration_us),
                       avg(CASE WHEN query_hash IS NULL THEN 0.0 ELSE 1.0 END)
                FROM {x}
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            r.Read();
            execs = r.GetInt64(0); distinct = r.GetInt64(1);
            minDur = r.IsDBNull(2) ? null : r.GetInt64(2);
            qhShare = r.IsDBNull(3) ? 0 : r.GetDouble(3);
        }

        var dbs = Strings(conn, $"SELECT DISTINCT database_name FROM {x} WHERE database_name IS NOT NULL ORDER BY 1", o);
        var versions = Strings(conn, $"SELECT DISTINCT CAST(normalizer_version AS VARCHAR) FROM {alias}.ingestion_runs WHERE run_id IN (SELECT run_id FROM {x}) ORDER BY 1", o)
            .Select(int.Parse).ToList();
        var redaction = Strings(conn, $"SELECT DISTINCT coalesce(redaction_policy, 'unknown') FROM {alias}.ingestion_runs WHERE run_id IN (SELECT run_id FROM {x}) ORDER BY 1", o);
        var textPolicies = Strings(conn, $"SELECT DISTINCT coalesce(sql_text_policy, 'raw') FROM {alias}.ingestion_runs ORDER BY 1", o);

        long eligible, excluded;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT count(*) FILTER (WHERE plan_hash_source = 'queryplanhash' AND query_hash IS NOT NULL),
                       count(*) FILTER (WHERE NOT (plan_hash_source = 'queryplanhash' AND query_hash IS NOT NULL))
                FROM {alias}.plan_profiles
                """;
            using var r = c.ExecuteReader();
            r.Read();
            eligible = r.GetInt64(0); excluded = r.GetInt64(1);
        }

        const int MaxDatabases = 10;
        return new CompareSideCoverage(
            Path.GetDirectoryName(dbPath) ?? dbPath, runs, runs.Sum(r => r.SpanUs), gapUs, gapRun,
            execs, distinct, dbs.Take(MaxDatabases).ToList(), Math.Max(0, dbs.Count - MaxDatabases),
            versions, redaction, textPolicies, minDur, qhShare, eligible, excluded);
    }

    private static List<string> Strings(DuckDBConnection conn, string sql, CompareOptions o)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        if (sql.Contains("$db")) AddDb(c, o);
        using var r = c.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
```

`SqlTextPolicies` deliberately reads every run of the project, filtered or not: a text is written by whichever run saw its hash first (§7), so a filter on executions does not bound which policy produced it. `textPolicies` maps a NULL policy (a run older than the column) to `raw`, which is what such a run stored.

- [ ] Step 4: Run.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: 13 passed, 0 failed.

- [ ] Step 5: Break it. Replace `runs.Sum(r => r.SpanUs)` with `runs.Count == 0 ? 0 : (long)(runs[^1].Last - runs[0].First).TotalMicroseconds` and rerun.
Expected: exactly 1 failed (`Active_span_is_the_sum_of_run_spans_not_the_gap_between_imports`), 12 passed. Restore; 13 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): coverage block with per-run spans and gaps" -m "Every number in the digest depends on the windows it was computed over. The active span sums the runs, so two imports a day apart are not read as a quiet day, and a run with a large internal gap is named because a folder import can hold disjoint captures."
```

---

### Task 3: Printed text (§7, "The printed text")

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `QueryNormalizer.Normalize(string)` returning `NormalizedQuery(NormalizedSql, NormalizedHash, StatementKind, PrimaryTable, TargetObject, TokenizeFailed, QiCollapsedSql)`; `SqlTextSanitizer.Placeholder`.
- Produces: `internal static string ProjectComparison.DisplayText(string stored, bool sanitize)`; `internal static bool ProjectComparison.MustSanitize(CompareCoverage coverage)`.

- [ ] Step 1: Failing tests.

```csharp
    [Fact]
    public void Display_text_collapses_a_double_quoted_value_when_sanitizing()
    {
        var t = ProjectComparison.DisplayText("select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"", sanitize: true);
        Assert.DoesNotContain("GADGET-7781", t);
    }

    [Fact]
    public void Display_text_redacts_a_text_that_does_not_tokenize()
    {
        var t = ProjectComparison.DisplayText("select 'widgetrecalc", sanitize: true);
        Assert.Equal(SqlTextSanitizer.Placeholder, t);
    }

    [Fact]
    public void Display_text_leaves_an_already_sanitized_text_unchanged()
    {
        var stored = QueryNormalizer.Normalize("exec AppSchema.WidgetRecalc @WidgetId = 42").QiCollapsedSql;
        Assert.Equal(stored, ProjectComparison.DisplayText(stored, sanitize: true));
    }

    [Fact]
    public void Display_text_is_verbatim_when_not_sanitizing()
    {
        const string s = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";
        Assert.Equal(s, ProjectComparison.DisplayText(s, sanitize: false));
    }

    [Fact]
    public void Sanitizing_is_required_unless_every_run_on_both_sides_is_raw()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        Assert.False(ProjectComparison.MustSanitize(new ProjectComparison(a.DbPath, b.DbPath).CoverageOnly(Opt)));
        b.Import(Burst("h1", 1, 5_000, T0.AddHours(1)), SqlTextSanitization.Literals);
        Assert.True(ProjectComparison.MustSanitize(new ProjectComparison(a.DbPath, b.DbPath).CoverageOnly(Opt)));
    }
```

Before writing code, run the tokenize-failure input by hand once: if `QueryNormalizer.Normalize("select 'widgetrecalc").TokenizeFailed` is false on this tree, stop and report it; the test input must then be replaced by one that does fail, found by reading `TokenNormalizer`, and the plan corrected.

- [ ] Step 2: Run, expect a build error (`DisplayText` missing).

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`

- [ ] Step 3: Implement.

```csharp
    /// <summary>
    /// Le texte imprime. normalized_queries garde le premier texte ecrit pour une empreinte,
    /// quelle que soit la politique des runs suivants : la politique est par run, le texte par
    /// empreinte. D'ou une re-normalisation au moment du rapport, qui tient quel que soit le run
    /// d'origine. On prend QiCollapsedSql, ce que SqlTextSanitizer.Apply stocke au niveau
    /// literals ; Apply lui-meme n'est pas appele, son premier retour est le texte destine a
    /// sql_text_raw (l'instruction deballee d'un sp_executesql).
    /// </summary>
    internal static string DisplayText(string stored, bool sanitize)
    {
        if (!sanitize) return stored;
        var nq = QueryNormalizer.Normalize(stored);
        return nq.TokenizeFailed ? SqlTextSanitizer.Placeholder : nq.QiCollapsedSql;
    }

    internal static bool MustSanitize(CompareCoverage c) =>
        c.Base.SqlTextPolicies.Concat(c.Target.SqlTextPolicies).Any(p => p != "raw");
```

- [ ] Step 4: Run. Expected: 18 passed, 0 failed.

- [ ] Step 5: Break it: make `DisplayText` return `nq.NormalizedSql` instead of `nq.QiCollapsedSql`. Rerun.
Expected: exactly 1 failed (`Display_text_collapses_a_double_quoted_value_when_sanitizing`), 17 passed. If `Display_text_leaves_an_already_sanitized_text_unchanged` also fails, that is a real finding about idempotency: stop and report. Restore; 18 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): sanitize printed statement text at report time" -m "A project whose first import was raw keeps the raw text of every hash it saw then, so the policy of a project says nothing about a given row. Re-normalizing the printed text holds whichever run wrote it."
```

---

### Task 4: Load per hour and one-sided statements (§8, §9)

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Execs`, `AddDb`, `DisplayText`, `CompareCoverage.Base/Target.ActiveSpanUs`.
- Produces: `internal (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? Load(DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)` (null when not computed); `internal (OneSideList Appeared, OneSideList Disappeared) OneSided(DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)`; test seam `public (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? LoadOnly(CompareOptions o)` and `public (OneSideList Appeared, OneSideList Disappeared) OneSidedOnly(CompareOptions o)`.

- [ ] Step 1: Failing tests. Captures are one hour long so per-hour is computed.

```csharp
    [Fact]
    public void Ten_times_more_frequent_at_the_same_speed_ranks_as_a_load_increase()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 6, 10_000, T0, 600));      // 6 runs over 50 min
        b.Import(Burst("h1", 60, 10_000, T0, 50));      // 60 over 49 min 10 s
        var load = new ProjectComparison(a.DbPath, b.DbPath).LoadOnly(Opt);
        Assert.NotNull(load);
        var row = Assert.Single(load.Value.Up);
        Assert.Equal("h1", row.NormalizedHash);
        // Base : 60 000 us sur 3 000 s de span actif, soit 72 000 us par heure.
        Assert.Equal(72_000d, row.BaseUsPerHour, 0);
        Assert.Empty(load.Value.Down);
    }

    [Fact]
    public void Load_is_not_computed_below_the_active_span_threshold()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 3, 10_000, T0));
        Assert.Null(new ProjectComparison(a.DbPath, a.DbPath).LoadOnly(Opt));
    }

    [Fact]
    public void Appeared_and_disappeared_are_listed_with_their_full_counts()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("gone1", 2, 10_000, T0, 600).Concat(Burst("gone2", 2, 10_000, T0, 600)).Concat(Burst("both", 2, 10_000, T0, 600)));
        b.Import(Burst("new1", 2, 10_000, T0, 600).Concat(Burst("both", 2, 10_000, T0, 600)));
        var (appeared, disappeared) = new ProjectComparison(a.DbPath, b.DbPath).OneSidedOnly(Opt with { Limit = 1 });
        Assert.Equal(1L, appeared.Total);
        Assert.Equal("new1", Assert.Single(appeared.Rows).NormalizedHash);
        Assert.Equal(2L, disappeared.Total);
        Assert.Single(disappeared.Rows);
    }
```

- [ ] Step 2: Run, expect a build error.

- [ ] Step 3: Implement.

```csharp
    private const double UsPerHour = 3_600_000_000d;

    public (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? LoadOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        var cov = Coverage(conn, o);
        return Load(conn, o, cov, MustSanitize(cov));
    }

    public (OneSideList Appeared, OneSideList Disappeared) OneSidedOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        var cov = Coverage(conn, o);
        return OneSided(conn, o, cov, MustSanitize(cov));
    }

    internal (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? Load(
        DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)
    {
        var min = o.Thresholds.MinActiveSpanUs;
        if (cov.Base.ActiveSpanUs < min || cov.Target.ActiveSpanUs < min) return null;

        string Sql(string filter, string order) => $"""
            WITH b AS (SELECT normalized_hash h, count(*) n, sum(duration_us)::DOUBLE d FROM {Execs("base", o)} GROUP BY 1),
                 t AS (SELECT normalized_hash h, count(*) n, sum(duration_us)::DOUBLE d FROM {Execs("target", o)} GROUP BY 1)
            SELECT b.h, q.statement_kind, q.primary_table, q.normalized_sql,
                   b.n * $bf, t.n * $tf, b.d * $bf, t.d * $tf, t.d * $tf - b.d * $bf AS delta
            FROM b JOIN t ON b.h = t.h
            JOIN target.normalized_queries q ON q.normalized_hash = b.h
            WHERE greatest(b.n, t.n) >= $minExec AND {filter}
            ORDER BY {order}, b.h
            LIMIT $lim
            """;

        List<LoadRow> Read(string filter, string order)
        {
            using var c = conn.CreateCommand();
            c.CommandText = Sql(filter, order);
            AddDb(c, o);
            Add(c, "$bf", UsPerHour / cov.Base.ActiveSpanUs);
            Add(c, "$tf", UsPerHour / cov.Target.ActiveSpanUs);
            Add(c, "$minExec", o.Thresholds.MinExecutions);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var list = new List<LoadRow>();
            while (r.Read())
                list.Add(new LoadRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    DisplayText(r.GetString(3), sanitize),
                    r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8)));
            return list;
        }

        // filter et order sont des constantes de ce fichier, jamais une entree.
        return (Read("delta > 0", "delta DESC"), Read("delta < 0", "delta ASC"));
    }

    internal (OneSideList Appeared, OneSideList Disappeared) OneSided(
        DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)
    {
        bool perHour = cov.Base.ActiveSpanUs >= o.Thresholds.MinActiveSpanUs
                    && cov.Target.ActiveSpanUs >= o.Thresholds.MinActiveSpanUs;

        OneSideList List(string present, string absent, long spanUs)
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"""
                WITH p AS (SELECT normalized_hash h, count(*) n, sum(duration_us)::BIGINT d FROM {Execs(present, o)} GROUP BY 1),
                     a AS (SELECT DISTINCT normalized_hash h FROM {Execs(absent, o)})
                SELECT p.h, q.statement_kind, q.primary_table, q.normalized_sql, p.n, p.d, count(*) OVER () AS total
                FROM p JOIN {present}.normalized_queries q ON q.normalized_hash = p.h
                WHERE p.h NOT IN (SELECT h FROM a)
                ORDER BY p.d DESC, p.h
                LIMIT $lim
                """;
            AddDb(c, o);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var rows = new List<OneSideRow>();
            long total = 0;
            while (r.Read())
            {
                long d = r.GetInt64(5);
                rows.Add(new OneSideRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    DisplayText(r.GetString(3), sanitize), r.GetInt64(4), d,
                    perHour ? d * UsPerHour / spanUs : null));
                total = r.GetInt64(6);
            }
            return new OneSideList(rows, total);
        }

        return (List("target", "base", cov.Target.ActiveSpanUs), List("base", "target", cov.Base.ActiveSpanUs));
    }
```

Check before trusting it: `count(*) OVER ()` is evaluated before `LIMIT` in DuckDB, so it counts every row the `WHERE` kept. The `Appeared_and_disappeared...` test asserts `disappeared.Total == 2` with `Limit = 1`, which fails if that is wrong. A query with `$db` inside both CTEs binds one parameter named `db` used twice; if DuckDB.NET rejects the reuse, stop and report it.

- [ ] Step 4: Run. Expected: 21 passed, 0 failed.

- [ ] Step 5: Break it: in `Load`, replace `UsPerHour / cov.Base.ActiveSpanUs` with `UsPerHour / cov.Target.ActiveSpanUs`. Rerun.
Expected: exactly 1 failed (`Ten_times_more_frequent...`), 20 passed. If it still passes, the test does not pin the denominator: report it. Restore; 21 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): load per hour of capture and one-sided statements" -m "A statement can cost the server more because it runs more often at the same speed, which a per-execution ranking never shows. Per-hour figures divide by the active span, and the one-sided lists carry their full count so a truncated list reads as truncated."
```

---

### Task 5: Cost per execution (§7)

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Execs`, `AddDb`, `DisplayText`.
- Produces: `internal (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) Cost(DuckDBConnection conn, CompareOptions o, bool sanitize)`; test seam `public (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) CostOnly(CompareOptions o)`.

- [ ] Step 1: Failing tests.

```csharp
    [Fact]
    public void Three_times_slower_ranks_first_in_regressions_and_noise_does_not_rank()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("slow", 5, 10_000, T0).Concat(Burst("tiny", 5, 100, T0)).Concat(Burst("rare", 2, 10_000, T0)).Concat(Burst("fast", 5, 30_000, T0)));
        b.Import(Burst("slow", 5, 30_000, T0).Concat(Burst("tiny", 5, 900, T0)).Concat(Burst("rare", 2, 90_000, T0)).Concat(Burst("fast", 5, 10_000, T0)));
        var (reg, gains) = new ProjectComparison(a.DbPath, b.DbPath).CostOnly(Opt);
        var top = Assert.Single(reg);
        Assert.Equal("slow", top.NormalizedHash);
        Assert.Equal(3.0, top.Ratio!.Value, 3);
        Assert.Equal("fast", Assert.Single(gains).NormalizedHash);
    }

    [Fact]
    public void A_zero_base_average_gives_a_null_ratio_ranked_first()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("zero", 5, 0, T0).Concat(Burst("slow", 5, 10_000, T0)));
        b.Import(Burst("zero", 5, 5_000, T0).Concat(Burst("slow", 5, 30_000, T0)));
        var (reg, _) = new ProjectComparison(a.DbPath, b.DbPath).CostOnly(Opt);
        Assert.Equal("zero", reg[0].NormalizedHash);
        Assert.Null(reg[0].Ratio);
        Assert.Equal("slow", reg[1].NormalizedHash);
    }
```

In the first test, `tiny` is excluded by `MinAvgDurationUs` (both averages under 1 ms) and `rare` by `MinExecutions` (2 executions per side).

- [ ] Step 2: Run, expect a build error.

- [ ] Step 3: Implement.

```csharp
    public (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) CostOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        return Cost(conn, o, MustSanitize(Coverage(conn, o)));
    }

    internal (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) Cost(
        DuckDBConnection conn, CompareOptions o, bool sanitize)
    {
        string Agg(string alias) => $"""
            SELECT normalized_hash h, count(*) n, avg(duration_us) a,
                   quantile_cont(duration_us, 0.95) p, avg(cpu_time_us) c, avg(logical_reads) r
            FROM {Execs(alias, o)} GROUP BY 1
            """;

        List<CostRow> Read(string filter, string order)
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"""
                WITH b AS ({Agg("base")}), t AS ({Agg("target")}),
                     j AS (SELECT b.h, b.n bn, t.n tn, b.a ba, t.a ta, b.p bp, t.p tp, b.c bc, t.c tc, b.r br, t.r tr,
                                  CASE WHEN b.a = 0 THEN NULL ELSE t.a / b.a END AS ratio
                           FROM b JOIN t ON b.h = t.h
                           WHERE b.n >= $minExec AND t.n >= $minExec AND greatest(b.a, t.a) >= $minAvg)
                SELECT j.h, q.statement_kind, q.primary_table, q.normalized_sql,
                       bn, tn, ba, ta, bp, tp, bc, tc, br, tr, ratio
                FROM j JOIN target.normalized_queries q ON q.normalized_hash = j.h
                WHERE {filter}
                ORDER BY {order}, j.h
                LIMIT $lim
                """;
            AddDb(c, o);
            Add(c, "$minExec", o.Thresholds.MinExecutions);
            Add(c, "$minAvg", o.Thresholds.MinAvgDurationUs);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var list = new List<CostRow>();
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            while (r.Read())
                list.Add(new CostRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    DisplayText(r.GetString(3), sanitize), r.GetInt64(4), r.GetInt64(5),
                    r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9),
                    D(10), D(11), D(12), D(13), D(14)));
            return list;
        }

        // Constantes de ce fichier. Une moyenne de base nulle donne un ratio NULL, classe en tete.
        return (Read("(ratio > 1 OR ratio IS NULL)", "(ratio IS NULL) DESC, ratio DESC, ta DESC"),
                Read("ratio < 1", "ratio ASC"));
    }
```

- [ ] Step 4: Run. Expected: 23 passed, 0 failed.

- [ ] Step 5: Break it: replace `CASE WHEN b.a = 0 THEN NULL ELSE t.a / b.a END` with `t.a / b.a`. Rerun.
Expected: exactly 1 failed (`A_zero_base_average...`), 22 passed. Restore; 23 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): cost per execution, regressions and gains" -m "The ratio of averages is stable at low counts, and the two floors keep a microsecond statement or a two-execution sample out of the ranking. A zero base average would be infinite and unserializable, so it is NULL and ranked first."
```

---

### Task 6: Plans (§10)

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Execs`, `AddDb`, `CompareCoverage.Base/Target.EligiblePlanProfiles`, `CompareFixture.Plan(...)`, `CompareFixture.Plans(...)`.
- Produces: `internal PlanSection PlanChanges(DuckDBConnection conn, CompareOptions o, CompareCoverage cov)`; test seam `public PlanSection PlansOnly(CompareOptions o)`.

- [ ] Step 1: Failing tests. `0A1B2C3D4E5F6071` has a leading zero; its decimal form is `728224406569967729`.

```csharp
    [Fact]
    public void A_changed_plan_and_an_appeared_finding_are_listed_and_linked_through_a_leading_zero_hash()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var ra = a.Import([new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc", 5_000, T0, QueryHash: "728224406569967729")]);
        var rb = b.Import([new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc", 5_000, T0, QueryHash: "728224406569967729")]);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));
        b.Plans(rb, CompareFixture.Plan("0A1B2C3D4E5F6071", "P2", 9_000, "queryplanhash",
            new SqlFerret.Core.Plans.PlanFinding("spill_to_tempdb", 3, "{}")));
        var s = new ProjectComparison(a.DbPath, b.DbPath).PlansOnly(Opt);
        Assert.False(s.Skipped);
        var row = Assert.Single(s.Rows);
        Assert.True(row.PlanChanged);
        Assert.Equal(["spill_to_tempdb"], row.AppearedKinds);
        Assert.Equal("h1", row.LinkedNormalizedHash);
    }

    [Fact]
    public void Multi_statement_plans_are_excluded_and_a_side_without_eligible_plans_skips()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var ra = a.Import(Burst("h1", 1, 5_000, T0));
        var rb = b.Import(Burst("h1", 1, 5_000, T0));
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "M1", 1_000, "multi"));
        b.Plans(rb, CompareFixture.Plan("0A1B2C3D4E5F6071", "M2", 1_000, "multi"));
        var s = new ProjectComparison(a.DbPath, b.DbPath).PlansOnly(Opt);
        Assert.True(s.Skipped);
        Assert.Empty(s.Rows);
    }

    [Fact]
    public void With_a_database_filter_only_linked_plans_are_kept_and_the_rest_counted()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var ra = a.Import([new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc", 5_000, T0, QueryHash: "728224406569967729")]);
        var rb = b.Import([new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc", 5_000, T0, QueryHash: "728224406569967729")]);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000), CompareFixture.Plan("00000000000000AA", "Q1", 1_000));
        b.Plans(rb, CompareFixture.Plan("0A1B2C3D4E5F6071", "P2", 1_000), CompareFixture.Plan("00000000000000AA", "Q2", 1_000));
        var s = new ProjectComparison(a.DbPath, b.DbPath).PlansOnly(Opt with { Database = "AppDb" });
        Assert.Equal("0A1B2C3D4E5F6071", Assert.Single(s.Rows).QueryHash);
        Assert.Equal(2L, s.UnlinkedExcluded);
    }
```

- [ ] Step 2: Run, expect a build error.

- [ ] Step 3: Implement.

```csharp
    public PlanSection PlansOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        return PlanChanges(conn, o, Coverage(conn, o));
    }

    /// <summary>Hash d'execution (decimal UInt64 en general) vers le hex nu, 16 chiffres, des plans.
    /// hex() perd les zeros de tete ; TRY_CAST parce qu'un cast qui echoue avorte la requete.</summary>
    private const string ExecHashHex = "printf('%016X', TRY_CAST(e.query_hash AS UBIGINT))";
    private const string PlanHashHex = "lpad(upper(p.query_hash), 16, '0')";

    internal PlanSection PlanChanges(DuckDBConnection conn, CompareOptions o, CompareCoverage cov)
    {
        if (cov.Base.EligiblePlanProfiles == 0 || cov.Target.EligiblePlanProfiles == 0)
            return new PlanSection(true, "a side has no single-statement plan profile", [], 0, 0);

        string Eligible(string alias) => $"""
            SELECT {PlanHashHex} qh, p.plan_hash, p.plan_profile_id, p.duration_us
            FROM {alias}.plan_profiles p
            WHERE p.plan_hash_source = 'queryplanhash' AND p.query_hash IS NOT NULL
            """;
        // Avec --database, un plan n'est garde que s'il se rattache a une execution de cette base.
        string Kept(string alias) => o.Database is null ? Eligible(alias) : $"""
            SELECT * FROM ({Eligible(alias)}) WHERE qh IN
              (SELECT {ExecHashHex} FROM {Execs(alias, o)} e WHERE e.query_hash IS NOT NULL)
            """;
        string Per(string alias) => $"""
            SELECT k.qh,
                   array_to_string(list_sort(list_distinct(list(k.plan_hash))), ',') plans,
                   median(k.duration_us) med,
                   array_to_string(list_sort(list_distinct(list(f.kind) FILTER (WHERE f.kind IS NOT NULL))), ',') kinds
            FROM ({Kept(alias)}) k LEFT JOIN {alias}.plan_findings f ON f.plan_profile_id = k.plan_profile_id
            GROUP BY k.qh
            """;

        using var c = conn.CreateCommand();
        c.CommandText = $"""
            WITH b AS ({Per("base")}), t AS ({Per("target")}),
                 link AS (SELECT {ExecHashHex} qh, any_value(e.normalized_hash) nh
                          FROM {Execs("target", o)} e WHERE e.query_hash IS NOT NULL GROUP BY 1)
            SELECT b.qh, b.plans, t.plans, b.kinds, t.kinds, b.med, t.med, link.nh,
                   count(*) OVER () AS total,
                   (SELECT count(*) FROM ({Eligible("base")})) + (SELECT count(*) FROM ({Eligible("target")}))
                   - (SELECT count(*) FROM ({Kept("base")})) - (SELECT count(*) FROM ({Kept("target")})) AS unlinked
            FROM b JOIN t ON b.qh = t.qh
            LEFT JOIN link ON link.qh = b.qh
            WHERE b.plans <> t.plans OR coalesce(b.kinds, '') <> coalesce(t.kinds, '')
            ORDER BY t.med DESC NULLS LAST, b.qh
            LIMIT $lim
            """;
        AddDb(c, o);
        Add(c, "$lim", o.Limit);

        static IReadOnlyList<string> Split(string? s) => string.IsNullOrEmpty(s) ? [] : s.Split(',');
        var rows = new List<PlanChangeRow>();
        long total = 0, unlinked = 0;
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                var bk = Split(r.IsDBNull(3) ? null : r.GetString(3));
                var tk = Split(r.IsDBNull(4) ? null : r.GetString(4));
                rows.Add(new PlanChangeRow(r.GetString(0), Split(r.GetString(1)), Split(r.GetString(2)),
                    r.GetString(1) != r.GetString(2),
                    tk.Except(bk).ToList(), bk.Except(tk).ToList(),
                    r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                    r.IsDBNull(7) ? null : r.GetString(7)));
                total = r.GetInt64(8);
                unlinked = r.GetInt64(9);
            }
        return new PlanSection(false, null, rows, total, unlinked);
    }
```

The `unlinked` count is computed inside the main query, so it reads 0 when no row changed. That is acceptable for a count displayed next to an empty list, and the third test has a changed row. If you find you need it independently of the rows, compute it in a second scalar query rather than changing the semantics silently, and say so in the task report.

`list(...) FILTER (WHERE ...)` and `median` over `BIGINT` are DuckDB features the plan has not run on 1.5.3: run the tests, and if either is rejected, stop and report the exact error.

- [ ] Step 4: Run. Expected: 26 passed, 0 failed.

- [ ] Step 5: Break it: change `ExecHashHex` to `"upper(hex(TRY_CAST(e.query_hash AS UBIGINT)))"`. Rerun.
Expected: exactly 2 failed (`A_changed_plan...` on `LinkedNormalizedHash`, `With_a_database_filter...`), 24 passed. Restore; 26 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): plan and finding changes per query hash" -m "Plans are keyed on the query hash of the plan XML, single-statement only, because a multi-statement plan would report a change in its second statement against its first. The link to executions pads to sixteen hex digits; hex alone drops the leading zeros of one hash in sixteen."
```

---

### Task 7: `Run`, the envelope, and JSON

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: every section method of Tasks 2 to 6.
- Produces: `public CompareDigestResult Run(CompareOptions options)`.

- [ ] Step 1: Failing tests.

```csharp
    [Fact]
    public void A_project_compared_with_itself_reports_no_change()
    {
        using var a = new CompareFixture();
        var run = a.Import(Burst("h1", 6, 10_000, T0, 600).Concat(Burst("h2", 6, 20_000, T0, 600)));
        a.Plans(run, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));
        var d = new ProjectComparison(a.DbPath, a.DbPath).Run(Opt);
        Assert.Empty(d.Regressions); Assert.Empty(d.Gains);
        Assert.True(d.LoadComputed);
        Assert.Empty(d.LoadIncreases); Assert.Empty(d.LoadDecreases);
        Assert.Equal(0L, d.Appeared.Total); Assert.Equal(0L, d.Disappeared.Total);
        Assert.Empty(d.Plans.Rows);
    }

    [Fact]
    public void A_raw_then_literals_project_never_prints_the_value_in_any_section_or_format()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        const string leaky = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";
        var execs = Enumerable.Range(0, 6).Select(i => new CompareFixture.Exec("h1", leaky, 10_000 * (i + 1), T0.AddMinutes(i * 10))).ToList();
        a.Import(execs.Select(e => e with { Sql = "select * from AppSchema.WidgetRecalc where Code = ?" }), SqlTextSanitization.Literals);
        b.Import(execs);                                                   // raw first: this text is kept
        b.Import(execs.Select(e => e with { At = e.At.AddDays(1), DurationUs = e.DurationUs * 3 }), SqlTextSanitization.Literals);
        b.Import([new CompareFixture.Exec("h9", leaky, 50_000, T0)]);     // one-sided, raw
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(Opt);
        var json = System.Text.Json.JsonSerializer.Serialize(new CompareDigestEnvelope(ProjectComparison.SchemaVersion, DateTime.UtcNow, d));
        Assert.DoesNotContain("GADGET-7781", json);
        Assert.NotEmpty(d.Regressions);
        Assert.Equal(1L, d.Appeared.Total);
    }

    [Fact]
    public void A_null_ratio_serializes_to_json()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("zero", 5, 0, T0));
        b.Import(Burst("zero", 5, 5_000, T0));
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(Opt);
        var json = System.Text.Json.JsonSerializer.Serialize(new CompareDigestEnvelope(1, DateTime.UtcNow, d));
        Assert.Contains("\"Ratio\":null", json);
    }
```

The markdown half of the leak test is in Task 8, where the renderer exists.

- [ ] Step 2: Run, expect a build error (`Run` missing).

- [ ] Step 3: Implement.

```csharp
    public CompareDigestResult Run(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
        var cov = Coverage(conn, options);
        bool sanitize = MustSanitize(cov);
        var (reg, gains) = Cost(conn, options, sanitize);
        var load = Load(conn, options, cov, sanitize);
        var (appeared, disappeared) = OneSided(conn, options, cov, sanitize);
        var plans = PlanChanges(conn, options, cov);
        return new CompareDigestResult(cov, reg, gains,
            load is not null, load?.Up ?? [], load?.Down ?? [],
            appeared, disappeared, plans);
    }
```

- [ ] Step 4: Run. Expected: 29 passed, 0 failed.

- [ ] Step 5: Break it: in `Run`, pass `false` instead of `sanitize` to `OneSided`. Rerun.
Expected: exactly 1 failed (`A_raw_then_literals...`), 28 passed. Restore; 29 passed.

- [ ] Step 6: Run the whole suite once: `dotnet test 2>&1 | tail -5`. Expected: 709 tests (680 + 29), the same skips as before (up to 11), 0 failed.

- [ ] Step 7: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): assemble the digest" -m "One connection serves every section, so both projects are attached once and checked once. The end-to-end test builds the raw-then-literals project the review found leaking and checks the serialized result, not a section in isolation."
```

---

### Task 8: CLI command and markdown

Files:
- Create: `src/SqlFerret.Cli/CompareDigestMarkdown.cs`
- Modify: `src/SqlFerret.Cli/Program.cs` (usage line at line 50; new `case "compare":` next to `case "export-health":`)
- Create: `tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs`
- Create: `tests/SqlFerret.Core.Tests/CliCompareTests.cs`

Interfaces:
- Consumes: `ProjectComparison.Run`, `CompareDigestEnvelope`, `CompareRefusedException`, `SqlFerretConfig.Load(string? jsonPath)` and its `DurationUnit`, `DisplayFormat.Duration(long microseconds, string unit)`, `MarkdownText.Safe(string?)`, `BlockingDigestMarkdown.HasTraversal(string)`.
- Produces: `public static string CompareDigestMarkdown.Render(CompareDigestEnvelope e, string durationUnit)`.

- [ ] Step 1: Failing tests for the renderer.

```csharp
// tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Normalization;
using Xunit;

public class CompareDigestMarkdownTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Markdown_has_every_section_in_order_and_says_when_one_is_empty()
    {
        using var a = new CompareFixture();
        a.Import(Enumerable.Range(0, 3).Select(i => new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc", 5_000, T0.AddMinutes(i))));
        var d = new ProjectComparison(a.DbPath, a.DbPath).Run(new CompareOptions(10, null, new CompareThresholds()));
        var md = CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms");
        string[] order = ["## Coverage", "## Cost per execution", "## Load per hour", "## Appeared and disappeared", "## Plans"];
        var at = order.Select(h => md.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0));
        Assert.Equal(at.OrderBy(i => i), at);
        Assert.Contains("No regression above the thresholds.", md);
        Assert.Contains("not computed", md);
    }

    [Fact]
    public void Markdown_of_a_raw_then_literals_project_does_not_carry_the_value()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        const string leaky = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";
        var execs = Enumerable.Range(0, 6).Select(i => new CompareFixture.Exec("h1", leaky, 10_000 * (i + 1), T0.AddMinutes(i * 10))).ToList();
        a.Import(execs.Select(e => e with { Sql = "select * from AppSchema.WidgetRecalc where Code = ?" }), SqlTextSanitization.Literals);
        b.Import(execs);
        b.Import(execs.Select(e => e with { At = e.At.AddDays(1), DurationUs = e.DurationUs * 3 }), SqlTextSanitization.Literals);
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(new CompareOptions(10, null, new CompareThresholds()));
        Assert.DoesNotContain("GADGET-7781", CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms"));
    }
}
```

- [ ] Step 2: Failing tests for the CLI. They follow `CliExportHealthTests` (same `CliPath` and `Run` helpers, copied, since that file keeps them private).

```csharp
// tests/SqlFerret.Core.Tests/CliCompareTests.cs
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

public class CliCompareTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static string CliPath() =>
        Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "SqlFerret.Cli.exe" : "SqlFerret.Cli");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(CliPath()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static CompareFixture Project()
    {
        var f = new CompareFixture();
        f.Import(Enumerable.Range(0, 3).Select(i => new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc", 5_000, T0.AddMinutes(i))));
        return f;
    }

    [Fact]
    public void Compare_writes_nothing_into_either_project()
    {
        using var a = Project();
        using var b = Project();
        var before = (Snapshot(a.Dir), Snapshot(b.Dir));
        var (code, output, err) = Run("compare", "--base", a.Dir, "--target", b.Dir, "--format", "both");
        Assert.True(code == 0, err);
        Assert.Contains("## Coverage", output);
        Assert.Contains("schemaVersion", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Item1, Snapshot(a.Dir));
        Assert.Equal(before.Item2, Snapshot(b.Dir));
    }

    [Theory]
    [InlineData("--target")]
    [InlineData("--base")]
    public void A_missing_side_is_an_error(string present)
    {
        using var a = Project();
        var (code, _, err) = Run("compare", present, a.Dir);
        Assert.Equal(1, code);
        Assert.Contains("--base and --target", err);
    }

    [Theory]
    [InlineData("--format", "xml")]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "x")]
    [InlineData("--out", "../escape.md")]
    public void Bad_options_are_refused(string flag, string value)
    {
        using var a = Project();
        var (code, _, _) = Run("compare", "--base", a.Dir, "--target", a.Dir, flag, value);
        Assert.Equal(1, code);
    }

    [Fact]
    public void A_refusal_exits_one_with_its_message()
    {
        using var a = Project();
        var missing = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}");
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", missing);
        Assert.Equal(1, code);
        Assert.Contains("no project database", err);
        Assert.False(Directory.Exists(missing));
    }
}
```

That is 2 renderer tests and 1 + 2 + 4 + 1 = 8 CLI test cases: 10 in total.

- [ ] Step 3: Run, expect a build error (`CompareDigestMarkdown` missing).

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~CompareDigestMarkdownTests|FullyQualifiedName~CliCompareTests" 2>&1 | tail -5`

- [ ] Step 4: Write the renderer.

```csharp
// src/SqlFerret.Cli/CompareDigestMarkdown.cs
using System.Globalization;
using System.Text;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;

namespace SqlFerret.Cli;

/// <summary>Rendu Markdown de compare, dans l'hote : Core ne rend que des microsecondes.</summary>
public static class CompareDigestMarkdown
{
    private static string Safe(string? v) => MarkdownText.Safe(v);
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Render(CompareDigestEnvelope e, string durationUnit)
    {
        string Dur(double us) => DisplayFormat.Duration((long)us, durationUnit);
        var d = e.Digest;
        var sb = new StringBuilder();
        sb.AppendLine("# Project comparison").AppendLine();

        sb.AppendLine("## Coverage").AppendLine();
        sb.AppendLine("| | Base | Target |").AppendLine("|---|---|---|");
        var (b, t) = (d.Coverage.Base, d.Coverage.Target);
        sb.AppendLine($"| Project | {Safe(b.ProjectDir)} | {Safe(t.ProjectDir)} |");
        sb.AppendLine($"| Runs | {b.Runs.Count} | {t.Runs.Count} |");
        sb.AppendLine($"| Active span | {Dur(b.ActiveSpanUs)} | {Dur(t.ActiveSpanUs)} |");
        sb.AppendLine($"| Executions | {b.Executions} | {t.Executions} |");
        sb.AppendLine($"| Distinct statements | {b.DistinctStatements} | {t.DistinctStatements} |");
        sb.AppendLine($"| Databases | {Safe(string.Join(", ", b.Databases))}{(b.OtherDatabases > 0 ? $" (+{b.OtherDatabases})" : "")} | {Safe(string.Join(", ", t.Databases))}{(t.OtherDatabases > 0 ? $" (+{t.OtherDatabases})" : "")} |");
        sb.AppendLine($"| Normalizer versions | {string.Join(", ", b.NormalizerVersions)} | {string.Join(", ", t.NormalizerVersions)} |");
        sb.AppendLine($"| Redaction | {string.Join(", ", b.RedactionPolicies)} | {string.Join(", ", t.RedactionPolicies)} |");
        sb.AppendLine($"| SQL text | {string.Join(", ", b.SqlTextPolicies)} | {string.Join(", ", t.SqlTextPolicies)} |");
        sb.AppendLine($"| Smallest duration | {(b.MinDurationUs is { } bm ? Dur(bm) : "-")} | {(t.MinDurationUs is { } tm ? Dur(tm) : "-")} |");
        sb.AppendLine($"| Executions with query_hash | {N(b.QueryHashShare * 100)} % | {N(t.QueryHashShare * 100)} % |");
        sb.AppendLine($"| Plan profiles used / left out | {b.EligiblePlanProfiles} / {b.ExcludedPlanProfiles} | {t.EligiblePlanProfiles} / {t.ExcludedPlanProfiles} |");
        sb.AppendLine();
        foreach (var n in d.Coverage.Notes) sb.AppendLine($"- {Safe(n)}");
        sb.AppendLine();

        sb.AppendLine("## Cost per execution").AppendLine();
        CostTable(sb, "Regressions", d.Regressions, "No regression above the thresholds.", Dur);
        CostTable(sb, "Gains", d.Gains, "No gain above the thresholds.", Dur);

        sb.AppendLine("## Load per hour").AppendLine();
        if (!d.LoadComputed) sb.AppendLine("Per-hour figures not computed: an active span is under the threshold.").AppendLine();
        else
        {
            LoadTable(sb, "Increases", d.LoadIncreases, "No load increase.", Dur);
            LoadTable(sb, "Decreases", d.LoadDecreases, "No load decrease.", Dur);
        }

        sb.AppendLine("## Appeared and disappeared").AppendLine();
        OneSide(sb, "Appeared (target only)", d.Appeared, Dur);
        OneSide(sb, "Disappeared (base only)", d.Disappeared, Dur);

        sb.AppendLine("## Plans").AppendLine();
        if (d.Plans.Skipped) sb.AppendLine($"Skipped: {Safe(d.Plans.SkipReason)}.").AppendLine();
        else if (d.Plans.Rows.Count == 0) sb.AppendLine("No plan or finding change among single-statement plans.").AppendLine();
        else
        {
            sb.AppendLine($"{d.Plans.Rows.Count} of {d.Plans.Total} shown. Single-statement plans only.").AppendLine();
            sb.AppendLine("| Query hash | Plan changed | Findings appeared | Findings gone | Median base | Median target | Statement |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in d.Plans.Rows)
                sb.AppendLine($"| {Safe(r.QueryHash)} | {(r.PlanChanged ? "yes" : "no")} | {Safe(string.Join(", ", r.AppearedKinds))} | {Safe(string.Join(", ", r.DisappearedKinds))} | {(r.BaseMedianUs is { } x ? Dur(x) : "-")} | {(r.TargetMedianUs is { } y ? Dur(y) : "-")} | {Safe(r.LinkedNormalizedHash ?? "-")} |");
            sb.AppendLine();
        }
        if (d.Plans.UnlinkedExcluded > 0)
            sb.AppendLine($"{d.Plans.UnlinkedExcluded} plan profiles left out: not linked to an execution of the filtered database.").AppendLine();
        return sb.ToString();
    }

    private static void CostTable(StringBuilder sb, string title, IReadOnlyList<CostRow> rows, string empty, Func<double, string> dur)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (rows.Count == 0) { sb.AppendLine(empty).AppendLine(); return; }
        sb.AppendLine("| Ratio | Avg base | Avg target | p95 base | p95 target | Count base/target | Statement |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {(r.Ratio is { } x ? N(x) : "new cost")} | {dur(r.BaseAvgUs)} | {dur(r.TargetAvgUs)} | {dur(r.BaseP95Us)} | {dur(r.TargetP95Us)} | {r.BaseCount}/{r.TargetCount} | `{Safe(r.NormalizedSql)}` |");
        sb.AppendLine();
    }

    private static void LoadTable(StringBuilder sb, string title, IReadOnlyList<LoadRow> rows, string empty, Func<double, string> dur)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (rows.Count == 0) { sb.AppendLine(empty).AppendLine(); return; }
        sb.AppendLine("| Delta per hour | Base per hour | Target per hour | Executions/h base | Executions/h target | Statement |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {dur(r.DeltaUsPerHour)} | {dur(r.BaseUsPerHour)} | {dur(r.TargetUsPerHour)} | {N(r.BaseExecPerHour)} | {N(r.TargetExecPerHour)} | `{Safe(r.NormalizedSql)}` |");
        sb.AppendLine();
    }

    private static void OneSide(StringBuilder sb, string title, OneSideList list, Func<double, string> dur)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (list.Total == 0) { sb.AppendLine("None.").AppendLine(); return; }
        sb.AppendLine($"{list.Rows.Count} of {list.Total} shown.").AppendLine();
        sb.AppendLine("| Executions | Total duration | Per hour | Statement |").AppendLine("|---|---|---|---|");
        foreach (var r in list.Rows)
            sb.AppendLine($"| {r.Executions} | {dur(r.TotalDurationUs)} | {(r.UsPerHour is { } h ? dur(h) : "-")} | `{Safe(r.NormalizedSql)}` |");
        sb.AppendLine();
    }
}
```

`DisplayFormat.Duration` takes a `long`; a negative delta passes through it unchanged (it divides, it does not take an absolute value). If you find it renders a negative value badly, say so in the report rather than changing `DisplayFormat`, which other commands use.

- [ ] Step 5: Add the CLI case in `src/SqlFerret.Cli/Program.cs`, right after the `export-health` case. It does not call `OpenProject()`.

```csharp
    case "compare":
        {
            var baseDir = Arg("--base"); var targetDir = Arg("--target");
            if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(targetDir))
            { Console.Error.WriteLine("compare: --base and --target <dir> are required"); return 1; }

            var format = Arg("--format", "md");
            if (format is not ("json" or "md" or "both"))
            { Console.Error.WriteLine("compare: --format must be json, md or both"); return 1; }

            var outPath = Arg("--out", "");
            if (outPath.Length > 0 && SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outPath))
            { Console.Error.WriteLine("compare: invalid --out path"); return 1; }

            if (!int.TryParse(Arg("--limit", "10"), out var limit) || limit <= 0)
            { Console.Error.WriteLine("compare: --limit must be a positive integer"); return 1; }

            // Pas d'OpenProject : il ecrit project.json et cree README, plans/ et exports/ (spec §3).
            var basePath = Path.Combine(Path.GetFullPath(baseDir), "sqlferret.duckdb");
            var targetPath = Path.Combine(Path.GetFullPath(targetDir), "sqlferret.duckdb");
            string unit;
            try { unit = SqlFerretConfig.Load(Path.Combine(Path.GetFullPath(baseDir), "sqlferret.config.json")).DurationUnit; }
            catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"compare: {ex.Message}"); return 1; }

            CompareDigestEnvelope envelope;
            try
            {
                var digest = new ProjectComparison(basePath, targetPath)
                    .Run(new CompareOptions(limit, Arg("--database", "") is { Length: > 0 } db ? db : null, new CompareThresholds()));
                envelope = new CompareDigestEnvelope(ProjectComparison.SchemaVersion, DateTime.UtcNow, digest);
            }
            catch (CompareRefusedException ex) { Console.Error.WriteLine(ex.Message); return 1; }

            var json = System.Text.Json.JsonSerializer.Serialize(envelope,
                           new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var md = SqlFerret.Cli.CompareDigestMarkdown.Render(envelope, unit);

            if (outPath.Length == 0)
                Console.WriteLine(format switch { "json" => json, "md" => md, _ => md + Environment.NewLine + json });
            else if (format == "both")
            {
                var stem = Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath));
                File.WriteAllText(stem + ".md", md);
                File.WriteAllText(stem + ".json", json);
                Console.WriteLine($"written: {stem}.md, {stem}.json");
            }
            else
            {
                File.WriteAllText(outPath, format == "json" ? json : md);
                Console.WriteLine($"written: {outPath}");
            }
            return 0;
        }
```

Then append ` | compare --base <dir> --target <dir> [--database <name>] [--format json|md|both] [--out <file>] [--limit <n>]` to the usage string on line 50, before its closing quote.

- [ ] Step 6: Run.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~CompareDigestMarkdownTests|FullyQualifiedName~CliCompareTests" 2>&1 | tail -5`
Expected: 10 passed, 0 failed.

- [ ] Step 7: Break it: insert `AuditProject.OpenOrCreate(baseDir, Directory.GetCurrentDirectory());` as the first statement after the `--limit` check of the `compare` case. Rerun.
Expected: exactly 1 failed (`Compare_writes_nothing_into_either_project`), 9 passed. Remove the inserted line; 10 passed.

- [ ] Step 8: Verify the lock message by hand once, outside the test suite (CI has no DuckDB CLI). In one shell invocation:

```bash
cd <worktree> && F=$(mktemp -d) && mkdir $F/p && \
  ( (echo "SELECT 1;"; sleep 8) | duckdb $F/p/sqlferret.duckdb >/dev/null 2>&1 & ); sleep 2; \
  dotnet run --project src/SqlFerret.Cli -- compare --base $F/p --target $F/p; echo "exit $?"; sleep 8; rm -rf $F
```

The background `duckdb` process creates the file and holds its lock for eight seconds. Expected: `compare: ... is open in another SQLFerret process ...` and `exit 1`, no stack trace. The attach runs before the schema check, so the lock message wins over the missing-tables one.

- [ ] Step 9: Commit.

```bash
dotnet format --include src/SqlFerret.Cli/CompareDigestMarkdown.cs src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs tests/SqlFerret.Core.Tests/CliCompareTests.cs
git add src/SqlFerret.Cli/CompareDigestMarkdown.cs src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs tests/SqlFerret.Core.Tests/CliCompareTests.cs
git commit -m "feat(cli): sqlferret compare" -m "The command reads the display unit from the base project's config file and nothing else, because the project opener every other command uses writes provenance and creates directories. The CLI test snapshots both project directories, file by file, to hold that."
```

---

### Task 9: Documentation

Files:
- Modify: `docs/cli-reference.md` (new `## compare` section after `## export-health`; the command list in the file's introduction if it enumerates commands)
- Modify: `docs/README.md` (the `cli-reference.md` row of the full index lists the commands)
- Modify: `CLAUDE.md` ("CLI commands, nine of them" becomes ten, with `compare` added to the list)
- Modify: `docs/development.md` only if it enumerates commands; check with `grep -n "export-health" docs/development.md`.

- [ ] Step 1: Write the `docs/cli-reference.md` section, in the style of `## export-health`: synopsis, flag table (copy §3 of the spec), then three short paragraphs: what the coverage block says and why it comes first; the refusals and their remedies (re-import for a fingerprint generation mismatch, any command once for an old schema, close the TUI for a lock); and the privacy rule (text re-normalized unless every run on both sides is `raw`, plan statement text never printed). Every claim must be one the code of Tasks 1 to 8 implements; do not describe SQL Server behavior.

- [ ] Step 2: Update `CLAUDE.md` and `docs/README.md` as listed.

- [ ] Step 3: Full validation, in one invocation:

```bash
cd <worktree> && dotnet format --verify-no-changes && dotnet build -warnaserror 2>&1 | tail -3 && dotnet test 2>&1 | tail -5
```

Expected: format clean, 0 warnings, 719 tests (680 + 29 + 10), up to 11 skipped, 0 failed. In `CLAUDE.md`, update "680 tests" to the number actually observed.

- [ ] Step 4: `git status --porcelain -uall` shows only the files of this task; nothing under `sample/`, no `.duckdb`, no `/tmp` path.

- [ ] Step 5: Commit.

```bash
git add docs/cli-reference.md docs/README.md CLAUDE.md
git commit -m "docs: sqlferret compare" -m "The reference states what the coverage block is for, what each refusal asks the user to do, and why statement text can look more collapsed than in either project."
```
