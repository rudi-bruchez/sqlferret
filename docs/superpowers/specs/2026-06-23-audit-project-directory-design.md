# Audit Project Directory — design

**Date:** 2026-06-23
**Status:** Approved (brainstorming) — ready for implementation plan
**Branch target:** new branch off `main`

## Problem

Today a SqlFerret "project" is three loosely-coupled things anchored to the current
working directory:

- `--project` is a **single file path** (`workload.duckdb`), default `workload.duckdb` in cwd
  (`Program.cs:34`).
- `.sqlplan` files go to a **separately configured** `PlansFolder`, default `./plans`
  (`SqlFerretConfig.cs:11,28`), unrelated to the `.duckdb` location.
- `sqlferret.config.json` and `.env` are read from the **cwd** (`Program.cs:9-10`).

There is no single, transportable unit for an audit. You cannot create an audit, archive it,
move it, or reopen it by pointing at one place. This blocks the planned multi-project workflow
(Query Store extraction, AI export packs, an MCP host) — each of those needs a stable place to
read from and write to.

## Goal

Introduce a self-contained **audit project directory**: SqlFerret creates a directory holding
the DuckDB database, the `.sqlplan` files, a provenance manifest, and (optionally) project-local
config and secrets. Reopening an audit is `--project <dir>`. The directory is the project.

Downstream features (Query Store import/export, MCP host) will branch off this foundation, each
with its own spec.

## Decisions (locked during brainstorming)

1. **`--project` is a directory, not a file. Breaking change accepted** (tool is unreleased). The
   old `--project foo.duckdb` form is dropped — no dual-mode, no auto-migration.
2. **Project-local config and secrets take precedence** over cwd, so an audit is portable.
   The "real environment wins" invariant is preserved.

## On-disk layout

```
mon-audit/
  sqlferret.duckdb           # the DuckDB database (fixed name)
  plans/                     # the .sqlplan files (created on demand)
  exports/                   # AI export packs — future feature (created on demand)
  project.json               # manifest: provenance, maintained by the tool
  sqlferret.config.json      # settings (optional; falls back to ./ then defaults)
  .env                       # project-local secrets (optional, gitignored)
```

Two distinct files by design:

- **`project.json`** — provenance **maintained by the tool**, never hand-edited.
- **`sqlferret.config.json`** — user-edited **settings**, shape unchanged (reuses the existing
  `SqlFerretConfig` parser verbatim).

KISS: one role per file.

## New Core abstraction — `AuditProject`

New namespace `SqlFerret.Core.Project` (depends on `Config` + `Storage`; sits just below the
hosts in the dependency layering).

```csharp
namespace SqlFerret.Core.Project;

public sealed class AuditProject
{
    public string Directory { get; }
    public string DuckDbPath { get; }      // <dir>/sqlferret.duckdb
    public string PlansFolder { get; }     // <dir>/plans
    public string ExportsFolder { get; }   // <dir>/exports
    public SqlFerretConfig Config { get; }
    public ProjectManifest Manifest { get; }

    // Creates the skeleton (<dir>, plans/, project.json) if absent;
    // otherwise loads the manifest and bumps LastOpenedUtc.
    public static AuditProject OpenOrCreate(string dir);

    // Convenience: DuckDbProject.Open(DuckDbPath).
    public DuckDbProject OpenDb();
}

public record ProjectManifest(
    int SchemaVersion,
    string ToolVersion,
    DateTime CreatedUtc,
    DateTime LastOpenedUtc,
    string? Notes);
```

### `OpenOrCreate(dir)` behavior

- Resolve `dir` to an absolute path.
- If the directory does not exist: create it and `plans/`, write an initial `project.json`
  (`SchemaVersion = 1`, `ToolVersion` from the assembly version, `CreatedUtc = LastOpenedUtc =
  DateTime.UtcNow`, `Notes = null`). `exports/` is created lazily by the future export feature,
  not here.
- If it exists: read `project.json`, then update `LastOpenedUtc = DateTime.UtcNow` and persist.
  A missing/malformed `project.json` in an existing directory is treated as a fresh project
  (re-initialized manifest) rather than a hard error — deliberate fallback path, bare `catch`
  acceptable per the C# baseline.
- Load config and secrets per the precedence rules below; expose the resulting
  `SqlFerretConfig` as `Config`.

`ProjectManifest.SchemaVersion` starts at `1` (independent of `QueryNormalizer.Version`); it
versions the project-directory format so future layout changes are detectable.

## Config & secrets resolution (project-first)

- **Config:** `<dir>/sqlferret.config.json` if present; else `./sqlferret.config.json` (cwd);
  else built-in defaults. Parsed by the existing `SqlFerretConfig.Load` — no change to its shape.
- **Secrets:** load `<dir>/.env` **first**, then `./.env`. Because `DotEnv.Load` only sets a key
  when it is absent, the resulting precedence is exactly:
  **real OS env > project `.env` > cwd `.env`**. No change to `DotEnv` required.
- **`PlansFolder`:** default `<dir>/plans`. If `sqlferret.config.json` supplies a relative
  `plansFolder`, it is resolved **relative to the project directory** (never the cwd). An absolute
  configured path is honored as-is.

## CLI wiring (`Program.cs`)

- `--project` now names a **directory**; default `.` (the cwd becomes the project).
- The top-of-file `DotEnv.Load` / `SqlFerretConfig.Load` from cwd move under `AuditProject`'s
  control: after parsing `--project`, call `AuditProject.OpenOrCreate(dir)`, then drive
  everything from it.
- `import`, `top-slow`, and `export-blocking` switch to `project.OpenDb()` and
  `project.PlansFolder` / `project.Config` instead of the loose `--project` file path and
  cwd-relative config.
- Usage string updated to reflect the directory semantics.

## Testing (TDD, no server, no populated DuckDB)

`AuditProject` is pure path/IO logic — tested against a temp directory:

- `OpenOrCreate` on an absent dir creates the skeleton (`plans/`, `project.json` with
  `SchemaVersion = 1` and sane manifest timestamps).
- Reopening reads the manifest back and bumps `LastOpenedUtc` (and never resets `CreatedUtc`).
- An existing dir with missing/malformed `project.json` re-initializes cleanly (no throw).
- Config precedence: `<dir>/sqlferret.config.json` wins over `./sqlferret.config.json`.
- `.env` precedence: a key in `<dir>/.env` wins over the same key in `./.env`; a real
  environment variable wins over both.
- `PlansFolder` resolves relative to the project directory (relative configured path; default).
- Smoke: `OpenDb()` opens `<dir>/sqlferret.duckdb`.

## Out of scope (deliberately)

- Query Store extraction, AI export packs, the MCP host — each is a separate downstream spec
  that branches off this foundation.
- Backward compatibility with the old `--project foo.duckdb` file form (breaking change accepted).
- TUI host wiring beyond what is needed to keep it compiling (the TUI's project/path handling can
  adopt `AuditProject` in its own follow-up if needed).

## Hard-invariant check

- **Microseconds in Core** — unaffected (no duration/CPU handling here).
- **Secrets in `.env` only** — reinforced: project `.env` is gitignored; precedence keeps real env
  winning; nothing hardcoded.
- **SQL safety** — unaffected (no new SQL). `DuckDbProject.Open` path is derived, not interpolated
  into SQL.
- **KISS** — plain `record`/sealed class, static factory, no DI/interfaces (single concrete
  `AuditProject`, no `IAuditProject`).
