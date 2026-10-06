# SQLFerret — project guide for Claude

Cross-platform SQL Server workload analyzer. A headless **`SqlFerret.Core`** engine plus thin
hosts: ingest `.xel` Extended Events → normalize and classify queries → store in a DuckDB project
directory → analyze workload, blocking and execution plans → optionally snapshot Query Store.

User-facing documentation lives in **`docs/`** and is the reference for behavior, formats and
commands. This file is the working agreement for the agent.

---

# A. Repository facts

## Solution layout

```
sqlferret.sln                       classic .sln, net10.0 throughout
src/SqlFerret.Core/                 class library — the whole engine (headless, testable)
src/SqlFerret.Cli/                  console host
src/SqlFerret.Tui/                  Terminal.Gui host
tests/SqlFerret.Core.Tests/         xUnit
tests/SqlFerret.Tui.Tests/          xUnit
docs/                               user + contributor documentation (indexed by docs/README.md)
.agents/skills/                     agent skills, cross-runtime
```

CLI commands, nine of them: `import`, `top-slow`, `query`, `reclassify`, `export-blocking`,
`export-events`, `export-health`, `query-store-import`, `obfuscate-plan`. Flags and exit codes:
`docs/cli-reference.md`.

Core namespaces, one-directional deps:
`Model` ← `Normalization` / `Parameters` / `Filtering` ← `Ingestion` / `Storage` / `Analysis` /
`Plans` / `Obfuscation` / `Replay` / `Server` ← hosts (`Cli`, `Tui`).
`Config` and `Project` are cross-cutting and host-agnostic.

**Core must stay host-agnostic.** It references no UI framework and knows nothing about a
terminal. Both hosts are thin shells over `AuditProject`, `ImportRunner`, `WorkloadQueries`,
`AdHocQuery` and the digest classes.

## Agent skills

Skills live under **`.agents/skills/`**, the cross-runtime location, and are committed. For Claude
Code, link them into a runtime skills directory rather than copying one — a copy goes stale as the
tool evolves. Inside a clone, `.claude/skills` is the conventional place for that link; it is
gitignored, so create it once after cloning if you want the skills picked up here. `README.md` has
the command for both Unix and Windows.

Two skills ship with the repository: `analyzing-xel-workloads` (how to analyze a trace without
drawing confidently wrong conclusions — written in French) and `modern-csharp`.

## Tech stack

- **.NET 10 / C# 14** (`net10.0`, Nullable + ImplicitUsings, LangVersion latest)
- **DuckDB.NET.Data.Full** 1.5.3 — embedded analytics store
- **Microsoft.SqlServer.XEvent.XELite** 2024.2.5.1 — cross-platform `.xel` reader (push/async)
- **Microsoft.SqlServer.TransactSql.ScriptDom** 180.37.3 — T-SQL token stream + AST (classify)
- **Microsoft.Data.SqlClient** 7.0.1 — Query Store reads, `SET SHOWPLAN_XML ON` (compile-only)
- **Terminal.Gui** 2.4.6 — TUI host only
- **xUnit** + **Xunit.SkippableFact** — environment-gated tests

Gotcha: DuckDB.NET 1.5.3 parameter names must **not** carry the leading `$`. SQL uses `$name`,
the parameter collection matches `name`. Every `Add` helper does `name.TrimStart('$')`.

## Project directory

`--project` is a **directory**, not a file. It is created on first use and contains
`sqlferret.duckdb`, `project.json`, `plans/`, `exports/`, a generated `README.md`, and optionally
`sqlferret.config.json` and `.env`. See `docs/configuration.md`.

## Build / test / run

```bash
dotnet build                                  # 0 warnings expected (verified)
dotnet test                                   # 680 tests, up to 11 skip (see below)
dotnet test --filter <TestClassName>          # focused
dotnet format <path>                          # style; .editorconfig is the baseline
```

Up to 11 skips are expected, not a regression (4 on a machine holding `sample/`). Two distinct gates:

- **`sample/` present** — `XelReaderTests`, `BlockingQueriesTests`, `CliSmokeTests`,
  `CliQueryCommandTests`, `ImportRunnerTests`, `ServerDiagnosticsIngestionTests`,
  `ImportPresenterTests`, `ImportViewTests`
- **`SQLFERRET_TEST_CONN` set** (a SQL Server connection string) —
  `EstimatedPlanServiceTests`, `QueryStoreImportServiceTests`

End-to-end against a local capture (writes outside the repo):

```bash
dotnet run --project src/SqlFerret.Cli -- import sample/trace_0.xel --project /tmp/wl
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/wl --limit 20
dotnet run --project src/SqlFerret.Tui -- /tmp/wl
```

