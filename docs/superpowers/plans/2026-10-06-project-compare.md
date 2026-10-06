# `sqlferret compare` Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

Revision 2 of this plan, after a review panel of five readers who executed revision 1 verbatim. What changed: the printed-text rule follows revision 3 of the spec (no re-parsing; trust a text only when the run that wrote it was `literals`); the fixture stores text through `SqlTextSanitizer.Apply` as ingestion does; `InternalsVisibleTo` is added for the tests; `plans` was a DuckDB keyword, so every alias now carries `AS`; the plan median is computed before the findings join; the unlinked count has its own query; executions without a duration are left out and counted; `--out` inside a project and a bare flag are refused; the markdown carries every coverage field and the CPU and reads columns; the spec tests revision 1 left out are added.

Goal: a `compare` CLI command that reads two existing SQLFerret projects read-only and emits a markdown or JSON digest of what changed between them: coverage, cost per execution, load per hour, statements present on one side only, and plan changes.

Architecture: one Core class, `SqlFerret.Core.Analysis.ProjectComparison`, opens an in-memory DuckDB connection, attaches both `sqlferret.duckdb` files `READ_ONLY` as `base` and `target`, checks preconditions, and runs every section as DuckDB SQL. Records live in `CompareResults.cs`. The CLI host parses flags, reads the display unit with `SqlFerretConfig.Load`, and renders markdown through `CompareDigestMarkdown`. Nothing is written into either project directory.

Tech stack: .NET 10 / C# 14, DuckDB.NET.Data.Full 1.5.3, xUnit.

Spec: `docs/superpowers/specs/2026-10-06-project-compare-design.md` (revision 3). Read it before any task; every task argues from it, and the section numbers below (§n) are its sections.

## Global Constraints

