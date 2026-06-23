# Audit Project Directory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a SqlFerret "project" a single self-contained directory (DuckDB + `plans/` + manifest + README + project-local config/secrets), created and reopened via `--project <dir>`.

**Architecture:** A new `AuditProject` class in `SqlFerret.Core.Project` owns path resolution, skeleton creation, the `project.json` manifest lifecycle, and project-first config/`.env` precedence. The CLI resolves a required `--project <dir>` to an `AuditProject` and drives every command from it. No new SQL, no new dependencies.

**Tech Stack:** .NET 10 / C# 14, `System.Text.Json` (manifest), existing `SqlFerretConfig` + `DotEnv` + `DuckDbProject`, xUnit.

## Global Constraints

- **Target framework:** `net10.0`, Nullable + ImplicitUsings enabled, LangVersion latest.
- **Build is 0 warnings.** Run `dotnet format` before each commit.
- **KISS (binding):** plain `record`/sealed class, static factory, no DI container, no `IAuditProject` interface (single concrete impl).
- **Secrets in `.env` only:** real OS environment always wins; a missing `.env` is a silent no-op; never hardcode secrets.
- **`ProjectManifest.SchemaVersion = 1`** — versions the project-directory format; independent of `QueryNormalizer.Version`.
- **Breaking change accepted:** `--project` is a directory, not a file. No legacy file mode.
- **`git` is wrapped with `rtk`**; commits are co-authored with the trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Work stays on branch `feat/audit-project-directory` (already created).

---

## File Structure

- `src/SqlFerret.Core/Project/ProjectManifest.cs` (new) — manifest record + JSON read/write. Single responsibility: provenance persistence.
- `src/SqlFerret.Core/Project/AuditProject.cs` (new) — project directory abstraction: paths, skeleton, README, manifest lifecycle, config/`.env` precedence, `OpenDb`.
- `src/SqlFerret.Cli/Program.cs` (modify) — `--project <dir>` required; all commands driven from `AuditProject`.
- `tests/SqlFerret.Core.Tests/ProjectManifestTests.cs` (new) — manifest roundtrip + malformed handling.
- `tests/SqlFerret.Core.Tests/AuditProjectTests.cs` (new) — skeleton, manifest lifecycle, README, config/`.env` precedence, `PlansFolder`, `OpenDb`.
- `tests/SqlFerret.Core.Tests/AuditProjectE2ETests.cs` (new) — open project → ingest → query, the real path the CLI uses.

---

## Task 1: ProjectManifest

**Files:**
- Create: `src/SqlFerret.Core/Project/ProjectManifest.cs`
- Test: `tests/SqlFerret.Core.Tests/ProjectManifestTests.cs`

**Interfaces:**
- Consumes: nothing (leaf).
- Produces:
  - `record ProjectManifest(int SchemaVersion, string ToolVersion, DateTime CreatedUtc, DateTime LastOpenedUtc, string? Notes)`
  - `const int ProjectManifest.CurrentSchemaVersion = 1`
  - `static ProjectManifest? ProjectManifest.TryRead(string path)` — returns `null` on missing/malformed.
  - `void ProjectManifest.Write(string path)` — writes indented JSON.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/ProjectManifestTests.cs`:

```csharp
using SqlFerret.Core.Project;
using Xunit;

public class ProjectManifestTests
{
    [Fact]
    public void Write_then_TryRead_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm_{Guid.NewGuid():N}.json");
        try
        {
            var created = new DateTime(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc);
            var m = new ProjectManifest(1, "1.2.3", created, created, null);
            m.Write(path);

            var back = ProjectManifest.TryRead(path);
            Assert.NotNull(back);
            Assert.Equal(1, back!.SchemaVersion);
            Assert.Equal("1.2.3", back.ToolVersion);
            Assert.Equal(created, back.CreatedUtc);
            Assert.Equal(created, back.LastOpenedUtc);
            Assert.Null(back.Notes);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TryRead_missing_returns_null()
        => Assert.Null(ProjectManifest.TryRead("/nonexistent/dir/project.json"));

    [Fact]
    public void TryRead_malformed_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not valid json ");
        try { Assert.Null(ProjectManifest.TryRead(path)); }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ProjectManifestTests`
Expected: FAIL — `ProjectManifest` does not exist (compile error).

- [ ] **Step 3: Write the minimal implementation**

Create `src/SqlFerret.Core/Project/ProjectManifest.cs`:

```csharp
using System.Text.Json;

namespace SqlFerret.Core.Project;

/// <summary>
/// Provenance for an audit project directory, persisted as <c>project.json</c>.
/// Maintained by the tool — not meant to be hand-edited.
/// </summary>
public record ProjectManifest(
    int SchemaVersion,
    string ToolVersion,
    DateTime CreatedUtc,
    DateTime LastOpenedUtc,
    string? Notes)
{
    /// <summary>Current version of the project-directory format.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>Reads the manifest, or returns null if absent or malformed (treat as fresh).</summary>
    public static ProjectManifest? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            // Malformed manifest → caller re-initializes. Deliberate fallback path.
            return null;
        }
    }

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter ProjectManifestTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Project/ProjectManifest.cs tests/SqlFerret.Core.Tests/ProjectManifestTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): ProjectManifest record with JSON read/write

