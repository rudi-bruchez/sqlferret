# SQLFerret — project guide for Claude

SQL Server `.xel` Extended Events analyzer. A headless **`SqlFerret.Core`** engine plus thin hosts:
ingest a `.xel` file or a `logs/` folder → normalize queries → store in an embedded DuckDB project
file → analyze the workload → optionally capture estimated execution plans.

## Solution layout

```
sqlferret.sln                      (classic .sln, net10.0 throughout)
src/SqlFerret.Core/   class library — the whole engine (headless, testable)
src/SqlFerret.Cli/    console host  — `import` + `top-slow` commands
tests/SqlFerret.Core.Tests/  xUnit
```

Core namespaces (layered by responsibility, one-directional deps):
`Model` ← `Normalization` / `Parameters` / `Filtering` ← `Ingestion` / `Storage` / `Analysis` / `Replay` / `Server` ← (`Cli` host).
Future hosts (Plan 2: Terminal.Gui TUI, later Avalonia) sit over the **same Core** — keep Core host-agnostic.

## Tech stack

- **.NET 10 / C# 14** (`net10.0`, Nullable + ImplicitUsings, LangVersion latest)
- **DuckDB.NET.Data.Full** 1.5.3 — embedded analytics store (the on-disk `.duckdb` project file)
- **Microsoft.SqlServer.XEvent.XELite** 2024.2.5.1 — the only robust cross-platform `.xel` reader (push/async)
- **Microsoft.SqlServer.TransactSql.ScriptDom** 180.37.3 — T-SQL token stream (normalization) + minimal AST (classify)
- **Microsoft.Data.SqlClient** 7.0.1 — `SET SHOWPLAN_XML ON` estimated plans (compile-only)
- **xUnit** + **Xunit.SkippableFact** — fixture/server-gated tests

## Architecture: KISS (binding — spec §2)

This is the user's explicit, non-negotiable preference. **Do not** introduce:
repository/unit-of-work, onion/Clean/DDD layering, `IXxxService` interfaces (unless a real second
impl exists), AutoMapper/MediatR/CQRS, or a DI container. Plain `record`/POCO DTOs, static utility
classes, primary-constructor services. **Aggregation lives in DuckDB SQL** (`WorkloadQueries`),
never hand-built C# reduction loops. The *only* abstraction in Core is `IXeEventData` (real XELite
event + test fakes are two genuine implementations).

## Hard invariants (enforce in every change & review)

- **Microseconds everywhere in Core.** Durations/CPU stored as `*_us` (`long`); formatting to ms/s
  happens only in hosts via `DisplayFormat`. Never convert units inside Core.
- **Secrets in `.env` only.** DB connection strings / LLM keys via `${ENV_VAR}` interpolation
  (`DotEnv` + `SqlFerretConfig`). Never hardcode or commit secrets. `.env` is gitignored;
  `.env.example` is committed. Real environment wins over `.env`; a missing `.env` is a silent no-op.
- **Redaction before any parameter value is written to disk.** `RedactionPolicy` (off/hash/masked/full
  + per-name sensitive overrides) is applied in `IngestionService` before building `PreparedParameter`.
- **Nothing silently dropped.** Unmapped / tokenize-failed / ingest-cleaned events are all counted in
  `ingestion_runs`. Counters must stay mutually exclusive and exhaustive.
- **`QueryNormalizer.Version = 1`**, persisted on both `ingestion_runs` and `normalized_queries`.
- **SQL safety.** User free-text (hashes, names, ids, limits) must be **bound parameters** (`$name`);
  only allow-listed identifiers (`FilterCompiler.AllowedFields`, `WorkloadQueries` sort/dim lists) may
  be interpolated. `FilterCompiler` escapes string values by doubling single quotes. `planId` for
  `.sqlplan` files must reject path traversal. SHOWPLAN_XML stays compile-only (no destructive re-run).

## Modern C# 14 baseline

Primary constructors for stateful services; collection expressions `[]` (not `new[]{}`/`Array.Empty`);
`record` over tuples for multi-field values; raw string literals `"""` for SQL; `required`/`init` on
DTOs; switch/`is` pattern matching where it reads cleanly. A bare `catch` is acceptable *only* on
deliberate fallback/defaults paths (e.g. parse-failure → fallback, malformed JSON → empty state).

## Sensitive sample data

`sample/` holds **real production `.xel` captures** (≈270 MB, real SQL text / literals / PII) and is
**gitignored — never commit it.** Real-life integration tests (`XelReaderTests`,
`EstimatedPlanServiceTests`) discover it locally via `[SkippableFact]` and **skip cleanly when absent**
(so CI stays green). There is no committed binary fixture and no container is required.

## Build / test / run

```bash
dotnet build                                  # 0 warnings expected
dotnet test                                   # full suite (1 skipped = env-gated live-SQL test)
dotnet test --filter <TestClassName>          # focused
# headless end-to-end against a local sample trace (writes to /tmp, gitignored):
dotnet run --project src/SqlFerret.Cli -- import sample/performances_0_134262655313690000.xel --project /tmp/wl.duckdb
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/wl.duckdb --limit 20
```

For the live estimated-plan integration test, set `SQLFERRET_TEST_CONN` (a SQL Server connection
string) in your environment / `.env`.

## Workflow conventions

- **TDD**: red → green → commit per change. Tests assert real behavior; output stays pristine.
- **Formatting**: `.editorconfig` is the style baseline; run `dotnet format` before committing.
- `git` is wrapped with `rtk` per the user's global setup; commits are co-authored. Branch off `main`;
  never commit on `main` without consent. Do not commit `.duckdb` files, `plans/`, `.env`, or `sample/`.

## Status

**Plan 1 (Core engine) is complete and merged to `main`** — full ingestion pipeline, DuckDB storage,
`WorkloadQueries` analysis, `ReplayBuilder`, `EstimatedPlanService`, config, and the `SqlFerret.Cli`
host. See `docs/superpowers/specs/` (design) and `docs/superpowers/plans/` (the 20-task plan).

**Deferred to Plan 2 (TUI over the same Core):** the Terminal.Gui front end; hot-path perf (share one
ScriptDom parse across `TokenNormalizer` + `AstClassifier` — currently ~3 parses/event);
`ingestion_runs.files_count`/`bytes_total` accuracy for folder imports; culture-invariant
`ParameterExtractor.GuessType`; sealing services; broader `UiState` round-trip assertions.