- Core stays host-agnostic and returns microseconds only; formatting through `DisplayFormat.Duration` happens in the CLI (`CLAUDE.md`, invariants).
- All aggregation is DuckDB SQL. C# only maps rows and compares small lists.
- DuckDB.NET parameter names carry no leading `$`: SQL uses `$name`, the `Add` helper does `name.TrimStart('$')`.
- In DuckDB SQL, write every column alias with `AS`: `plans` is a keyword and `expr plans` is a parse error (measured by four readers of revision 1).
- The only interpolated user text is the `ATTACH` path, single quotes doubled (`CLAUDE.md`, SQL safety, second exception). `--database` is always a bound parameter. Aliases `base` and `target` are constants.
- The CLI host never calls `AuditProject.OpenOrCreate` for `compare` (§3).
- No interface, no DI, no repository; primary constructors, records, collection expressions `[]`, raw string literals for SQL (`CLAUDE.md`, KISS and C# baseline).
- Test fixtures use only the anonymous vocabulary: `AppDb`, `AppSchema`, `WidgetRecalc`, `@WidgetId`, `@GadgetCode`, `@TenantId`, `@Code`. `GADGET-7781` is the invented value whose absence the leak tests check.
- Exit codes: `0` success, `1` any error or refusal (`docs/cli-reference.md`). No stack trace reaches the user.
- Commit messages: prose explaining why, no bold, no em dash, and no attribution trailer of any kind (no `Co-Authored-By`, no `Generated with`).
- Run `dotnet format` on the files a task touched before its commit; `dotnet build -warnaserror` must stay at 0 warnings.

## How to run tests in this plan

Every test step gives a filter and an exact expected count. Any other count, a higher one included, means the filter or the work is wrong: stop and say so. Setup and test run in one shell invocation, because shell state does not survive between calls.

```bash
cd <worktree> && dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5
```

Every task has a step that breaks the new code on purpose and watches named tests fail. Reporting "3 of 20 failed" there is the success of that step, not a failure. The counts in a break step are those of the tests existing at that task, not of the finished suite. If what you measure contradicts this plan (a count, a message, a DuckDB behavior), stop and say so; do not bend the code to fit the plan.

## Review Focus

Inputs the spec implies that a section's happy path does not exercise, most likely first. Each has its test in the owning task.

1. A project whose first import was `raw` and a later one `literals`: no printed text carries the value, and the statement is still readable from the other side when that side was `literals` (Task 3, Task 7).
2. A project with two imports a day apart: per-hour figures use the sum of run spans (Task 2, Task 4).
3. A query hash with a leading zero: the plan link joins (Task 6).
4. Executions without a duration, as `*_starting` events map: no crash, counted in coverage (Task 2, Task 7).
5. `--out` pointing inside one of the projects, directly or through a symbolic link: refused; `--out` into a directory that does not exist: exit 1 without a stack trace (Task 8).

---

### Task 1: Results records, attach, preconditions, fixture

Files:
- Modify: `src/SqlFerret.Core/SqlFerret.Core.csproj` (add `InternalsVisibleTo`)
- Create: `src/SqlFerret.Core/Analysis/CompareResults.cs`
- Create: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Create: `tests/SqlFerret.Core.Tests/CompareFixture.cs`
- Create: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `DuckDbProject.Open(string)`, `DuckDbProject.BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy, SqlTextSanitization sqlText = SqlTextSanitization.Raw)`, `DuckDbProject.InsertBatch(long runId, IReadOnlyList<PreparedRow> rows)`, `DuckDbProject.InsertPlanProfileBatch(long runId, IReadOnlyList<PreparedPlanProfile> rows)`, `QueryNormalizer.Normalize(string)`, `SqlTextSanitizer.Apply(string raw, NormalizedQuery nq, SqlTextSanitization level)` returning `(string Text, NormalizedQuery Normalized, bool Failed)`.
- Produces: every record below; `ProjectComparison(string baseDbPath, string targetDbPath)`; `public void Check(CompareOptions options)`; `CompareRefusedException`; internal `AttachSql`, `FingerprintGeneration`, `DescribeAttachFailure`, `Execs`, `DbWhere`, `AddDb`, `Add`, `Open`, `CheckPreconditions`; the test helper `CompareFixture` with `Import`, `BlockingOnly`, `Plans`, `Plan`, `Sql`.

- [ ] Step 1: Let the test assembly see `internal` members. In `src/SqlFerret.Core/SqlFerret.Core.csproj`, add after the `PackageReference` item group:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="SqlFerret.Core.Tests" />
  </ItemGroup>
```

- [ ] Step 2: Write `CompareResults.cs` in full. Later tasks fill these; they are defined once here so every task sees the same names.

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
    long ExecutionsWithoutDuration,
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

- [ ] Step 3: Write the test fixture. It stores rows the way `IngestionService` does: normalize, then `SqlTextSanitizer.Apply` at the run's policy, and insert what `Apply` returns. Only the hash is forced, so tests control it.

```csharp
// tests/SqlFerret.Core.Tests/CompareFixture.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

/// <summary>
/// Un projet de test : un dossier temporaire et son sqlferret.duckdb, rempli par les chemins
/// d'insertion reels. Le texte passe par SqlTextSanitizer.Apply comme dans IngestionService :
/// la revision 1 du plan inserait le texte brut, ce qui cachait que la regle de texte detruisait
/// les textes reels.
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

    public record Exec(string Hash, string Sql, long? DurationUs, DateTime At,
        string Db = "AppDb", string? QueryHash = null, long? CpuUs = null, long? Reads = null);

    /// <summary>Un import : un run, ses executions. Rend le run_id.</summary>
    public long Import(IEnumerable<Exec> execs, SqlTextSanitization policy = SqlTextSanitization.Raw,
        string redaction = "off")
    {
        using var p = DuckDbProject.Open(DbPath);
        var run = p.BeginRun("logs/", 1, 100, redaction, policy);
        var rows = execs.Select(e =>
        {
            var nq = QueryNormalizer.Normalize(e.Sql) with { NormalizedHash = e.Hash };
            var (text, stored, _) = SqlTextSanitizer.Apply(e.Sql, nq, policy);
            return new PreparedRow(
                new ExecutionEvent
                {
                    EventName = "rpc_completed",
                    EventClass = EventClass.RpcCall,
                    ObjectName = "AppSchema.WidgetRecalc",
                    SqlTextRaw = text,
                    DatabaseName = e.Db,
                    SessionId = 52,
                    DurationUs = e.DurationUs,
                    CpuTimeUs = e.CpuUs,
                    LogicalReads = e.Reads,
                    QueryHash = e.QueryHash,
                    CapturedAt = e.At,
                    XeFileName = "s_0.xel",
                },
                stored, []);
        }).ToList();
        p.InsertBatch(run, rows);
        return run;
    }

    /// <summary>
    /// Un run qui ne contient qu'un blocked-process report, dont l'input buffer porte l'empreinte
    /// hash. Ecrit les memes lignes que InsertBlockingBatch pour ce que compare lit :
    /// blocking_reports, blocking_processes, et la signature, premier ecrit gagnant.
    /// </summary>
    public long BlockingOnly(string hash, string sql, SqlTextSanitization policy)
    {
        using var p = DuckDbProject.Open(DbPath);
        var run = p.BeginRun("logs/", 1, 1, "off", policy);
        var nq = QueryNormalizer.Normalize(sql) with { NormalizedHash = hash };
        var stored = SqlTextSanitizer.Apply(sql, nq, policy).Normalized;
        var ts = new DateTime(2026, 1, 1, 8, 0, 0);
        void X(string text, params (string Name, object Value)[] ps)
        {
            using var c = p.Connection.CreateCommand();
            c.CommandText = text;
            foreach (var (n, v) in ps)
            {
                var prm = c.CreateParameter(); prm.ParameterName = n; prm.Value = v; c.Parameters.Add(prm);
            }
            c.ExecuteNonQuery();
        }
        X("INSERT INTO blocking_reports (report_id, run_id, captured_at) VALUES ($id, $run, $ts)",
          ("id", run * 1000), ("run", run), ("ts", ts));
        X("INSERT INTO blocking_processes (report_id, role, inputbuf_fingerprint) VALUES ($id, 'blocked', $h)",
          ("id", run * 1000), ("h", hash));
        X("""
          INSERT INTO normalized_queries (normalized_hash, normalized_sql, statement_kind, normalizer_version, first_seen_at, last_seen_at)
          VALUES ($h, $sql, 'SELECT', 4, $ts, $ts) ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at
          """, ("h", hash), ("sql", stored.NormalizedSql), ("ts", ts));
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
        StatementText = "SELECT * FROM AppSchema.WidgetRecalc WHERE WidgetId = 4242",
        StatementTextLength = 58,
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

The `BlockingOnly` column lists are the subset of `blocking_reports` and `blocking_processes` that compare reads; the other columns stay NULL. If the partial insert is rejected, stop and report it.

- [ ] Step 4: Write the failing tests for this task.

```csharp
// tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Storage;
using Xunit;

public class ProjectComparisonTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CompareOptions Opt = new(10, null, new CompareThresholds());
    private const string Exec1 = "exec AppSchema.WidgetRecalc @WidgetId = 42";

    private static IEnumerable<CompareFixture.Exec> Burst(string hash, int n, long durationUs, DateTime start, int stepSeconds = 60, string db = "AppDb") =>
        Enumerable.Range(0, n).Select(i => new CompareFixture.Exec(hash, Exec1, durationUs, start.AddSeconds(i * stepSeconds), db));

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
    public void A_side_whose_executions_all_lack_a_duration_is_accepted()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 2, 5_000, T0));
        using var b = new CompareFixture();
        b.Import([new CompareFixture.Exec("h1", "SELECT 1", null, T0)]);
        new ProjectComparison(a.DbPath, b.DbPath).Check(Opt);   // must not throw: §5 asks for an execution, not a duration
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
        // Un import ancien, puis un vrai reclassify : il remet normalized_queries a jour, pas le run.
        b.Sql("UPDATE ingestion_runs SET normalizer_version = 2");
        b.Sql("UPDATE normalized_queries SET normalizer_version = 2");
        using (var p = DuckDbProject.Open(b.DbPath)) new Reclassifier(p).Run();
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, b.DbPath).Check(Opt));
        Assert.Contains("re-import", ex.Message);
        Assert.DoesNotContain("reclassify", ex.Message);
    }

    [Fact]
    public void Two_generations_on_one_side_are_refused()
    {
        using var a = new CompareFixture();
        var old = a.Import(Burst("h1", 1, 5_000, T0));
        a.Import(Burst("h1", 1, 5_000, T0.AddHours(1)));
        a.Sql($"UPDATE ingestion_runs SET normalizer_version = 2 WHERE run_id = {old}");
        Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, a.DbPath).Check(Opt));
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

- [ ] Step 5: Run and watch the build fail.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: build error, `ProjectComparison` does not exist.

- [ ] Step 6: Write `ProjectComparison.cs`.

```csharp
// src/SqlFerret.Core/Analysis/ProjectComparison.cs
using DuckDB.NET.Data;

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
        ("normalized_queries", "normalized_hash"), ("normalized_queries", "normalized_sql"),
        ("normalized_queries", "statement_kind"), ("normalized_queries", "primary_table"),
        ("ingestion_runs", "run_id"), ("ingestion_runs", "normalizer_version"),
        ("ingestion_runs", "redaction_policy"), ("ingestion_runs", "sql_text_policy"),
        ("plan_profiles", "plan_profile_id"), ("plan_profiles", "plan_hash"),
        ("plan_profiles", "plan_hash_source"), ("plan_profiles", "query_hash"),
        ("plan_profiles", "duration_us"),
        ("plan_findings", "plan_profile_id"), ("plan_findings", "kind"),
        ("blocking_reports", "report_id"), ("blocking_reports", "run_id"),
        ("blocking_processes", "report_id"), ("blocking_processes", "inputbuf_fingerprint"),
    ];

    private static readonly (string Alias, int Side)[] Sides = [("base", 0), ("target", 1)];
    private string PathOf(int side) => side == 0 ? baseDbPath : targetDbPath;

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

    // "Could not set lock" est mesure sur Linux. Le libelle Windows n'est pas verifie : "used by
    // another process" est celui du systeme pour un partage refuse. S'il differe, le message
    // generique reste exact, sans pile, et sort en 1.
    internal static string DescribeAttachFailure(string dbPath, Exception ex) =>
        ex.Message.Contains("Could not set lock", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            ? $"compare: {dbPath} is open in another SQLFerret process (a TUI, an import); close it first"
            : $"compare: cannot attach {dbPath}: {ex.Message}";

    /// <summary>Attache les deux projets et verifie le §5 ; leve CompareRefusedException sinon.</summary>
    public void Check(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
    }

    /// <summary>Executions retenues d'un cote : une empreinte, une duree, la base filtree (§6).
    /// alias est constant, la base est liee.</summary>
    internal static string Execs(string alias, CompareOptions o) =>
        $"(SELECT * FROM {alias}.executions AS e WHERE e.normalized_hash IS NOT NULL AND e.duration_us IS NOT NULL{DbWhere(o)})";

    internal static string DbWhere(CompareOptions o) => o.Database is null ? "" : " AND e.database_name = $db";

    internal static void AddDb(System.Data.IDbCommand c, CompareOptions o)
    {
        if (o.Database is not null) Add(c, "$db", o.Database);
    }

    internal DuckDBConnection Open()
    {
        foreach (var (_, side) in Sides)
            if (!File.Exists(PathOf(side))) throw new CompareRefusedException($"compare: no project database at {PathOf(side)}");

        var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        foreach (var (alias, side) in Sides)
        {
            try { Exec(conn, AttachSql(PathOf(side), alias)); }
            catch (DuckDBException ex)
            {
                conn.Dispose();
                throw new CompareRefusedException(DescribeAttachFailure(PathOf(side), ex));
            }
        }
        return conn;
    }

    internal void CheckPreconditions(DuckDBConnection conn, CompareOptions options)
    {
        foreach (var (alias, side) in Sides)
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
                        $"compare: {PathOf(side)} predates column {need.Table}.{need.Column}; run any other command on it once (for example top-slow) to migrate it");
        }

        foreach (var (alias, side) in Sides)
        {
            using var c = conn.CreateCommand();
            // Toutes les executions, avec ou sans duree (§5) : une capture faite seulement
            // d'executions sans duree doit atteindre la couverture, qui les compte (§6).
            c.CommandText = $"SELECT count(*) FROM {alias}.executions AS e WHERE e.normalized_hash IS NOT NULL{DbWhere(options)}";
            AddDb(c, options);
            if (Convert.ToInt64(c.ExecuteScalar()) == 0)
                throw new CompareRefusedException(
                    $"compare: {PathOf(side)} has no execution{(options.Database is null ? "" : $" in database {options.Database}")}");
        }

        // Les runs qui ont stocke des executions : ce sont eux qui ont calcule les empreintes
        // comparees. Un run sans execution (system_health, blocages seuls) n'en a stocke aucune.
        var versions = new List<(string Path, int Version)>();
        foreach (var (alias, side) in Sides)
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"""
                SELECT DISTINCT coalesce(r.normalizer_version, -1) AS v
                FROM {alias}.ingestion_runs AS r
                WHERE r.run_id IN (SELECT e.run_id FROM {alias}.executions AS e)
                """;
            using var rd = c.ExecuteReader();
            while (rd.Read()) versions.Add((PathOf(side), rd.GetInt32(0)));
        }
        if (versions.Select(v => FingerprintGeneration(v.Version)).Distinct().Count() > 1 || versions.Any(v => v.Version < 0))
            throw new CompareRefusedException(
                "compare: the projects hold fingerprints from different normalizer generations ("
                + string.Join(", ", versions.Distinct().Select(v => $"{v.Path}: v{v.Version}"))
                + "); re-import the older capture, since its stored hashes cannot be recomputed in place");
    }

    private static void Exec(DuckDBConnection conn, string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    internal static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
```

- [ ] Step 7: Run the tests.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: 11 passed, 0 failed.

- [ ] Step 8: Break it on purpose. Change `FingerprintGeneration` to `=> version;` and rerun the same command.
Expected: exactly 2 failed (`Fingerprint_generations_v3_and_v4_are_comparable_and_v2_is_not`, `A_v3_side_against_a_v4_side_is_accepted`), 9 passed. That is the success of this step. Restore the code and rerun: 11 passed.

- [ ] Step 9: Commit.

```bash
dotnet format --include src/SqlFerret.Core/SqlFerret.Core.csproj src/SqlFerret.Core/Analysis/CompareResults.cs src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/CompareFixture.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/SqlFerret.Core.csproj src/SqlFerret.Core/Analysis/CompareResults.cs src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/CompareFixture.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): attach two projects read-only and refuse what cannot be compared" -m "The comparison is only worth reading when both sides hold fingerprints of one generation, and reclassify rewrites the version without the hashes, so the check reads the runs that stored executions. A project held open elsewhere or predating a migration is refused with a message that says what to do."
```

---

### Task 2: Coverage (§6)

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Open()`, `CheckPreconditions`, `Execs`, `DbWhere`, `AddDb`, `Add` from Task 1.
- Produces: `internal CompareCoverage Coverage(DuckDBConnection conn, CompareOptions options)`; test seam `public CompareCoverage CoverageOnly(CompareOptions options)`.

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
    public void Each_run_is_checked_for_a_split_against_its_own_span()
    {
        using var a = new CompareFixture();
        var steady = a.Import(Burst("h1", 6, 5_000, T0, 7200));    // every 2 h over 10 h: the largest gap, no split
        var day = T0.AddDays(1);
        var split = a.Import(Burst("h1", 2, 5_000, day).Concat(Burst("h1", 1, 5_000, day.AddMinutes(31))));   // 0, 1 min, 31 min
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(steady, cov.Base.LargestGapRunId);
        Assert.Contains(cov.Notes, n => n.Contains($"run {split} has a gap"));
        Assert.DoesNotContain(cov.Notes, n => n.Contains($"run {steady} has a gap"));
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

    [Fact]
    public void Executions_without_a_duration_are_left_out_and_counted()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 2, 5_000, T0).Append(new CompareFixture.Exec("h1", Exec1, null, T0.AddMinutes(5))));
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(2L, cov.Base.Executions);
        Assert.Equal(1L, cov.Base.ExecutionsWithoutDuration);
    }
