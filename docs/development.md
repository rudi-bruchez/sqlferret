# Development

## Build and test

```bash
dotnet build          # zero warnings expected
dotnet test           # 387 tests: 377 pass, 10 skip
dotnet format         # run before committing; .editorconfig is the style baseline
```

Focus a single class:

```bash
dotnet test --filter QueryNormalizerTests
dotnet test --filter FullyQualifiedName~Plan
```

### About the skipped tests

Ten tests are environment-gated and skip cleanly rather than failing. That is by design, so a
fresh clone runs green with no setup.

| Gate | Tests |
|---|---|
| A local `sample/` folder with real `.xel` captures | `XelReaderTests`, `BlockingQueriesTests.Real_blocking_xel…`, `ImportRunnerTests`, `CliSmokeTests`, `ImportPresenterTests`, `ImportViewTests` |
| `SQLFERRET_TEST_CONN` pointing at a live SQL Server | `EstimatedPlanServiceTests`, `QueryStoreImportServiceTests` |

They use `Xunit.SkippableFact`. To run the live-server ones:

```bash
export SQLFERRET_TEST_CONN='Server=localhost;Database=tempdb;User ID=sa;Password=…;TrustServerCertificate=True'
dotnet test --filter EstimatedPlanServiceTests
```

## Sample data

`sample/` holds real production `.xel` captures — around 270 MB of genuine SQL text, literal
values and PII. It is **gitignored and must never be committed.**

There is no committed binary fixture and no container is required. Tests that want real data
discover `sample/` locally and skip when it is absent. If you have a capture of your own, drop it
in `sample/` and those tests light up.

The same rule covers `exemples-plans/`, any `*.sqlplan`, `*.duckdb` files, the root `plans/`
folder and `.env`. All are gitignored. Check `git status` before committing after an end-to-end
run.

## Running end to end

```bash
# against your own local capture, writing outside the repo
dotnet run --project src/SqlFerret.Cli -- import sample/trace_0.xel --project /tmp/wl
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/wl --limit 20
dotnet run --project src/SqlFerret.Tui -- /tmp/wl
```

## Conventions

### TDD

Red, green, commit, one change at a time. Tests assert real behavior rather than restating the
implementation, and test output stays clean — a test that prints noise on success is a test that
will be ignored when it starts failing.

### Style

`.editorconfig` is the baseline; run `dotnet format` before committing.

Language baseline is C# 14 on `net10.0`, with `Nullable` and `ImplicitUsings` on:

- Primary constructors for stateful services
- Collection expressions `[]`, not `new[]{}` or `Array.Empty<T>()`
- `record` over tuples once there is more than a pair of fields
- Raw string literals `"""` for embedded SQL
- `required` / `init` on DTOs
- Pattern matching where it beats an if-chain

A bare `catch` is acceptable only on a deliberate fallback path — parse failure to a default,
malformed JSON to empty state, a best-effort provenance write. Comment why, at the site.

### Architecture rules

