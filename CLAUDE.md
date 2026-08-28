# SQLFerret — project guide for Claude

Cross-platform SQL Server workload analyzer. A headless **`SqlFerret.Core`** engine plus thin
hosts: ingest `.xel` Extended Events → normalize queries → store in a DuckDB project directory →
analyze workload, blocking and execution plans → optionally snapshot Query Store.

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
```

CLI commands: `import`, `top-slow`, `export-blocking`, `export-events`, `query-store-import`,
`obfuscate-plan`. Flags and exit codes: `docs/cli-reference.md`.

Core namespaces, one-directional deps:
`Model` ← `Normalization` / `Parameters` / `Filtering` ← `Ingestion` / `Storage` / `Analysis` /
`Plans` / `Obfuscation` / `Replay` / `Server` ← hosts (`Cli`, `Tui`).
`Config` and `Project` are cross-cutting and host-agnostic.

**Core must stay host-agnostic.** It references no UI framework and knows nothing about a
terminal. Both hosts are thin shells over `AuditProject`, `ImportRunner`, `WorkloadQueries` and
the digest classes.

## Tech stack

- **.NET 10 / C# 14** (`net10.0`, Nullable + ImplicitUsings, LangVersion latest)
- **DuckDB.NET.Data.Full** 1.5.3 — embedded analytics store
- **Microsoft.SqlServer.XEvent.XELite** 2024.2.5.1 — cross-platform `.xel` reader (push/async)
- **Microsoft.SqlServer.TransactSql.ScriptDom** 180.37.3 — T-SQL token stream + minimal AST
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
dotnet test                                   # 387 tests: 377 pass, 10 skip (see below)
dotnet test --filter <TestClassName>          # focused
dotnet format <path>                          # style; .editorconfig is the baseline
```

The 10 skips are expected, not a regression. Two distinct gates:

- **`sample/` present** — `XelReaderTests`, `BlockingQueriesTests`, `BlockingDigestTests`,
  `CliSmokeTests`, `ImportRunnerTests`, `ImportPresenterTests`, `ImportViewTests`
- **`SQLFERRET_TEST_CONN` set** (a SQL Server connection string) —
  `EstimatedPlanServiceTests`, `QueryStoreImportServiceTests`

End-to-end against a local capture (writes outside the repo):

```bash
dotnet run --project src/SqlFerret.Cli -- import sample/trace_0.xel --project /tmp/wl
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/wl --limit 20
dotnet run --project src/SqlFerret.Tui -- /tmp/wl
```

**There is no CI in this repository.** Nothing validates a change except the commands above,
run locally.

## Sensitive data — non-negotiable

`sample/` holds **real production `.xel` captures** (~270 MB: real SQL text, literals, PII).
It is gitignored and **must never be committed**. Same for `exemples-plans/`, `*.sqlplan`,
`*.duckdb`, `plans/` (root), and `.env`.

There is no committed binary fixture and no container is required. Check `git status` after any
end-to-end run.

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
  is built. Note: `off` discards parameter values *and* is the only mode that retains raw
  blocking/deadlock XML. See `docs/privacy.md`.
- **Nothing silently dropped.** Unmapped, tokenize-failed, ingest-cleaned, blocking, deadlock and
  plan-profile events are all counted on `ingestion_runs`. Counters stay mutually exclusive and
  exhaustive. A new event type means a new counter.
- **`QueryNormalizer.Version = 1`**, persisted on both `ingestion_runs` and `normalized_queries`.
  Changing normalization rules requires bumping it — fingerprints across versions are not
  comparable.
- **SQL safety.** User free text (hashes, names, ids, limits) must be **bound parameters**
  (`$name`). Only allow-listed identifiers may be interpolated: `FilterCompiler.AllowedFields`,
  `WorkloadQueries` sort/dimension lists. `FilterCompiler` escapes strings by doubling single
  quotes. A `planId` destined for a `.sqlplan` name must reject path traversal, in Core and not
  only at the host boundary. `SET SHOWPLAN_XML ON` stays compile-only — never a destructive
  re-run.
- **Capture actions.** `query_post_execution_plan_profile` self-identifies (`QueryHash` /
  `QueryPlanHash` inside `<StmtSimple>`), but correlating with `executions` requires
  `sqlserver.query_hash` on the completion events. **`<StmtSimple StatementText>` is never a key**
  — the engine truncates it, and it can be near-empty. See `docs/capture-session.md`.

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
`dotnet build` and a filtered `dotnet test`. The full suite runs in a few seconds and is cheap,
so use it when the change is not narrowly scoped.

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
- Commits are authored by the user alone. No co-author trailer.
- Branch off `main`. **Never commit on `main` without consent.**
- Never commit: `.duckdb`, `plans/`, `.env`, `sample/`, `exemples-plans/`, `*.sqlplan`.
- Run `dotnet format` on touched files before a commit.

---

# D. Status and known gaps

Single source: **`docs/development.md#known-gaps`**. Do not duplicate the list here; update it
there.

Summary: the `.xel` pipeline, DuckDB storage, workload and blocking analysis, real-plan
ingestion, Query Store import, obfuscation, the CLI and the TUI are implemented and tested.
Open items include hot-path parsing (~3 ScriptDom parses per event), folder-import
`files_count`/`bytes_total` accuracy, culture-invariant `ParameterExtractor.GuessType`,
CLI sort options for `top-slow`, and plan-to-execution correlation (needs no code — only a
capture carrying `sqlserver.query_hash`).

---

# E. Unverified — confirm before relying on

Carried over from the previous version of this file, not confirmable from the repository:

- **`spec §2`** — cited as the authority for the KISS rule. No such document is in the repo.
- **Avalonia** as a future host — no code, no dependency, no reference to it anywhere.
- **`rtk`** as a git wrapper — not resolvable on this machine, in Bash or in PowerShell.

Do not act on these as if they were established.