```

- [ ] Step 2: Run and watch the build fail (`CoverageOnly` does not exist).

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`

- [ ] Step 3: Implement. Add to `ProjectComparison`:

```csharp
    public CompareCoverage CoverageOnly(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
        return Coverage(conn, options);
    }

    internal CompareCoverage Coverage(DuckDBConnection conn, CompareOptions options)
    {
        var b = Side(conn, "base", baseDbPath, options);
        var t = Side(conn, "target", targetDbPath, options);
        var notes = new List<string>();
        foreach (var (s, alias) in ((CompareSideCoverage, string)[])[(b, "base"), (t, "target")])
        {
            if (s.ActiveSpanUs < options.Thresholds.MinActiveSpanUs)
                notes.Add($"{s.ProjectDir}: active span under the threshold, per-hour figures are not computed");
            foreach (var runId in SplitRuns(conn, alias, options))
                notes.Add($"{s.ProjectDir}: run {runId} has a gap of more than a quarter of its span; it probably holds several disjoint captures, and its per-hour figures understate the load");
            if (s.ExecutionsWithoutDuration > 0)
                notes.Add($"{s.ProjectDir}: {s.ExecutionsWithoutDuration} executions without a duration are left out of every section");
            if (s.EligiblePlanProfiles == 0)
                notes.Add($"{s.ProjectDir}: no single-statement plan profile, the plan section is skipped");
        }
        notes.Add("SQLFerret does not know the predicates of the capture sessions; two captures with different duration thresholds have per-hour loads that cannot be compared");
        return new CompareCoverage(b, t, notes);
    }

    /// <summary>Les runs dont le plus grand trou depasse le quart de leur propre etendue. Chaque run
    /// se mesure contre la sienne : le run au plus grand trou absolu n'est pas forcement celui-la.</summary>
    private static List<long> SplitRuns(DuckDBConnection conn, string alias, CompareOptions o)
    {
        using var c = conn.CreateCommand();
        c.CommandText = $"""
            SELECT run_id FROM (
              SELECT run_id,
                     epoch_us(captured_at) - lag(epoch_us(captured_at)) OVER (PARTITION BY run_id ORDER BY captured_at) AS gap,
                     epoch_us(max(captured_at) OVER (PARTITION BY run_id)) - epoch_us(min(captured_at) OVER (PARTITION BY run_id)) AS span
              FROM {Execs(alias, o)} AS x) AS g
            GROUP BY run_id HAVING max(span) > 0 AND max(gap) * 4 > max(span) ORDER BY run_id
            """;
        AddDb(c, o);
        using var r = c.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private static CompareSideCoverage Side(DuckDBConnection conn, string alias, string dbPath, CompareOptions o)
    {
        var x = Execs(alias, o);
        var runs = new List<CompareRunSpan>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT run_id, min(captured_at) AS first_at, max(captured_at) AS last_at,
                       epoch_us(max(captured_at)) - epoch_us(min(captured_at)) AS span_us, count(*) AS n
                FROM {x} AS x GROUP BY run_id ORDER BY run_id
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
                  FROM {x} AS x) AS g
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
                SELECT count(*) AS n, count(DISTINCT normalized_hash) AS d, min(duration_us) AS m,
                       avg(CASE WHEN query_hash IS NULL THEN 0.0 ELSE 1.0 END) AS s
                FROM {x} AS x
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            r.Read();
            execs = r.GetInt64(0); distinct = r.GetInt64(1);
            minDur = r.IsDBNull(2) ? null : r.GetInt64(2);
            qhShare = r.IsDBNull(3) ? 0 : r.GetDouble(3);
        }

        long noDuration;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT count(*) FROM {alias}.executions AS e WHERE e.normalized_hash IS NOT NULL AND e.duration_us IS NULL{DbWhere(o)}";
            AddDb(c, o);
            noDuration = Convert.ToInt64(c.ExecuteScalar());
        }

        var dbs = Strings(conn, $"SELECT DISTINCT database_name FROM {x} AS x WHERE database_name IS NOT NULL ORDER BY 1", o);
        var versions = Strings(conn, $"SELECT DISTINCT CAST(normalizer_version AS VARCHAR) AS v FROM {alias}.ingestion_runs WHERE run_id IN (SELECT run_id FROM {x} AS x) ORDER BY 1", o)
            .Select(int.Parse).ToList();
        var redaction = Strings(conn, $"SELECT DISTINCT coalesce(redaction_policy, 'unknown') AS p FROM {alias}.ingestion_runs WHERE run_id IN (SELECT run_id FROM {x} AS x) ORDER BY 1", o);
        var textPolicies = Strings(conn, $"SELECT DISTINCT coalesce(sql_text_policy, 'raw') AS p FROM {alias}.ingestion_runs ORDER BY 1", o);

        long eligible, excluded;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT count(*) FILTER (WHERE plan_hash_source = 'queryplanhash' AND query_hash IS NOT NULL) AS el,
                       count(*) FILTER (WHERE NOT (plan_hash_source = 'queryplanhash' AND query_hash IS NOT NULL)) AS ex
                FROM {alias}.plan_profiles
                """;
            using var r = c.ExecuteReader();
            r.Read();
            eligible = r.GetInt64(0); excluded = r.GetInt64(1);
        }

        return new CompareSideCoverage(
            Path.GetDirectoryName(dbPath) ?? dbPath, runs, runs.Sum(r => r.SpanUs), gapUs, gapRun,
            execs, noDuration, distinct, dbs.Take(o.Limit).ToList(), Math.Max(0, dbs.Count - o.Limit),
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

`SqlTextPolicies` deliberately reads every run of the project, filtered or not: a text is written by whichever run met its hash first (§7), so a filter on executions does not bound which policy produced it. A NULL policy (a run older than the column) maps to `raw`, which is what such a run stored. The plan-profile counts are before any `--database` filter, which the markdown label says (Task 8).

- [ ] Step 4: Run.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`
Expected: 17 passed, 0 failed.