project.json provenance: schema version, tool version, created/last-opened.
TryRead returns null on missing/malformed so callers re-initialize cleanly.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: AuditProject

**Files:**
- Create: `src/SqlFerret.Core/Project/AuditProject.cs`
- Test: `tests/SqlFerret.Core.Tests/AuditProjectTests.cs`

**Interfaces:**
- Consumes: `ProjectManifest` (Task 1); `SqlFerretConfig.Load(string?)`, `DotEnv.Load(string)`, `DuckDbProject.Open(string)` (existing).
- Produces:
  - `sealed class AuditProject` with read-only properties `string Directory`, `string DuckDbPath`, `string PlansFolder`, `string ExportsFolder`, `SqlFerretConfig Config`, `ProjectManifest Manifest`.
  - `static AuditProject AuditProject.OpenOrCreate(string projectDir, string? configFallbackDir = null)`.
  - `DuckDbProject AuditProject.OpenDb()`.

Precedence rules implemented here:
- `.env`: load `<dir>/.env` first, then `<configFallbackDir>/.env` → real OS env > project `.env` > cwd `.env` (relies on `DotEnv.Load` only setting absent keys).
- config: `<dir>/sqlferret.config.json` if present, else `<configFallbackDir>/sqlferret.config.json` if present, else defaults.
- `PlansFolder`: absolute config value honored as-is; relative (incl. the `./plans` default) resolved against `Directory`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/AuditProjectTests.cs`:

```csharp
using SqlFerret.Core.Project;
using Xunit;

public class AuditProjectTests
{
    static string NewTempDir() => Path.Combine(Path.GetTempPath(), $"ap_{Guid.NewGuid():N}");