Read [architecture.md](architecture.md#design-rules) before adding anything structural. The
short list of things that will be rejected: repository/unit-of-work patterns, Clean/onion/DDD
layering, `IXxxService` interfaces without a second implementation, AutoMapper, MediatR, CQRS,
and a DI container.

Aggregation belongs in DuckDB SQL, not in C# reduction loops.

### Invariants to preserve

Every change should be checked against these:

1. **Microseconds everywhere in Core.** `*_us`, `long`. Formatting to ms or s happens only in
   hosts, via `DisplayFormat`.
2. **Secrets in `.env` only**, referenced as `${VAR}`. Never hardcoded, never committed.
3. **Redaction before any parameter value reaches disk.** Applied in `IngestionService`, not later.
4. **Nothing silently dropped.** New event handling means a new counter on `ingestion_runs`, and
   the counters must stay mutually exclusive and exhaustive.
5. **`QueryNormalizer.Version`** is persisted on both `ingestion_runs` and `normalized_queries`.
   Changing normalization rules means bumping it.
6. **SQL safety.** User free text is bound (`$name`); only allow-listed identifiers are
   interpolated. `planId` values must reject path traversal. `SET SHOWPLAN_XML ON` stays
   compile-only.

### Git

Branch off `main`. Do not commit to `main` without consent. Commits are co-authored.

Never commit: `.duckdb` files, `plans/`, `.env`, `sample/`, `exemples-plans/`, `*.sqlplan`.

## Project layout

```text
src/SqlFerret.Core/
├── Model/            plain records, no behavior
├── Normalization/    ScriptDom tokenizer, AST classifier, fingerprint
├── Parameters/       extraction + redaction policy
├── Filtering/        filter rules and their SQL/predicate compiler
├── Ingestion/        XELite reading, event routing, ImportRunner, progress
├── Storage/          DuckDbProject (partial: .cs, .Plans, .QueryStore, .Obfuscation)
├── Analysis/         WorkloadQueries, BlockingQueries, BlockingDigest, EventExport
├── Plans/            plan identity, parser, findings, artifact writer
├── Obfuscation/      identifier map, plan and statement rewriters
├── Server/           the only SqlConnection users: estimated plans, Query Store
├── Replay/           captured execution → runnable batch
├── Config/           DotEnv, SqlFerretConfig, DisplayFormat, UiState
└── Project/          AuditProject, ProjectManifest

src/SqlFerret.Cli/    Program.cs switch + BlockingDigestMarkdown renderer
src/SqlFerret.Tui/    Shell/, Views/, Presenters/, Clipboard/
```

## Known gaps

Tracked, deliberate, and open to contribution:

- **Hot-path parsing.** `TokenNormalizer` and `AstClassifier` each build their own ScriptDom
  parse, so roughly three parses run per event. Sharing one parse across both would be the single
  biggest ingestion speedup available.
- **`ingestion_runs.files_count` / `bytes_total`** are not accurate for folder imports.
- **`ParameterExtractor.GuessType`** is not culture-invariant.
- **Services are not sealed.** Most of them could be.
- **`UiState` round-trip assertions** are thinner than they should be.
- **Plan-to-execution correlation** is deferred. It needs no new code, only a capture that
  includes `sqlserver.query_hash`; see [capture-session.md](capture-session.md).
- **`top-slow` cannot change its sort column** from the CLI, although the underlying query
  supports four. The TUI exposes them.
- **No `--help`.** Running the CLI bare prints a usage line; [cli-reference.md](cli-reference.md)
  is the reference.
- **No deadlock digest.** Graphs are stored and exportable, but not summarized the way blocking is.
- **Redaction and blocking XML retention are coupled** through a single policy value, so you
  cannot have redacted parameters and retained blocking XML in one project. See
  [privacy.md](privacy.md#the-sharp-edge-in-off).

## Dependencies, and why each one is there

| Package | Version | Why |
|---|---|---|
| `DuckDB.NET.Data.Full` | 1.5.3 | Embedded analytics engine. Columnar, no server, real window functions and quantiles. |
| `Microsoft.SqlServer.XEvent.XELite` | 2024.2.5.1 | The only robust cross-platform `.xel` reader. Push/async API. |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.37.3 | Real T-SQL token stream for normalization, plus a minimal AST for classification. |
| `Microsoft.Data.SqlClient` | 7.0.1 | Query Store reads and `SET SHOWPLAN_XML ON`. |
| `Terminal.Gui` | 2.4.6 | TUI host only; Core does not reference it. |
| `xunit` + `Xunit.SkippableFact` | 2.9.3 / 1.5.61 | Tests, with clean environment gating. |

One gotcha worth knowing: **DuckDB.NET 1.5.3 parameter names must not include the leading `$`.**
The SQL placeholder is `$name` but the parameter collection matches on `name`. Every `Add` helper
in the codebase does `name.TrimStart('$')` for exactly this reason.