- [ ] Step 5: Break it. Replace `runs.Sum(r => r.SpanUs)` with `runs.Count == 0 ? 0 : (long)(runs[^1].Last - runs[0].First).TotalMicroseconds` and rerun.
Expected: exactly 1 failed (`Active_span_is_the_sum_of_run_spans_not_the_gap_between_imports`), 16 passed. Restore; 17 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): coverage block with per-run spans and gaps" -m "Every number in the digest depends on the windows it was computed over. The active span sums the runs, so two imports a day apart are not read as a quiet day, a run with a large internal gap is named, and executions without a duration are counted rather than silently dropped."
```

---

### Task 3: Printed text (§7, "The printed text")

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Coverage`, `CompareCoverage.Base/Target.SqlTextPolicies`, `CompareFixture.BlockingOnly`.
- Produces: `internal const string TextWithheld`; `internal static bool MustSanitize(CompareCoverage coverage)`; `internal static string TextsCte(bool sanitize)`, a CTE named `compare_texts(h, txt)` that every later section joins; test seam `public string? TextOf(CompareOptions o, string hash)`.

- [ ] Step 1: Failing tests. The leaky text is a double-quoted value, which ingestion stores verbatim under `raw` and collapses under `literals` (`NormalizedQuery.QiCollapsedSql`).

```csharp
    private const string Leaky = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";

    [Fact]
    public void A_raw_first_side_is_not_trusted_and_the_literals_side_text_is_printed()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        b.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));                       // raw first
        b.Import(Burst("h1", 1, 5_000, T0.AddDays(1)).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        var text = new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h1");
        Assert.NotNull(text);
        Assert.DoesNotContain("GADGET-7781", text);
        Assert.NotEqual(ProjectComparison.TextWithheld, text);
    }

    [Fact]
    public void A_hash_known_only_to_a_raw_first_side_is_withheld()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0), SqlTextSanitization.Literals);
        b.Import(Burst("h2", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        b.Import(Burst("h1", 1, 5_000, T0.AddDays(1)), SqlTextSanitization.Literals);
        Assert.Equal(ProjectComparison.TextWithheld, new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h2"));
    }

    [Fact]
    public void Two_all_raw_projects_print_the_stored_text()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        b.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        Assert.Contains("GADGET-7781", new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h1"));
    }

    [Fact]
    public void A_hash_first_met_in_a_raw_blocking_report_is_withheld()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0), SqlTextSanitization.Literals);
        b.BlockingOnly("hb", Leaky, SqlTextSanitization.Raw);                                         // wrote the text
        b.Import(Burst("hb", 1, 5_000, T0.AddHours(1)).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        Assert.Equal(ProjectComparison.TextWithheld, new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "hb"));
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

- [ ] Step 2: Run, expect a build error (`TextOf` missing).

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~ProjectComparisonTests" 2>&1 | tail -5`

- [ ] Step 3: Implement.

```csharp
    internal const string TextWithheld = "(text withheld: first imported under raw)";

    internal static bool MustSanitize(CompareCoverage c) =>
        c.Base.SqlTextPolicies.Concat(c.Target.SqlTextPolicies).Any(p => p != "raw");

    /// <summary>
    /// Le texte imprime pour chaque empreinte, en CTE compare_texts(h, txt). Les deux ecrivains de
    /// normalized_queries (l'upsert des executions, l'insert des blocages) gardent le premier
    /// texte ; la politique est par run. Le texte d'un cote n'est donc fiable que si le premier
    /// run de ce cote qui a rencontre l'empreinte etait en literals. Rien n'est re-parse : un
    /// texte normalise porte des ?, que ScriptDom ne parse pas (spec §7, revision 3).
    /// </summary>
    internal static string TextsCte(bool sanitize)
    {
        if (!sanitize)
            return """
                compare_texts AS (
                  SELECT a.h AS h, coalesce(tq.normalized_sql, bq.normalized_sql) AS txt
                  FROM (SELECT normalized_hash AS h FROM base.normalized_queries
                        UNION SELECT normalized_hash AS h FROM target.normalized_queries) AS a
                  LEFT JOIN base.normalized_queries AS bq ON bq.normalized_hash = a.h
                  LEFT JOIN target.normalized_queries AS tq ON tq.normalized_hash = a.h)
                """;

        static string Trusted(string alias) => $"""
            SELECT o.h AS h, q.normalized_sql AS txt
            FROM (SELECT s.h AS h, min(s.run_id) AS r FROM (
                    SELECT normalized_hash AS h, run_id AS run_id FROM {alias}.executions WHERE normalized_hash IS NOT NULL
                    UNION ALL
                    SELECT p.inputbuf_fingerprint AS h, br.run_id AS run_id
                    FROM {alias}.blocking_processes AS p JOIN {alias}.blocking_reports AS br ON br.report_id = p.report_id
                    WHERE p.inputbuf_fingerprint IS NOT NULL) AS s
                  GROUP BY s.h) AS o
            JOIN {alias}.ingestion_runs AS r ON r.run_id = o.r
            JOIN {alias}.normalized_queries AS q ON q.normalized_hash = o.h
            WHERE r.sql_text_policy = 'literals'
            """;

        return $"""
            compare_texts AS (
              SELECT a.h AS h, coalesce(tt.txt, tb.txt, '{TextWithheld}') AS txt
              FROM (SELECT normalized_hash AS h FROM base.normalized_queries
                    UNION SELECT normalized_hash AS h FROM target.normalized_queries) AS a
              LEFT JOIN ({Trusted("base")}) AS tb ON tb.h = a.h
              LEFT JOIN ({Trusted("target")}) AS tt ON tt.h = a.h)
            """;
    }

    public string? TextOf(CompareOptions o, string hash)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        var sanitize = MustSanitize(Coverage(conn, o));
        using var c = conn.CreateCommand();
        c.CommandText = $"WITH {TextsCte(sanitize)} SELECT txt FROM compare_texts WHERE h = $h";
        Add(c, "$h", hash);
        return c.ExecuteScalar() as string;
    }
```

`TextWithheld` is a constant of this file interpolated into SQL, not user text, and holds no single quote.

- [ ] Step 4: Run. Expected: 22 passed, 0 failed.

- [ ] Step 5: Break it: in `Trusted`, replace `WHERE r.sql_text_policy = 'literals'` with `WHERE r.sql_text_policy IS NOT NULL`. Rerun.
Expected: exactly 3 failed (`A_raw_first_side_is_not_trusted...`, `A_hash_known_only_to_a_raw_first_side_is_withheld`, `A_hash_first_met_in_a_raw_blocking_report_is_withheld`), 19 passed. Restore; 22 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): print a statement text only when the run that wrote it was literals" -m "normalized_queries keeps the first text written for a hash, from executions or from blocking reports, so the policy of a project says nothing about a given row. Re-normalizing at report time was tried in the plan and destroys every text, since ScriptDom does not parse the question marks of a normalized statement."
```

---

### Task 4: Load per hour and one-sided statements (§8, §9)

Files:
- Modify: `src/SqlFerret.Core/Analysis/ProjectComparison.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs`

Interfaces:
- Consumes: `Execs`, `AddDb`, `Add`, `TextsCte`, `MustSanitize`, `CompareCoverage.Base/Target.ActiveSpanUs`.
- Produces: `internal (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? Load(DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)`; `internal (OneSideList Appeared, OneSideList Disappeared) OneSided(DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)`; test seams `LoadOnly(CompareOptions)` and `OneSidedOnly(CompareOptions)`.

- [ ] Step 1: Failing tests.

```csharp
    [Fact]
    public void Ten_times_more_frequent_at_the_same_speed_ranks_as_a_load_increase()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 6, 10_000, T0, 600));      // 6 over 50 min
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

    [Fact]
    public void The_database_filter_applies_to_one_sided_statements()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("both", 2, 10_000, T0, 600).Concat(Burst("otherdb", 2, 10_000, T0, 600, db: "AppDb2")));
        b.Import(Burst("both", 2, 10_000, T0, 600));
        var (_, disappeared) = new ProjectComparison(a.DbPath, b.DbPath).OneSidedOnly(Opt with { Database = "AppDb" });
        Assert.Equal(0L, disappeared.Total);
        var (_, all) = new ProjectComparison(a.DbPath, b.DbPath).OneSidedOnly(Opt);
        Assert.Equal(1L, all.Total);
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

        List<LoadRow> Read(string filter, string order)
        {
            using var c = conn.CreateCommand();
            // filter et order sont des constantes de ce fichier, jamais une entree.
            c.CommandText = $"""
                WITH {TextsCte(sanitize)},
                     b AS (SELECT normalized_hash AS h, count(*) AS n, sum(duration_us)::DOUBLE AS d FROM {Execs("base", o)} AS x GROUP BY 1),
                     t AS (SELECT normalized_hash AS h, count(*) AS n, sum(duration_us)::DOUBLE AS d FROM {Execs("target", o)} AS x GROUP BY 1)
                SELECT b.h AS h, q.statement_kind AS kind, q.primary_table AS tbl, ct.txt AS txt,
                       b.n * $bf AS bn, t.n * $tf AS tn, b.d * $bf AS bd, t.d * $tf AS td, t.d * $tf - b.d * $bf AS delta
                FROM b JOIN t ON b.h = t.h
                JOIN target.normalized_queries AS q ON q.normalized_hash = b.h
                JOIN compare_texts AS ct ON ct.h = b.h
                WHERE greatest(b.n, t.n) >= $minExec AND {filter}
                ORDER BY {order}, b.h
                LIMIT $lim
                """;
            AddDb(c, o);
            Add(c, "$bf", UsPerHour / cov.Base.ActiveSpanUs);
            Add(c, "$tf", UsPerHour / cov.Target.ActiveSpanUs);
            Add(c, "$minExec", o.Thresholds.MinExecutions);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var list = new List<LoadRow>();
            while (r.Read())
                list.Add(new LoadRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8)));
            return list;
        }

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
                WITH {TextsCte(sanitize)},
                     p AS (SELECT normalized_hash AS h, count(*) AS n, sum(duration_us)::BIGINT AS d FROM {Execs(present, o)} AS x GROUP BY 1),
                     a AS (SELECT DISTINCT normalized_hash AS h FROM {Execs(absent, o)} AS x)
                SELECT p.h AS h, q.statement_kind AS kind, q.primary_table AS tbl, ct.txt AS txt,
                       p.n AS n, p.d AS d, count(*) OVER () AS total
                FROM p JOIN {present}.normalized_queries AS q ON q.normalized_hash = p.h
                JOIN compare_texts AS ct ON ct.h = p.h
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
                    r.GetString(3), r.GetInt64(4), d, perHour ? d * UsPerHour / spanUs : null));
                total = r.GetInt64(6);
            }
            return new OneSideList(rows, total);
        }

        return (List("target", "base", cov.Target.ActiveSpanUs), List("base", "target", cov.Base.ActiveSpanUs));
    }
