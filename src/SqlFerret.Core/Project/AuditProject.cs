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

    /// <summary>True when project.json existed but was unreadable (corrupt/null); provenance was reset.</summary>
    public bool RecoveredFromCorruptManifest { get; }

    private static string ToolVersion =>
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AuditProject).Assembly)
            .GetName().Version?.ToString() ?? "0.0.0";

    private AuditProject(string dir, SqlFerretConfig config, ProjectManifest manifest,
        string plansFolder, string exportsFolder, bool recoveredFromCorruptManifest)
    {
        Directory = dir;
        Config = config;
        Manifest = manifest;
        PlansFolder = plansFolder;
        ExportsFolder = exportsFolder;
        DuckDbPath = Path.Combine(dir, "sqlferret.duckdb");
        RecoveredFromCorruptManifest = recoveredFromCorruptManifest;
    }

    /// <summary>
    /// Creates the project skeleton (dir, plans/, exports/, project.json, README.md) if absent;
    /// otherwise reads the manifest and bumps LastOpenedUtc. Config and secrets resolve
    /// project-first, falling back to <paramref name="configFallbackDir"/> (the cwd in hosts).
    /// </summary>
    public static AuditProject OpenOrCreate(string projectDir, string? configFallbackDir = null)
    {
        var dir = Path.GetFullPath(projectDir);

        // Item 4: reject if a regular file already exists at the path.
        if (File.Exists(dir))
            throw new IOException($"--project must be a directory, but a file exists at: {dir}");

        var fallback = configFallbackDir is null ? null : Path.GetFullPath(configFallbackDir);

        System.IO.Directory.CreateDirectory(dir);

        var manifestPath = Path.Combine(dir, "project.json");
        var existing = ProjectManifest.TryRead(manifestPath);
        // Item 5: detect corrupt manifest (file present but unreadable).
        bool corrupt = File.Exists(manifestPath) && existing is null;
        var now = DateTimeOffset.UtcNow;
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
        System.IO.Directory.CreateDirectory(plans);

        // Item 8: create exports/ eagerly (symmetry with plans/).
        var exports = Path.Combine(dir, "exports");
        System.IO.Directory.CreateDirectory(exports);

        return new AuditProject(dir, config, manifest, plans, exports, corrupt);
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