**CI runs on push to `main` and on every pull request.** `.github/workflows/ci.yml`, on
`ubuntu-latest`: restore, `dotnet format --verify-no-changes`, `dotnet build -warnaserror`,
`dotnet test`. It is the gate an ordinary change actually passes through, and it is not a
substitute for running the same commands locally first.

One trap, documented in the workflow itself: the format check fails with thousands of
`ENDOFLINE` errors on a Windows working copy. `.editorconfig` asks for `lf`, every blob in the
index is `lf`, and there is no `.gitattributes` — so a Windows clone with the default
`core.autocrlf=true` writes CRLF into the working tree. The runner checks out `lf` and agrees.
Set `core.autocrlf=false` and re-checkout if you need the check to pass here; never "fix" it by
committing CRLF.

The second workflow, `.github/workflows/release.yml`, fires on a `v*` tag: it checks the tag
against `Directory.Build.props`, runs the suite, cross-publishes the five RIDs from a single
Linux runner and smoke-tests the linux-x64 archive before publishing.

## Running the tool on a real trace

- The binary is **not on the `PATH`**: `src/SqlFerret.Cli/bin/Debug/net10.0/SqlFerret.Cli.exe`,
  or `dotnet run --project src/SqlFerret.Cli -- …`.
- **`--help` is not recognized.** With no argument, the tool prints its usage line.
- `--redaction off` at import when the analysis needs the real SQL text and parameter values. It
  is also the only policy that retains raw blocking/deadlock XML.
- For any ad-hoc SQL on a project: **`sqlferret query`**. Do not hand-roll a harness that loads
  `DuckDB.NET.Data.dll` — the native DLL does not sit next to the managed one, and that detour has
  already produced its own bugs.
- A project imported by an older normalizer is upgraded in place by
  `sqlferret reclassify --project <dir>`, without re-importing.
- `query` duration formatting follows the **output column name**: `SELECT duration_us AS duration`
  returns raw microseconds. Keep the suffix (`AS total_us`) to keep the formatting, or pass
  `--raw` to never get it. The suffix converts **any** numeric value in that column whatever its
  SQL type — `sum()` yields a HUGEINT and `avg()` a DOUBLE — so two `_us` columns on one row are
  always in the same unit.
- `query` applies a **1000-row limit by default**; `--limit <n>` changes it, `--no-limit` lifts
  it. Truncation is announced on `stderr` — do not pipe that away.
- The four formats split into two families: `csv` and `json` are **faithful** (newlines and types
  preserved, `json` stays typed down to a HUGEINT); `table` and `md` are **presentation** formats,
  one record per line, where a `sql_text_raw` newline comes out as `\n`. To replay SQL, take `csv`
  or `json`.

## Sensitive data — non-negotiable

`sample/` is where you drop your own `.xel` captures, and those are **production data**: real SQL
text, literals, PII. It is gitignored and **must never be committed**. Same for `exemples-plans/`,
`*.sqlplan`, `*.duckdb`, `plans/` (root), and `.env`.

There is no committed binary fixture and no container is required. Check `git status` after any
end-to-end run.

**The test fixtures are anonymous, and must stay that way.** They use `SampleApp`, `AppSchema`,
`AppDb`, `WidgetRecalc`, `WidgetScaling`, `@WidgetId`, `@GadgetCode`, `@TenantId`, `@Code`. Never
paste a real schema, table, procedure, column or parameter name from a capture into a test, and
never a real literal value. When a bug needs a real-world shape, invent one in the same
vocabulary.

---

# B. Architecture and code invariants

## KISS — deliberate, and enumerated

**Do not introduce**: repository/unit-of-work, onion/Clean/DDD layering, `IXxxService` interfaces
(unless a real second implementation exists), AutoMapper/MediatR/CQRS, or a DI container.

Use instead: plain `record`/POCO DTOs, static utility classes, primary-constructor services newed
up at the call site. The **only** abstraction in Core is `IXeEventData` — the real XELite event
and the test fakes are two genuine implementations.

**Aggregation lives in DuckDB SQL** (`WorkloadQueries`, `BlockingQueries`), never in hand-built
C# reduction loops.

If a task appears to require one of the forbidden patterns, **stop and ask.** Do not work around
the rule with an equivalent in disguise; do not silently drop the requirement either.

## Invariants

Hold these in code you write or modify.
If you observe a violation in code the task does not touch, **report it and move on** — see C.1.

- **Microseconds everywhere in Core.** Durations/CPU/waits stored as `*_us` (`long`). Formatting
  to ms/s happens only in hosts, via `DisplayFormat`. Never convert units inside Core.