```

`count(*) OVER ()` is evaluated before `LIMIT` on the embedded engine (measured by four readers of revision 1). `sum(duration_us)` is never NULL here, since `Execs` keeps only rows with a duration.

- [ ] Step 4: Run. Expected: 26 passed, 0 failed.

- [ ] Step 5: Break it: in `Load`, replace `UsPerHour / cov.Base.ActiveSpanUs` with `UsPerHour / cov.Target.ActiveSpanUs`. Rerun.
Expected: exactly 1 failed (`Ten_times_more_frequent...`, on `BaseUsPerHour`), 25 passed. Restore; 26 passed.

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
- Consumes: `Execs`, `AddDb`, `Add`, `TextsCte`, `MustSanitize`, `Coverage`.
- Produces: `internal (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) Cost(DuckDBConnection conn, CompareOptions o, bool sanitize)`; test seam `CostOnly(CompareOptions)`.

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

    [Fact]
    public void More_frequent_at_the_same_speed_is_not_a_cost_regression()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 6, 10_000, T0, 600));
        b.Import(Burst("h1", 60, 10_000, T0, 50));
        var (reg, gains) = new ProjectComparison(a.DbPath, b.DbPath).CostOnly(Opt);
        Assert.Empty(reg);
        Assert.Empty(gains);
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
            SELECT normalized_hash AS h, count(*) AS n, avg(duration_us) AS a,
                   quantile_cont(duration_us, 0.95) AS p, avg(cpu_time_us) AS c, avg(logical_reads) AS r
            FROM {Execs(alias, o)} AS x GROUP BY 1
            """;

        List<CostRow> Read(string filter, string order)
        {
            using var c = conn.CreateCommand();
            // filter et order sont des constantes de ce fichier.
            c.CommandText = $"""
                WITH {TextsCte(sanitize)},
                     b AS ({Agg("base")}), t AS ({Agg("target")}),
                     j AS (SELECT b.h AS h, b.n AS bn, t.n AS tn, b.a AS ba, t.a AS ta, b.p AS bp, t.p AS tp,
                                  b.c AS bc, t.c AS tc, b.r AS br, t.r AS tr,
                                  CASE WHEN b.a = 0 THEN NULL ELSE t.a / b.a END AS ratio
                           FROM b JOIN t ON b.h = t.h
                           WHERE b.n >= $minExec AND t.n >= $minExec AND greatest(b.a, t.a) >= $minAvg)
                SELECT j.h AS h, q.statement_kind AS kind, q.primary_table AS tbl, ct.txt AS txt,
                       bn, tn, ba, ta, bp, tp, bc, tc, br, tr, ratio
                FROM j JOIN target.normalized_queries AS q ON q.normalized_hash = j.h
                JOIN compare_texts AS ct ON ct.h = j.h
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
                    r.GetString(3), r.GetInt64(4), r.GetInt64(5),
                    r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9),
                    D(10), D(11), D(12), D(13), D(14)));
            return list;
        }

        // Une moyenne de base nulle donne un ratio NULL, classe en tete des regressions.
        return (Read("(ratio > 1 OR ratio IS NULL)", "(ratio IS NULL) DESC, ratio DESC, ta DESC"),
                Read("ratio < 1", "ratio ASC"));
    }
```

- [ ] Step 4: Run. Expected: 29 passed, 0 failed.

- [ ] Step 5: Break it: replace `CASE WHEN b.a = 0 THEN NULL ELSE t.a / b.a END` with `t.a / b.a`. Rerun.
Expected: exactly 1 failed (`A_zero_base_average...`), 28 passed. Restore; 29 passed. The JSON test of Task 7 would fail too under this break, but it does not exist yet.

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
- Consumes: `Execs`, `AddDb`, `Add`, `CompareCoverage.Base/Target.EligiblePlanProfiles`, `CompareFixture.Plan(...)`, `CompareFixture.Plans(...)`.
- Produces: `internal PlanSection PlanChanges(DuckDBConnection conn, CompareOptions o, CompareCoverage cov)`; test seam `PlansOnly(CompareOptions)`.

- [ ] Step 1: Failing tests. `0A1B2C3D4E5F6071` has a leading zero; its decimal form is `728224406569967729` (checked by three readers).