    [Fact]
    public void OpenOrCreate_creates_skeleton()
    {
        var dir = NewTempDir();
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.True(Directory.Exists(p.Directory));
            Assert.True(Directory.Exists(Path.Combine(p.Directory, "plans")));
            Assert.True(File.Exists(Path.Combine(p.Directory, "project.json")));
            Assert.True(File.Exists(Path.Combine(p.Directory, "README.md")));
            Assert.Equal(ProjectManifest.CurrentSchemaVersion, p.Manifest.SchemaVersion);
            Assert.Equal(p.Manifest.CreatedUtc, p.Manifest.LastOpenedUtc);
            Assert.Equal(Path.Combine(p.Directory, "sqlferret.duckdb"), p.DuckDbPath);
            Assert.Equal(Path.Combine(p.Directory, "exports"), p.ExportsFolder);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reopen_bumps_LastOpenedUtc_keeps_CreatedUtc()
    {
        var dir = NewTempDir();
        try
        {
            var first = AuditProject.OpenOrCreate(dir);
            var created = first.Manifest.CreatedUtc;
            Thread.Sleep(15);
            var second = AuditProject.OpenOrCreate(dir);
            Assert.Equal(created, second.Manifest.CreatedUtc);
            Assert.True(second.Manifest.LastOpenedUtc > first.Manifest.LastOpenedUtc);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Malformed_manifest_reinitializes_without_throwing()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), "{ broken ");
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(ProjectManifest.CurrentSchemaVersion, p.Manifest.SchemaVersion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Readme_not_overwritten_on_reopen()
    {
        var dir = NewTempDir();
        try
        {
            AuditProject.OpenOrCreate(dir);
            var readme = Path.Combine(Path.GetFullPath(dir), "README.md");
            File.WriteAllText(readme, "USER EDIT");
            AuditProject.OpenOrCreate(dir);
            Assert.Equal("USER EDIT", File.ReadAllText(readme));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void OpenDb_opens_database_at_project_path()
    {
        var dir = NewTempDir();
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            using var db = p.OpenDb();
            Assert.NotNull(db.Connection);
            Assert.True(File.Exists(p.DuckDbPath));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PlansFolder_defaults_inside_project()
    {
        var dir = NewTempDir();
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(Path.Combine(p.Directory, "plans"), p.PlansFolder);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PlansFolder_relative_config_resolved_against_project()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sqlferret.config.json"),
            """{ "server": { "plansFolder": "myplans" } }""");
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(Path.Combine(p.Directory, "myplans"), p.PlansFolder);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Project_config_wins_over_cwd_config()
    {
        var proj = NewTempDir();
        var cwd = NewTempDir();
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "sqlferret.config.json"),
            """{ "display": { "durationUnit": "us" } }""");
        File.WriteAllText(Path.Combine(proj, "sqlferret.config.json"),
            """{ "display": { "durationUnit": "s" } }""");
        try
        {
            var p = AuditProject.OpenOrCreate(proj, cwd);
            Assert.Equal("s", p.Config.DurationUnit);
        }
        finally { Directory.Delete(proj, true); Directory.Delete(cwd, true); }
    }

    [Fact]
    public void Project_env_wins_over_cwd_env()
    {
        var proj = NewTempDir();
        var cwd = NewTempDir();
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, ".env"), "SF_PREC_A=cwd\n");
        File.WriteAllText(Path.Combine(proj, ".env"), "SF_PREC_A=project\n");
        Environment.SetEnvironmentVariable("SF_PREC_A", null);
        try
        {
            AuditProject.OpenOrCreate(proj, cwd);
            Assert.Equal("project", Environment.GetEnvironmentVariable("SF_PREC_A"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SF_PREC_A", null);
            Directory.Delete(proj, true); Directory.Delete(cwd, true);
        }
    }

    [Fact]
    public void Real_env_wins_over_project_env()
    {
        var proj = NewTempDir();
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, ".env"), "SF_PREC_B=project\n");
        Environment.SetEnvironmentVariable("SF_PREC_B", "real");
        try
        {
            AuditProject.OpenOrCreate(proj);
            Assert.Equal("real", Environment.GetEnvironmentVariable("SF_PREC_B"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SF_PREC_B", null);
            Directory.Delete(proj, true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter AuditProjectTests`
Expected: FAIL — `AuditProject` does not exist (compile error).

- [ ] **Step 3: Write the minimal implementation**

Create `src/SqlFerret.Core/Project/AuditProject.cs`:

```csharp
using SqlFerret.Core.Config;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Project;

/// <summary>
/// A self-contained audit project directory: the DuckDB database, captured plans,
/// a provenance manifest, a static README, and project-local config/secrets.
/// Open or create one with <see cref="OpenOrCreate"/>; reopen by pointing at the same directory.
/// </summary>
public sealed class AuditProject
{
    public string Directory { get; }
    public string DuckDbPath { get; }
    public string PlansFolder { get; }
    public string ExportsFolder { get; }
    public SqlFerretConfig Config { get; }
    public ProjectManifest Manifest { get; }

    private static readonly string ToolVersion =
        typeof(AuditProject).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private AuditProject(string dir, SqlFerretConfig config, ProjectManifest manifest, string plansFolder)
    {
        Directory = dir;
        Config = config;
        Manifest = manifest;
        PlansFolder = plansFolder;
        DuckDbPath = Path.Combine(dir, "sqlferret.duckdb");
        ExportsFolder = Path.Combine(dir, "exports");
    }

    /// <summary>
    /// Creates the project skeleton (dir, plans/, project.json, README.md) if absent;
    /// otherwise reads the manifest and bumps LastOpenedUtc. Config and secrets resolve
    /// project-first, falling back to <paramref name="configFallbackDir"/> (the cwd in hosts).
    /// </summary>
    public static AuditProject OpenOrCreate(string projectDir, string? configFallbackDir = null)
    {
        var dir = Path.GetFullPath(projectDir);
        var fallback = configFallbackDir is null ? null : Path.GetFullPath(configFallbackDir);

        System.IO.Directory.CreateDirectory(dir);
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "plans"));

        var manifestPath = Path.Combine(dir, "project.json");
        var existing = ProjectManifest.TryRead(manifestPath);
        var now = DateTime.UtcNow;
        var manifest = existing is null
            ? new ProjectManifest(ProjectManifest.CurrentSchemaVersion, ToolVersion, now, now, null)
            : existing with { LastOpenedUtc = now };
        manifest.Write(manifestPath);

        var readmePath = Path.Combine(dir, "README.md");
        if (!File.Exists(readmePath)) File.WriteAllText(readmePath, ReadmeContent);

        // Secrets: project .env first, then cwd .env. DotEnv only sets absent keys, so the
        // effective precedence is: real OS env > project .env > cwd .env.
        DotEnv.Load(Path.Combine(dir, ".env"));
        if (fallback is not null) DotEnv.Load(Path.Combine(fallback, ".env"));

        // Config: project file, else cwd file, else built-in defaults.
        var projCfg = Path.Combine(dir, "sqlferret.config.json");
        var cwdCfg = fallback is null ? null : Path.Combine(fallback, "sqlferret.config.json");
        string? cfgPath = File.Exists(projCfg) ? projCfg
            : (cwdCfg is not null && File.Exists(cwdCfg) ? cwdCfg : null);
        var config = SqlFerretConfig.Load(cfgPath);

        var plans = Path.IsPathRooted(config.PlansFolder)
            ? config.PlansFolder
            : Path.GetFullPath(Path.Combine(dir, config.PlansFolder));

        return new AuditProject(dir, config, manifest, plans);
    }

    /// <summary>Opens the project's DuckDB database (creating its schema if new).</summary>
    public DuckDbProject OpenDb() => DuckDbProject.Open(DuckDbPath);

    private const string ReadmeContent = """
        # SqlFerret audit project

        This directory is a self-contained SqlFerret audit project. Reopen it by pointing
        SqlFerret at this directory with `--project <this-dir>`.

        ## Layout

        | Path | Role |
        |------|------|
        | `sqlferret.duckdb` | Embedded DuckDB database: normalized workload, blocking/deadlock reports, analysis tables. Open with the DuckDB CLI for ad-hoc SQL. |
        | `plans/` | Captured `*.sqlplan` execution plans (estimated / Query Store). Open in SSMS or Plan Explorer. |
        | `exports/` | Generated export packs (JSON/YAML + plans) for downstream / AI analysis. Created on demand. |
        | `project.json` | Project manifest — provenance maintained by SqlFerret. Do not edit by hand. |
        | `sqlferret.config.json` | Optional settings (display units, redaction policy, server connection). Edit to taste. |
        | `.env` | Optional project-local secrets (e.g. `${SQLFERRET_CONN}`). Gitignored. Real environment variables win over this file. |
        | `README.md` | This file. Written once at creation; safe to edit/extend. |

        ## project.json fields

        - `SchemaVersion` — version of the project-directory format (currently 1).
        - `ToolVersion` — SqlFerret version that created the project.
        - `CreatedUtc` — when the project was created.
        - `LastOpenedUtc` — when the project was last opened by SqlFerret.
        - `Notes` — free-form notes (optional).

        ## Notes

        - Durations and CPU times are stored in **microseconds** in the database; hosts format them for display.
        - Parameter values may be redacted per the project's redaction policy before being written to disk.
        """;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter AuditProjectTests`
Expected: PASS (10 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Core/Project/AuditProject.cs tests/SqlFerret.Core.Tests/AuditProjectTests.cs
rtk git commit -m "$(cat <<'EOF'
feat(core): AuditProject self-contained project directory

OpenOrCreate builds the skeleton (duckdb, plans/, project.json, README.md),
maintains the manifest, and resolves config/.env project-first (real env >
project .env > cwd .env). PlansFolder resolves relative to the project.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: CLI wiring + end-to-end test

**Files:**
- Modify: `src/SqlFerret.Cli/Program.cs` (full rewrite of the dispatch — shown below)
- Create: `tests/SqlFerret.Core.Tests/AuditProjectE2ETests.cs`

**Interfaces:**
- Consumes: `AuditProject.OpenOrCreate(string, string?)`, `AuditProject.OpenDb()`, `AuditProject.Config` (Task 2); existing `IngestionService`, `WorkloadQueries`, `ImportRunner`, `BlockingDigest`, `BlockingQueries`, `DisplayFormat`.
- Produces: CLI commands `import`, `top-slow`, `export-blocking` all requiring `--project <dir>`.

- [ ] **Step 1: Write the failing end-to-end test**

Create `tests/SqlFerret.Core.Tests/AuditProjectE2ETests.cs`:

```csharp
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Project;
using Xunit;

public class AuditProjectE2ETests
{
    [Fact]
    public void Open_project_then_ingest_then_query()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ape2e_{Guid.NewGuid():N}");
        try
        {
            var project = AuditProject.OpenOrCreate(dir);
            using var db = project.OpenDb();
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, []));
            var ev = new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
                new Dictionary<string, object?> { ["batch_text"] = "SELECT 1", ["duration"] = 10L },
                new Dictionary<string, object?>());
            svc.Ingest("logs/", [((IXeEventData)ev, "s_0.xel", 0L)]);

            var top = new WorkloadQueries(db.Connection).TopSlow(10, "total_duration_us", []);
            Assert.Single(top);
            Assert.True(File.Exists(project.DuckDbPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter AuditProjectE2ETests`
Expected: FAIL — compile error (`AuditProject` not yet referenced from tests is fine if Task 2 merged; if running this task in isolation the failure is the missing test passing against unbuilt wiring). If Task 2 is complete this test already compiles and passes — in that case treat Step 3 as the real deliverable and re-run in Step 5.

- [ ] **Step 3: Rewrite `src/SqlFerret.Cli/Program.cs`**

Replace the entire file with:

```csharp
// src/SqlFerret.Cli/Program.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Project;

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
    return AuditProject.OpenOrCreate(dir, Directory.GetCurrentDirectory());
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: import <path> --project <dir> | top-slow --project <dir> | export-blocking --project <dir> [--format json|md|both] [--samples N] [--full] [--out file]");
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
            if (outPath.Length > 0 && outPath.Contains(".."))
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
    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";

file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
```

- [ ] **Step 4: Build the whole solution (0 warnings)**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings. (Confirms the `DotEnv`/`SqlFerretConfig` top-level loads were fully removed and nothing else referenced them.)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS. Same skipped-count as before (the env-gated live-SQL test); no new failures. `AuditProjectE2ETests` passes.

- [ ] **Step 6: Manual verification against a real sample trace**

Run:

```bash
dotnet run --project src/SqlFerret.Cli -- import sample/performances_0_134262655313690000.xel --project /tmp/audit-demo
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/audit-demo --limit 5
```

Expected: the first command creates `/tmp/audit-demo/` containing `sqlferret.duckdb`, `plans/`, `project.json`, `README.md`, prints the `run N: read=… mapped=…` summary; the second reopens the same directory and prints up to 5 rows. Then confirm the required-flag guard:

```bash
dotnet run --project src/SqlFerret.Cli -- top-slow
```

Expected: prints `--project <dir> is required` and exits non-zero. (If the `sample/` trace is absent locally, substitute any `.xel` you have; the directory-creation and required-flag behavior are the point.)

- [ ] **Step 7: Format and commit**

```bash
dotnet format
rtk git add src/SqlFerret.Cli/Program.cs tests/SqlFerret.Core.Tests/AuditProjectE2ETests.cs
rtk git commit -m "$(cat <<'EOF'
feat(cli): drive commands from AuditProject; --project is a required directory

import/top-slow/export-blocking now resolve --project <dir> to an AuditProject
(skeleton created on first use), reading config/secrets project-first. Breaking:
--project no longer accepts a .duckdb file path.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**Spec coverage:**
- Layout (duckdb, plans/, exports/, project.json, README.md, config, .env) → Task 2 skeleton + README const + properties. ✓
- `--project` required directory, no default → Task 3 `OpenProject` guard + manual Step 6. ✓
- Breaking (no legacy file mode) → Task 3 (file path no longer used). ✓
- Project-first config + `.env` precedence (real env wins) → Task 2 tests `Project_config_wins_over_cwd_config`, `Project_env_wins_over_cwd_env`, `Real_env_wins_over_project_env`. ✓
- `PlansFolder` relative to project → Task 2 tests `PlansFolder_defaults_inside_project`, `PlansFolder_relative_config_resolved_against_project`. ✓
- `AuditProject` API (Directory, DuckDbPath, PlansFolder, ExportsFolder, Config, Manifest, OpenOrCreate, OpenDb) → Task 2. ✓
- `ProjectManifest` SchemaVersion=1, tool-maintained, malformed→reinit, README write-once → Tasks 1 & 2. ✓
- KISS / no DI / no interface → sealed class + record + static factory. ✓
- Secrets in `.env`, real env wins → Task 2 precedence + tests. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; README content is literal. ✓

**Type consistency:** `OpenOrCreate(string, string?)`, `OpenDb()`, `ProjectManifest.TryRead/Write/CurrentSchemaVersion`, and property names (`Directory`, `DuckDbPath`, `PlansFolder`, `ExportsFolder`, `Config`, `Manifest`) are used identically across Tasks 2 and 3 and the tests. ✓

## Out of scope (per spec)

Query Store extraction, AI export packs, the MCP host, and TUI adoption of `AuditProject` — each is a separate downstream effort.