- **Secrets in `.env` only**, referenced as `${ENV_VAR}` (`DotEnv` + `SqlFerretConfig`). Never
  hardcode, never commit. `.env` gitignored, `.env.example` committed. Real environment wins over
  `.env`; a missing `.env` is a silent no-op.
- **Redaction before any parameter value reaches disk.** `RedactionPolicy` (off/hash/masked/full,
  plus per-name sensitive overrides) is applied in `IngestionService` before `PreparedParameter`
  is built. Note: `off` discards parameter values *and* is a necessary condition for retaining raw
  blocking/deadlock XML. Redaction covers parameter values; `--sanitize-sql-text` covers statement
  text (`sql_text_raw` / `normalized_sql`). They are independent for parameters, but **compose for
  statement text**: an input buffer is statement text, so `blocking_processes.inputbuf`,
  `blocking_reports.raw_xml` and `deadlock_reports.graph_xml` are kept verbatim only under
  `off` *and* `raw` — `IngestionService.VerbatimStatementTextAllowed`, mirrored by the
  `InputBufIsRaw` predicate in `Reclassifier`. The two must move together.
  See `docs/privacy.md`.
- **Nothing silently dropped.** Unmapped, tokenize-failed, ingest-cleaned, blocking, deadlock,
  plan-profile and server-diagnostics events are all counted on `ingestion_runs`. Counters stay
  mutually exclusive and exhaustive, and their sum equals `events_read`. A new event type means a
  new counter.
  Two columns count **sub-documents, not events**, and sit outside that sum:
  `server_diagnostics_embedded_blocking` and `server_diagnostics_embedded_blocking_failures`, for
  the `blocked-process-report` elements lifted out of a diagnostics cycle. Adding them to the
  reconciliation query would make it exceed `events_read`.
- **`QueryNormalizer.Version = 4`**, persisted on both `ingestion_runs` and `normalized_queries`.
  It versions **normalization and classification together**: bump it whenever `AstClassifier`
  returns a different answer for a given input, not only when the token rewriting changes.
  Otherwise already-imported projects silently keep their old classification and
  `HasStaleClassification()` never offers `reclassify`. Fingerprints across versions are not
  comparable.
- **`SqlTextSanitizer.Version = 1`**, persisted on `ingestion_runs.sql_text_sanitizer_version`.
  Changing what the sanitizer stores requires bumping it. It is coupled to
  `QueryNormalizer.Version`: at the `literals` level the stored text *is* the normalizer's
  output, so bumping one should prompt a look at the other.
- **SQL safety.** User free text (hashes, names, ids, limits) must be **bound parameters**
  (`$name`). Only allow-listed identifiers may be interpolated: `FilterCompiler.AllowedFields`,
  `WorkloadQueries` sort/dimension lists. `FilterCompiler` escapes strings by doubling single
  quotes. A `planId` destined for a `.sqlplan` name must reject path traversal, in Core and not
  only at the host boundary. `SET SHOWPLAN_XML ON` stays compile-only — never a destructive
  re-run.
  `query` is the deliberate exception: it runs user SQL verbatim, and non-writing is guaranteed by
  opening the connection **read-only**, never by inspecting the statement — parsing it to decide
  whether it writes would be a sieve.
  The second exception is the path of an `ATTACH` in `ProjectComparison` (`compare`): DuckDB.NET
  1.5.3 refuses a bound parameter there (`Parser Error: syntax error at or near "$"`, measured
  2026-10-06). The path is resolved by the host to an existing `sqlferret.duckdb`, interpolated as
  a string literal with single quotes doubled, and attached `READ_ONLY`; nothing else in that
  statement comes from the user. No other `ATTACH` and no other path may be interpolated.
- **Capture actions.** `query_post_execution_plan_profile` self-identifies (`QueryHash` /
  `QueryPlanHash` inside `<StmtSimple>`), but correlating with `executions` requires
  `sqlserver.query_hash` on the completion events. **`<StmtSimple StatementText>` is never a key**
  — the engine truncates it, and it can be near-empty. See `docs/capture-session.md`.
- **Every claim about SQL Server behaviour carries its source.** A statement about what the engine
  emits, what values a field can take, at what cadence, or whether a counter is cumulative or
  instantaneous is written in one of three forms and never a fourth: *measured* (naming the
  capture and what was counted), *documented* (linking Microsoft Learn), or *unverified* (said
  plainly, so a reader knows not to build on it). This is a shape requirement, not a ban on
  writing quickly.
  A measurement describes the server that produced it, never the engine: "the four components
  observed here" and "the four components" are different claims, and only the first is earned by
  counting. The gap is invisible once the sentence is finished, which is why the form is the
  guard. Two errors in the System Health spec came through it — a component set that is five plus
  one per availability group, and a cadence read off the wrong parameter — both caught by one
  query to the Microsoft Learn MCP server after the fact, either of which would have been caught
  before it by asking the question the form forces.