```csharp
    private static CompareFixture.Exec Linked(DateTime at, string db = "AppDb") =>
        new("h1", Exec1, 5_000, at, db, QueryHash: "728224406569967729");

    [Fact]
    public void A_changed_plan_and_an_appeared_finding_are_listed_and_linked_through_a_leading_zero_hash()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var ra = a.Import([Linked(T0)]);
        var rb = b.Import([Linked(T0)]);
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
        var ra = a.Import([Linked(T0)]);
        var rb = b.Import([Linked(T0)]);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000), CompareFixture.Plan("00000000000000AA", "Q1", 1_000));
        b.Plans(rb, CompareFixture.Plan("0A1B2C3D4E5F6071", "P2", 1_000), CompareFixture.Plan("00000000000000AA", "Q2", 1_000));
        var s = new ProjectComparison(a.DbPath, b.DbPath).PlansOnly(Opt with { Database = "AppDb" });
        Assert.Equal("0A1B2C3D4E5F6071", Assert.Single(s.Rows).QueryHash);
        Assert.Equal(2L, s.UnlinkedExcluded);
    }

    [Fact]
    public void Unlinked_plans_are_counted_even_when_no_plan_changed()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var ra = a.Import([Linked(T0)]);
        var rb = b.Import([Linked(T0)]);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000), CompareFixture.Plan("00000000000000AA", "Q1", 1_000));
        b.Plans(rb, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000), CompareFixture.Plan("00000000000000AA", "Q1", 1_000));
        var s = new ProjectComparison(a.DbPath, b.DbPath).PlansOnly(Opt with { Database = "AppDb" });
        Assert.Empty(s.Rows);
        Assert.Equal(2L, s.UnlinkedExcluded);
    }

    [Fact]
    public void The_plan_median_counts_each_profile_once_whatever_its_findings()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var ra = a.Import([Linked(T0)]);
        var rb = b.Import([Linked(T0)]);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));
        var f = new SqlFerret.Core.Plans.PlanFinding("large_scan", 1, "{}");
        b.Plans(rb,
            CompareFixture.Plan("0A1B2C3D4E5F6071", "P2", 1_000, "queryplanhash", f, f with { NodeId = 2 }, f with { NodeId = 3 }),
            CompareFixture.Plan("0A1B2C3D4E5F6071", "P2", 9_000));
        var row = Assert.Single(new ProjectComparison(a.DbPath, b.DbPath).PlansOnly(Opt).Rows);
        Assert.Equal(5_000d, row.TargetMedianUs);
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
            SELECT {PlanHashHex} AS qh, p.plan_hash AS plan_hash, p.plan_profile_id AS plan_profile_id, p.duration_us AS duration_us
            FROM {alias}.plan_profiles AS p
            WHERE p.plan_hash_source = 'queryplanhash' AND p.query_hash IS NOT NULL
            """;
        // Avec --database, un plan n'est garde que s'il se rattache a une execution de cette base.
        string Kept(string alias) => o.Database is null ? Eligible(alias) : $"""
            SELECT * FROM ({Eligible(alias)}) AS el WHERE el.qh IN
              (SELECT {ExecHashHex} AS qh FROM {Execs(alias, o)} AS e WHERE e.query_hash IS NOT NULL)
            """;

        long unlinked;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT (SELECT count(*) FROM ({Eligible("base")}) AS x1) + (SELECT count(*) FROM ({Eligible("target")}) AS x2)
                     - (SELECT count(*) FROM ({Kept("base")}) AS x3) - (SELECT count(*) FROM ({Kept("target")}) AS x4) AS unlinked
                """;
            AddDb(c, o);
            unlinked = Convert.ToInt64(c.ExecuteScalar());
        }

        using var q = conn.CreateCommand();
        // La mediane est prise sur les profils avant toute jointure aux findings : jointe apres,
        // un profil a trois findings comptait trois fois (mesure, revision 1 du plan).
        q.CommandText = $"""
            WITH bk AS ({Kept("base")}), tk AS ({Kept("target")}),
                 b AS (SELECT qh, array_to_string(list_sort(list_distinct(list(plan_hash))), ',') AS plan_set,
                              median(duration_us) AS med FROM bk GROUP BY qh),
                 t AS (SELECT qh, array_to_string(list_sort(list_distinct(list(plan_hash))), ',') AS plan_set,
                              median(duration_us) AS med FROM tk GROUP BY qh),
                 bf AS (SELECT k.qh AS qh, array_to_string(list_sort(list_distinct(list(f.kind))), ',') AS kinds
                        FROM bk AS k JOIN base.plan_findings AS f ON f.plan_profile_id = k.plan_profile_id GROUP BY k.qh),
                 tf AS (SELECT k.qh AS qh, array_to_string(list_sort(list_distinct(list(f.kind))), ',') AS kinds
                        FROM tk AS k JOIN target.plan_findings AS f ON f.plan_profile_id = k.plan_profile_id GROUP BY k.qh),
                 link AS (SELECT {ExecHashHex} AS qh, any_value(e.normalized_hash) AS nh
                          FROM {Execs("target", o)} AS e WHERE e.query_hash IS NOT NULL GROUP BY 1)
            SELECT b.qh AS qh, b.plan_set AS b_plans, t.plan_set AS t_plans, bf.kinds AS b_kinds, tf.kinds AS t_kinds,
                   b.med AS b_med, t.med AS t_med, link.nh AS nh, count(*) OVER () AS total
            FROM b JOIN t ON b.qh = t.qh
            LEFT JOIN bf ON bf.qh = b.qh
            LEFT JOIN tf ON tf.qh = t.qh
            LEFT JOIN link ON link.qh = b.qh
            WHERE b.plan_set <> t.plan_set OR coalesce(bf.kinds, '') <> coalesce(tf.kinds, '')
            ORDER BY t.med DESC NULLS LAST, b.qh
            LIMIT $lim
            """;
        AddDb(q, o);
        Add(q, "$lim", o.Limit);

        static IReadOnlyList<string> Split(string? s) => string.IsNullOrEmpty(s) ? [] : s.Split(',');
        var rows = new List<PlanChangeRow>();
        long total = 0;
        using (var r = q.ExecuteReader())
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
            }
        return new PlanSection(false, null, rows, total, unlinked);
    }
```

`median` over `BIGINT` and a `$db` bound once but used several times were both accepted by the embedded engine (measured by four readers of revision 1). If `GetDouble(5)` throws, stop and report the actual type.

- [ ] Step 4: Run. Expected: 34 passed, 0 failed.

- [ ] Step 5: Break it: change `ExecHashHex` to `"upper(hex(TRY_CAST(e.query_hash AS UBIGINT)))"`. Rerun.
Expected: exactly 3 failed (`A_changed_plan...` on `LinkedNormalizedHash`, `With_a_database_filter...`, `Unlinked_plans_are_counted...`), 31 passed. Restore; 34 passed.

- [ ] Step 6: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): plan and finding changes per query hash" -m "Plans are keyed on the query hash of the plan XML, single-statement only, because a multi-statement plan would report a change in its second statement against its first. The link to executions pads to sixteen hex digits, since hex alone drops the leading zeros of one hash in sixteen, and the unlinked count is its own query so that it survives an empty section."
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
    public void A_raw_then_literals_project_never_prints_the_value_in_any_section()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var execs = Enumerable.Range(0, 6).Select(i => new CompareFixture.Exec("h1", Leaky, 10_000 * (i + 1), T0.AddMinutes(i * 10))).ToList();
        var ra = a.Import(execs, SqlTextSanitization.Literals);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P0", 1_000));
        b.Import(execs);                                                    // raw first: this text is kept
        b.Import(execs.Select(e => e with { At = e.At.AddDays(1), DurationUs = e.DurationUs * 3 }), SqlTextSanitization.Literals);
        var r9 = b.Import([new CompareFixture.Exec("h9", Leaky, 50_000, T0), new CompareFixture.Exec("h9", Leaky, null, T0.AddMinutes(1))]);
        b.Plans(r9, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));  // a changed plan, so the plan section has a row
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(Opt);
        var json = System.Text.Json.JsonSerializer.Serialize(new CompareDigestEnvelope(ProjectComparison.SchemaVersion, DateTime.UtcNow, d));
        Assert.DoesNotContain("GADGET-7781", json);
        Assert.Single(d.Plans.Rows);
        Assert.DoesNotContain("WidgetId = 4242", json);                     // plan statement text, never printed
        Assert.NotEmpty(d.Regressions);
        Assert.Equal(1L, d.Appeared.Total);
        Assert.Equal(ProjectComparison.TextWithheld, d.Appeared.Rows[0].NormalizedSql);
        Assert.Contains(d.Coverage.Notes, n => n.Contains("withheld"));
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
        if (sanitize && cov.Base.SqlTextPolicies.Concat(cov.Target.SqlTextPolicies).Contains("raw"))
            cov = cov with
            {
                Notes = [.. cov.Notes,
                    "a side holds runs imported raw: statement texts first met by such a run are withheld, and shown from the other side when it was imported with literals"],
            };
        var (reg, gains) = Cost(conn, options, sanitize);
        var load = Load(conn, options, cov, sanitize);
        var (appeared, disappeared) = OneSided(conn, options, cov, sanitize);
        var plans = PlanChanges(conn, options, cov);
        return new CompareDigestResult(cov, reg, gains,
            load is not null, load?.Up ?? [], load?.Down ?? [],
            appeared, disappeared, plans);
    }
```

- [ ] Step 4: Run. Expected: 37 passed, 0 failed.

- [ ] Step 5: Break it: in `Run`, pass `false` instead of `sanitize` to `OneSided`. Rerun.
Expected: exactly 1 failed (`A_raw_then_literals...`), 36 passed. Restore; 37 passed.

- [ ] Step 6: Run the whole suite once: `dotnet test 2>&1 | tail -8`. Expected: 717 tests across both test projects (680 + 37), the same skips as before (up to 11), 0 failed.

- [ ] Step 7: Commit.

```bash
dotnet format --include src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git add src/SqlFerret.Core/Analysis/ProjectComparison.cs tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
git commit -m "feat(compare): assemble the digest" -m "One connection serves every section, so both projects are attached once and checked once. The end-to-end test builds the raw-then-literals project the spec review found leaking, through the real sanitizer, and checks the serialized result rather than a section in isolation."
```

---

### Task 8: CLI command and markdown

Files:
- Create: `src/SqlFerret.Cli/CompareDigestMarkdown.cs`
- Modify: `src/SqlFerret.Cli/Program.cs` (usage string, around line 50; new `case "compare":` right after the `case "export-health":` block)
- Create: `tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs`
- Create: `tests/SqlFerret.Core.Tests/CliCompareTests.cs`

Interfaces:
- Consumes: `ProjectComparison.Run`, `ProjectComparison.SchemaVersion`, `CompareDigestEnvelope`, `CompareRefusedException`, `SqlFerretConfig.Load(string? jsonPath)` and its `DurationUnit`, `DisplayFormat.Duration(long microseconds, string unit)`, `MarkdownText.Safe(string?)`, `BlockingDigestMarkdown.HasTraversal(string)`.
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
    private static readonly CompareOptions Opt = new(10, null, new CompareThresholds());
    private const string Exec1 = "exec AppSchema.WidgetRecalc @WidgetId = 42";
    private const string Leaky = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";

    [Fact]
    public void Markdown_has_every_section_in_order_and_says_when_one_is_empty()
    {
        using var a = new CompareFixture();
        a.Import(Enumerable.Range(0, 3).Select(i => new CompareFixture.Exec("h1", Exec1, 5_000, T0.AddMinutes(i))));
        var d = new ProjectComparison(a.DbPath, a.DbPath).Run(Opt);
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
        var execs = Enumerable.Range(0, 6).Select(i => new CompareFixture.Exec("h1", Leaky, 10_000 * (i + 1), T0.AddMinutes(i * 10))).ToList();
        a.Import(execs, SqlTextSanitization.Literals);
        b.Import(execs);
        b.Import(execs.Select(e => e with { At = e.At.AddDays(1), DurationUs = e.DurationUs * 3 }), SqlTextSanitization.Literals);
        b.Import([new CompareFixture.Exec("h9", Leaky, 50_000, T0)]);
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(Opt);
        Assert.DoesNotContain("GADGET-7781", CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms"));
    }

    [Fact]
    public void Markdown_lists_each_run_the_largest_gap_and_never_a_plan_statement_text()
    {
        using var a = new CompareFixture();
        var r1 = a.Import(Enumerable.Range(0, 2).Select(i => new CompareFixture.Exec("h1", Exec1, 5_000, T0.AddMinutes(i * 5))));
        a.Import(Enumerable.Range(0, 2).Select(i => new CompareFixture.Exec("h1", Exec1, 5_000, T0.AddDays(1).AddMinutes(i))));
        a.Plans(r1, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));
        var d = new ProjectComparison(a.DbPath, a.DbPath).Run(Opt);
        var md = CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms");
        Assert.Contains($"| {r1} |", md);
        Assert.Contains("Largest gap", md);
        Assert.DoesNotContain("WidgetId = 4242", md);
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
        f.Import(Enumerable.Range(0, 3).Select(i => new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc @WidgetId = 42", 5_000, T0.AddMinutes(i))));
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
        Assert.Contains("SchemaVersion", output);
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
    public void A_bare_database_flag_is_refused_rather_than_read_as_all()
    {
        using var a = Project();
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--database");
        Assert.Equal(1, code);
        Assert.Contains("--database", err);
    }

    [Fact]
    public void An_output_file_inside_a_project_is_refused()
    {
        using var a = Project();
        var before = Snapshot(a.Dir);
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--out", Path.Combine(a.Dir, "report.md"));
        Assert.Equal(1, code);
        Assert.Contains("inside", err);
        Assert.Equal(before, Snapshot(a.Dir));
    }

    [Fact]
    public void An_output_in_a_missing_directory_exits_one_without_a_stack_trace()
    {
        using var a = Project();
        var outPath = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}", "report.md");
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--out", outPath);
        Assert.Equal(1, code);
        Assert.Contains("cannot write", err);
        Assert.DoesNotContain("Unhandled", err);
    }

    [SkippableFact]
    public void An_output_reaching_a_project_through_a_symlink_is_refused()
    {
        Skip.If(OperatingSystem.IsWindows(), "creating a symbolic link needs a privilege on Windows");
        using var a = Project();
        var link = Path.Combine(Path.GetTempPath(), $"sf_cmp_link_{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, a.Dir);
        try
        {
            var before = Snapshot(a.Dir);
            var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--out", Path.Combine(link, "report.md"));
            Assert.Equal(1, code);
            Assert.Contains("inside", err);
            Assert.Equal(before, Snapshot(a.Dir));
        }
        finally { Directory.Delete(link); }   // supprime le lien, pas sa cible
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

That is 3 renderer tests and 1 + 2 + 4 + 1 + 1 + 1 + 1 + 1 = 12 CLI test cases: 15 in total. JSON property names are PascalCase with the serializer options `export-health` uses, hence `SchemaVersion` in the first CLI test.

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
    private static string N(double? v) => v is { } x ? N(x) : "-";

    public static string Render(CompareDigestEnvelope e, string durationUnit)
    {
        string Dur(double us) => DisplayFormat.Duration((long)us, durationUnit);
        string DurN(double? us) => us is { } x ? Dur(x) : "-";
        var d = e.Digest;
        var sb = new StringBuilder();
        sb.AppendLine("# Project comparison").AppendLine();

        sb.AppendLine("## Coverage").AppendLine();
        var (b, t) = (d.Coverage.Base, d.Coverage.Target);
        sb.AppendLine("| | Base | Target |").AppendLine("|---|---|---|");
        sb.AppendLine($"| Project | {Safe(b.ProjectDir)} | {Safe(t.ProjectDir)} |");
        sb.AppendLine($"| Active span | {Dur(b.ActiveSpanUs)} | {Dur(t.ActiveSpanUs)} |");
        sb.AppendLine($"| Largest gap inside a run | {DurN(b.LargestGapUs)}{(b.LargestGapRunId is { } br ? $" (run {br})" : "")} | {DurN(t.LargestGapUs)}{(t.LargestGapRunId is { } tr ? $" (run {tr})" : "")} |");
        sb.AppendLine($"| Executions | {b.Executions} | {t.Executions} |");
        sb.AppendLine($"| Executions without a duration, left out | {b.ExecutionsWithoutDuration} | {t.ExecutionsWithoutDuration} |");
        sb.AppendLine($"| Distinct statements | {b.DistinctStatements} | {t.DistinctStatements} |");
        sb.AppendLine($"| Databases | {Safe(string.Join(", ", b.Databases))}{(b.OtherDatabases > 0 ? $" (+{b.OtherDatabases})" : "")} | {Safe(string.Join(", ", t.Databases))}{(t.OtherDatabases > 0 ? $" (+{t.OtherDatabases})" : "")} |");
        sb.AppendLine($"| Normalizer versions | {string.Join(", ", b.NormalizerVersions)} | {string.Join(", ", t.NormalizerVersions)} |");
        sb.AppendLine($"| Redaction | {Safe(string.Join(", ", b.RedactionPolicies))} | {Safe(string.Join(", ", t.RedactionPolicies))} |");
        sb.AppendLine($"| SQL text | {Safe(string.Join(", ", b.SqlTextPolicies))} | {Safe(string.Join(", ", t.SqlTextPolicies))} |");
        sb.AppendLine($"| Smallest duration | {DurN(b.MinDurationUs)} | {DurN(t.MinDurationUs)} |");
        sb.AppendLine($"| Executions with query_hash | {N(b.QueryHashShare * 100)} % | {N(t.QueryHashShare * 100)} % |");
        sb.AppendLine($"| Plan profiles used / left out (before --database) | {b.EligiblePlanProfiles} / {b.ExcludedPlanProfiles} | {t.EligiblePlanProfiles} / {t.ExcludedPlanProfiles} |");
        sb.AppendLine();
        foreach (var (label, side) in (ReadOnlySpan<(string, CompareSideCoverage)>)[("Base", b), ("Target", t)])
        {
            sb.AppendLine($"{label} runs:").AppendLine();
            sb.AppendLine("| Run | First | Last | Span | Executions |").AppendLine("|---|---|---|---|---|");
            foreach (var r in side.Runs)
                sb.AppendLine($"| {r.RunId} | {r.First.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} | {r.Last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} | {Dur(r.SpanUs)} | {r.Executions} |");
            sb.AppendLine();
        }
        foreach (var n in d.Coverage.Notes) sb.AppendLine($"- {Safe(n)}");
        sb.AppendLine();

        sb.AppendLine("## Cost per execution").AppendLine();
        CostTable(sb, "Regressions", d.Regressions, "No regression above the thresholds.", Dur, DurN);
        CostTable(sb, "Gains", d.Gains, "No gain above the thresholds.", Dur, DurN);

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
            sb.AppendLine("| Query hash | Plan changed | Findings appeared | Findings gone | Median base | Median target | Linked statement hash |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in d.Plans.Rows)
                sb.AppendLine($"| {Safe(r.QueryHash)} | {(r.PlanChanged ? "yes" : "no")} | {Safe(string.Join(", ", r.AppearedKinds))} | {Safe(string.Join(", ", r.DisappearedKinds))} | {DurN(r.BaseMedianUs)} | {DurN(r.TargetMedianUs)} | {Safe(r.LinkedNormalizedHash ?? "-")} |");
            sb.AppendLine();
        }
        if (d.Plans.UnlinkedExcluded > 0)
            sb.AppendLine($"{d.Plans.UnlinkedExcluded} plan profiles left out: not linked to an execution of the filtered database.").AppendLine();
        return sb.ToString();
    }

    private static void CostTable(StringBuilder sb, string title, IReadOnlyList<CostRow> rows, string empty,
        Func<double, string> dur, Func<double?, string> durN)
    {
        sb.AppendLine($"### {title}").AppendLine();
        if (rows.Count == 0) { sb.AppendLine(empty).AppendLine(); return; }
        sb.AppendLine("| Ratio | Avg base | Avg target | p95 base | p95 target | CPU base | CPU target | Reads base | Reads target | Count base/target | Statement |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {(r.Ratio is { } x ? N(x) : "new cost")} | {dur(r.BaseAvgUs)} | {dur(r.TargetAvgUs)} | {dur(r.BaseP95Us)} | {dur(r.TargetP95Us)} | {durN(r.BaseAvgCpuUs)} | {durN(r.TargetAvgCpuUs)} | {N(r.BaseAvgReads)} | {N(r.TargetAvgReads)} | {r.BaseCount}/{r.TargetCount} | `{Safe(r.NormalizedSql)}` |");
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

`DisplayFormat.Duration` takes a `long` and divides; a negative delta passes through with its sign. If you find it renders a negative value badly, say so in the report rather than changing `DisplayFormat`, which other commands use.

- [ ] Step 5: Add the CLI case in `src/SqlFerret.Cli/Program.cs`, right after the closing brace of the `export-health` case. It does not call `OpenProject()`.

```csharp
    case "compare":
        {
            // Un drapeau sans valeur est une erreur, pas « tout » (spec §3, revision 3).
            foreach (var flag in (string[])["--base", "--target", "--database", "--format", "--out", "--limit"])
            {
                var at = Array.IndexOf(args, flag);
                if (at >= 0 && (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal)))
                { Console.Error.WriteLine($"compare: {flag} needs a value"); return 1; }
            }

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

            // Chemin reel : chaque composant qui est un lien symbolique est resolu, sinon un lien vers
            // un projet contournerait le refus de --out. Un composant absent reste tel quel.
            static string RealPath(string path, int depth = 0)
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full)!;
                var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var acc = root;
                for (var i = 0; i < parts.Length; i++)
                {
                    acc = Path.Combine(acc, parts[i]);
                    if (depth < 40 && new DirectoryInfo(acc).LinkTarget is not null
                        && new DirectoryInfo(acc).ResolveLinkTarget(true) is { } target)
                        return RealPath(Path.Combine([target.FullName, .. parts[(i + 1)..]]), depth + 1);
                }
                return acc;
            }

            string baseFull, targetFull;
            try
            {
                baseFull = RealPath(baseDir);
                targetFull = RealPath(targetDir);
                if (outPath.Length > 0)
                {
                    var outFull = Path.Combine(RealPath(Path.GetDirectoryName(Path.GetFullPath(outPath))!), Path.GetFileName(outPath));
                    // ponytail: insensible a la casse sur Windows et macOS ; un volume APFS sensible a la casse n'est pas detecte.
                    var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    bool Inside(string dir) => outFull.StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cmp);
                    if (Inside(baseFull) || Inside(targetFull))
                    { Console.Error.WriteLine("compare: --out must not be inside either project directory"); return 1; }
                }
            }
            catch (IOException ex) { Console.Error.WriteLine($"compare: {ex.Message}"); return 1; }   // une boucle de liens

            // Pas d'OpenProject : il ecrit project.json et cree README, plans/ et exports/ (spec §3).
            string unit;
            try { unit = SqlFerretConfig.Load(Path.Combine(baseFull, "sqlferret.config.json")).DurationUnit; }
            catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"compare: {ex.Message}"); return 1; }

            CompareDigestEnvelope envelope;
            try
            {
                var db = Arg("--database", "");
                var digest = new ProjectComparison(
                        Path.Combine(baseFull, "sqlferret.duckdb"), Path.Combine(targetFull, "sqlferret.duckdb"))
                    .Run(new CompareOptions(limit, db.Length > 0 ? db : null, new CompareThresholds()));
                envelope = new CompareDigestEnvelope(ProjectComparison.SchemaVersion, DateTime.UtcNow, digest);
            }
            catch (CompareRefusedException ex) { Console.Error.WriteLine(ex.Message); return 1; }
            catch (DuckDB.NET.Data.DuckDBException ex) { Console.Error.WriteLine($"compare: {ex.Message}"); return 1; }

            var json = System.Text.Json.JsonSerializer.Serialize(envelope,
                           new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var md = SqlFerret.Cli.CompareDigestMarkdown.Render(envelope, unit);

            try
            {
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"compare: cannot write the output: {ex.Message}"); return 1; }
            return 0;
        }