## C# 14 baseline

Primary constructors for stateful services; collection expressions `[]` (not `new[]{}` /
`Array.Empty`); `record` over tuples for multi-field values; raw string literals `"""` for SQL;
`required`/`init` on DTOs; switch/`is` pattern matching where it reads cleanly.

A bare `catch` is acceptable **only** on a deliberate fallback path (parse failure → fallback,
malformed JSON → empty state, best-effort provenance write). Comment why, at the site.

---

# C. Working agreement

## C.1 Scope

The user's request defines the scope. Do not modify a file, API, dependency, configuration,
database schema or external behavior that the task does not require.

On finding an unrelated problem: state it briefly, name the impact and the files, **do not fix
it**, and offer it as a separate task if useful. This applies to invariant violations in
untouched code as much as to anything else.

## C.2 Analyze before modifying

Before a non-trivial change: name the files and symbols involved, state the plan in a few lines,
and flag assumptions, risks and side effects.

Ask for confirmation only when the decision is ambiguous, costly, irreversible, risky, or outside
the requested scope. For a local, clearly requested, reversible change, act — do not ask
needlessly.

## C.3 Evidence, not assumption

Every claim about the code, the architecture, a behavior, a bug, a compatibility or a test result
must rest on something observable: a file read, a search, command output, a test run, internal
documentation, or a reproduction.

When information is missing, say precisely what is missing. Do not fill the gap with a guess.
Ask, or propose a verification step. Mark anything unconfirmed as unverified rather than
asserting it.

## C.4 Minimal, reversible changes

Prefer the smallest change that satisfies the request.

Do not combine, unless explicitly asked: a functional fix, a refactor, a broad rename, a global
reformat, a dependency upgrade, an API change, a schema change, or unrelated cleanup.

`dotnet format` reformats the whole tree by default — scope it to the files you touched.

## C.5 Proportionate validation

After a change, run the documented validation that fits the area touched — typically
`dotnet build` and a filtered `dotnet test`. The full suite runs in about ninety seconds and is
cheap, so use it when the change is not narrowly scoped.

Do not run long, costly or destructive commands without need or explicit agreement. Report the
exact commands run and their results. Distinguish clearly between validations that **passed**,
were **not run**, and **failed**. Never state that work is done or verified without something
verifiable behind it.

## C.6 Reporting

Close each task with a compact summary: files changed, what changed, validations run and their
result, risks/limits/unverified items, and — kept separate — unrelated problems noticed.

No long narration of internal reasoning. No claim of having tested, read or verified what was
not.

## C.7 Subagents

When delegating: pass verified facts only, a precise objective, a bounded file or search scope,
and explicit return criteria. Separate facts from assumptions from open questions.

Do not treat a subagent's inferred summary as repository truth — verify anything important before
acting on it. Modification decisions and irreversible actions stay with the main agent.

## C.8 Git and workflow

- **TDD**: red → green. Tests assert real behavior; test output stays clean on success.
- **Commit only when asked.** Do not commit as a step inside a larger task.
- Commits are authored by the user alone. No co-author trailer — this is a published
  artifact, whatever the development repository does.
- Branch off `main`. **Never commit on `main` without consent.**
- Never commit: `.duckdb`, `plans/`, `.env`, `sample/`, `exemples-plans/`, `*.sqlplan`.
- Run `dotnet format` on touched files before a commit.

---

# D. Status and known gaps

Single source: **`docs/development.md#known-gaps`**. Do not duplicate the list here; update it
there.

Summary: the `.xel` pipeline, DuckDB storage, workload and blocking analysis, DDL classification,
ad-hoc `query`, real-plan ingestion, Query Store import, obfuscation, the CLI and the TUI are
implemented and tested.

Open items include hot-path parsing (~3 ScriptDom parses per event — sharing one parse across
`TokenNormalizer` and `AstClassifier` is the biggest ingestion speedup available), folder-import
`files_count`/`bytes_total` accuracy, culture-invariant `ParameterExtractor.GuessType`, CLI sort
options for `top-slow`, sealing services, broader `UiState` round-trip assertions, no deadlock
digest, and plan-to-execution correlation (needs no code — only a capture carrying
`sqlserver.query_hash`).

---

# E. Unverified — confirm before relying on

- **Avalonia** as a future host — no code, no dependency, no reference to it anywhere in the
  repository. Aspirational only.
- The KISS rule in section B is elsewhere attributed to a design spec, §2. That spec is not part
  of this repository. The rule as written above is self-contained and binding on its own; do not
  go looking for the document.

Do not act on these as if they were established.