```

Then append ` | compare --base <dir> --target <dir> [--database <name>] [--format json|md|both] [--out <file>] [--limit <n>]` to the usage string near line 50, before its closing quote.

- [ ] Step 6: Run.

Run: `dotnet build -warnaserror 2>&1 | tail -3 && dotnet test --filter "FullyQualifiedName~CompareDigestMarkdownTests|FullyQualifiedName~CliCompareTests" 2>&1 | tail -5`
Expected: 15 passed, 0 failed.

- [ ] Step 7: Break it: insert `AuditProject.OpenOrCreate(baseDir, Directory.GetCurrentDirectory());` as the first statement after the `--limit` check of the `compare` case. Rerun.
Expected: exactly 3 failed (`Compare_writes_nothing_into_either_project`, and `An_output_file_inside_a_project_is_refused` and `An_output_reaching_a_project_through_a_symlink_is_refused`, whose snapshots see the files `OpenOrCreate` writes before the `--out` refusal is reached), 12 passed. Remove the inserted line; 15 passed.

Then break the guard: replace `RealPath(Path.GetDirectoryName(Path.GetFullPath(outPath))!)` with `Path.GetDirectoryName(Path.GetFullPath(outPath))!`. Expected: exactly 1 failed (`An_output_reaching_a_project_through_a_symlink_is_refused`), 14 passed. Restore; 15 passed.

- [ ] Step 8: Verify the lock message by hand once, outside the test suite (CI has no DuckDB CLI). In one shell invocation:

```bash
cd <worktree> && F=$(mktemp -d) && mkdir $F/p && \
  ( (echo "SELECT 1;"; sleep 8) | duckdb $F/p/sqlferret.duckdb >/dev/null 2>&1 & ); sleep 2; \
  dotnet run --project src/SqlFerret.Cli -- compare --base $F/p --target $F/p; echo "exit $?"; sleep 8; rm -r $F
```

The background `duckdb` process creates the file and holds its lock for eight seconds. Expected: `compare: ... is open in another SQLFerret process ...` and `exit 1`, no stack trace. Three readers reproduced the lock error between two processes; one did not, probably because its `duckdb` had exited before the attach. If you get the schema message instead, rerun with a longer `sleep` before giving up, and report which you saw.

- [ ] Step 9: Commit.

```bash
dotnet format --include src/SqlFerret.Cli/CompareDigestMarkdown.cs src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs tests/SqlFerret.Core.Tests/CliCompareTests.cs
git add src/SqlFerret.Cli/CompareDigestMarkdown.cs src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs tests/SqlFerret.Core.Tests/CliCompareTests.cs
git commit -m "feat(cli): sqlferret compare" -m "The command reads the display unit from the base project's config file and nothing else, because the project opener every other command uses writes provenance and creates directories, and it refuses an output path inside either project for the same reason. The CLI test snapshots both project directories, file by file, to hold that."
```

---

### Task 9: Documentation

Files:
- Modify: `docs/cli-reference.md` (new `## compare` section after `## export-health`, and the command list at the top of the file if it enumerates commands)
- Modify: `docs/README.md` (the `cli-reference.md` row of the full index lists the commands)
- Modify: `CLAUDE.md` ("CLI commands, nine of them" becomes ten, with `compare` added to the list; the test count)
- Modify: `docs/development.md` only if it enumerates commands; check with `grep -n "export-health" docs/development.md`.

- [ ] Step 1: Write the `docs/cli-reference.md` section, in the style of `## export-health`: synopsis, flag table (copy §3 of the spec, with the refusals of a bare flag and of `--out` inside a project), then three short paragraphs: what the coverage block says and why it comes first; the refusals and their remedies (re-import for a fingerprint generation mismatch, any command once for an old schema, close the TUI for a lock); and the text rule (a statement text is shown only when the run that first stored it was `literals`, unless every run on both sides is `raw`; otherwise it is withheld; plan statement text is never printed). Every claim must be one the code of Tasks 1 to 8 implements; do not describe SQL Server behavior.

- [ ] Step 2: Update `CLAUDE.md` and `docs/README.md` as listed.

- [ ] Step 3: Full validation, in one invocation:

```bash
cd <worktree> && dotnet format --verify-no-changes && dotnet build -warnaserror 2>&1 | tail -3 && dotnet test 2>&1 | tail -8
```

Expected: format clean, 0 warnings, 732 tests across both test projects (680 + 37 + 15), up to 11 skipped, 0 failed. In `CLAUDE.md`, update "680 tests" to the number actually observed.

- [ ] Step 4: `git status --porcelain -uall` shows only the files of this task; nothing under `sample/`, no `.duckdb`, no temporary path.

- [ ] Step 5: Commit.

```bash
git add docs/cli-reference.md docs/README.md CLAUDE.md
git commit -m "docs: sqlferret compare" -m "The reference states what the coverage block is for, what each refusal asks the user to do, and why a statement text can be withheld even though both projects hold it."
```
