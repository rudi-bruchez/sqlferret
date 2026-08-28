# Design — `sqlTextSanitization`: sanitizing `executions.sql_text_raw` at ingest

Date: 2026-08-28
Status: approved design, revision 2, not yet implemented

**Revision history.** Revision 1 proposed a third level, `Obfuscated`, built on
`StatementTextRewriter` and the project `ObfuscationMap`. Review established that the rewriter
tokenizes an identifier only if a prior `obfuscate-plan` run already minted it
(`StatementTextRewriter.cs:67-73` falls through to `sb.Append(t.Text)`; table and column tokens
come from `PlanObfuscator.cs:386-387`), so at ingest on a fresh project that level would have
emitted real table and column names at double the parse cost. It is **dropped** from this change.
Revision 1 also failed to protect `normalized_queries.normalized_sql`, which leaked literals on
unparseable input; that is fixed here (§4.2). Reviews live beside this file as
`…-design-claude.md` and `…-design-agy.md`.

Revision 2 shipped with its own defect (found empirically, not by review, after merge to the
feature branch): `Literals` collapsed `sp_executesql` RPCs' inner statement — itself a string
literal in `EventMapper.ExtractSql`'s `statement` field — along with the parameter values,
because `TokenNormalizer` has no notion that one particular literal is SQL text. Every table and
column name in the dominant RPC shape for parameterized applications was lost, contradicting
§4.3's "removes values, not structure" as written. Fixed by unwrapping and re-normalizing that one
literal in place (`SpExecuteSqlUnwrapper`), with a fallback to the fully collapsed form whenever
the unwrap can't be done safely; see §4.3.

---

## 1. Problem

`docs/privacy.md` states the gap plainly today:

> `executions.sql_text_raw` | The statement as captured, including any inlined literals |
> **Nothing. Always stored.**

The redaction policy (`off` / `hash` / `masked` / `full`) covers *extracted parameter values*
only. A parameterized RPC gets its values redacted; a batch with inlined literals is written to
`sqlferret.duckdb` verbatim:

```sql
EXEC dbo.GetCustomer @Email = 'alice@example.com'           -- @Email hashed
SELECT * FROM Customers WHERE Email = 'alice@example.com'   -- stored verbatim
```

Normalization already replaces literals with `?` in `normalized_queries.normalized_sql`, but that
is a grouping mechanism, not a privacy one — the raw column keeps the original. Consequence: a
project directory built from a literal-heavy workload must be treated as production data, which
blocks the main sharing use case (handing an assessment project to a colleague or a client).

The same problem was already solved once in this codebase, for blocking input buffers.
`IngestionService.PrepareProc` (`Ingestion/IngestionService.cs:113-134`) stores `nq.NormalizedSql`
in place of `InputBufRaw` under any policy except `off`, **and** substitutes a placeholder into
the `NormalizedQuery` itself when tokenization failed. This design applies that same treatment,
guard included, to `sql_text_raw`.

## 2. Scope

**In scope.** An ingest-time, destructive option that rewrites the statement text *before* it
reaches `InsertBatch`, so unredacted literals never touch the DuckDB file. The matching guard on
`normalized_queries`. Provenance and a version constant. Gating `EstimatedPlanService` on
sanitized runs, including the small model and query change it requires. Documentation.

**Out of scope.** Sanitizing an already-imported project after the fact. Sanitizing the truncated
`StatementText` in `plans/**/*.digest.json`. Sanitizing `qds_query_text.query_sql_text`
(`Storage/DuckDbProject.QueryStore.cs:30`), which `query-store-import` writes verbatim from
`sys.query_store_query_text` (`Server/QueryStoreImportService.cs:106-114`) into the same
`sqlferret.duckdb`, untruncated — a deliberate exclusion, not an oversight. All three exposures
must be *documented*, not left to be discovered; see §10. Also out of scope: identifier
obfuscation of statement text (the dropped `Obfuscated` level; a separate spec if wanted), and any
change to parameter redaction, blocking XML retention or deadlock graph redaction.

**Explicitly rejected.** Deriving the new behavior from `--redaction`. The two knobs stay
orthogonal: `docs/privacy.md` already documents one sharp edge where `off` means two different
things, and a third meaning would make the policy unexplainable.

## 3. The option

```csharp
namespace SqlFerret.Core.Normalization;

public enum SqlTextSanitization { Raw, Literals }
```

| Level | `sql_text_raw` contains | Cost |
|---|---|---|
| `Raw` *(default)* | The statement as captured | none |
| `Literals` | `nq.NormalizedSql` — literals `?`, identifiers real | none |

`Raw` is the default so existing projects, scripts and expectations are unchanged. Privacy here
is opt-in, and `docs/privacy.md` keeps saying so.

An enum rather than a bool: the value appears verbatim in the CLI, the config file and the
`sql_text_policy` column, and a future third level should extend it rather than replace it.

**Surface.**

- `IngestionOptions` gains a trailing member with a default:

  ```csharp
  public record IngestionOptions(RedactionMode Redaction, IReadOnlyList<FilterRule> Filters,
      int BatchSize = 5000, string? PlanProfileDir = null,
      SqlTextSanitization SqlText = SqlTextSanitization.Raw);
  ```

  Positional record with trailing defaults: every existing call site (roughly twenty, mostly in
  tests) keeps compiling untouched.

- `SqlFerretConfig` likewise gains `string SqlTextPolicy = "raw"` as a trailing defaulted member,
  reading `ingest.sqlTextSanitization`. Also non-breaking — five-argument positional construction
  still compiles. (Revision 1 review claimed otherwise; that claim is incorrect.)

  The member is **`SqlTextPolicy`, not `SqlTextSanitization`**: a member named after the enum type
  shadows it inside the declaring file, and `Enum.TryParse<SqlTextSanitization>` there would stop
  resolving. One name per concept — `SqlTextSanitization` is the enum type, `SqlTextPolicy` is the
  string form, matching `ExecutionEvent.SqlTextPolicy` and `ingestion_runs.sql_text_policy`.

- CLI: `--sanitize-sql-text <raw|literals>` on `import`, parsed in `Program.cs` immediately after
  the existing `--redaction` block (`Program.cs:59-65`) and validated identically: `Enum.TryParse`
  with `ignoreCase: true`, an explicit error listing the valid values, non-zero exit. The flag
  overrides the config value.

- TUI: `ImportPresenter` reads `project.Config.SqlTextPolicy` when building its
  `IngestionOptions`, so a configured project behaves the same in both hosts. (Revision 1 deferred
  this; it is one line and leaving the TUI silently on `Raw` would be a privacy trap.)

Naming note: the config key is `ingest.sqlTextSanitization`, not `sanitizeSqlTextRaw`, because
`raw` is one of the *values* — `sanitizeSqlTextRaw: "raw"` reads as a contradiction.

## 4. Behavior

### 4.1 The rewrite

`IngestionService` already calls `QueryNormalizer.Normalize(e.SqlTextRaw)` for every mapped event
(`IngestionService.cs:86`). The sanitizer reuses that result — **no additional parsing, at either
level**. The rewrite happens where the `PreparedRow` is built:

```csharp
var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
if (nq.TokenizeFailed) tokenizeFailures++;

var (text, safeNq, failed) = SqlTextSanitizer.Apply(e.SqlTextRaw, nq, options.SqlText);
if (failed) sqlTextSanitizeFailures++;

buffer.Add(new PreparedRow(e with { SqlTextRaw = text }, safeNq, RedactParams(e)));
```

The fingerprint is computed from the original text before the rewrite, and both `InsertExecution`
(`$nh`) and `UpsertSignature` (`$h`) read `r.Normalized.NormalizedHash`, which the sanitizer never
touches. So `normalized_hash` and every aggregate keyed on it are bit-for-bit identical to a `Raw`
import. Asserted by test, not assumed.

### 4.2 The `normalized_queries` guard — the part revision 1 got wrong

`TokenNormalizer.FallbackCollapse` (`Normalization/TokenNormalizer.cs:129`) is
`Regex.Replace(raw, @"\s+", " ").Trim().ToLowerInvariant()` — whitespace collapse and lowercase,
**literals fully intact**. So on tokenize failure, `nq.NormalizedSql` is not safe either.
`UpsertSignature` (`Storage/DuckDbProject.cs:166-174`) writes it into `normalized_queries`, in the
same file, joinable on `normalized_hash`.

Protecting only `executions.sql_text_raw` would therefore move the literal rather than remove it.
`SqlTextSanitizer.Apply` returns the substituted `NormalizedQuery` alongside the text:

```csharp
namespace SqlFerret.Core.Normalization;

public static class SqlTextSanitizer
{
    public const int Version = 1;
    public const string Placeholder = "(unparseable sql text; redacted)";

    public static (string Text, NormalizedQuery Normalized, bool Failed) Apply(
        string raw, NormalizedQuery nq, SqlTextSanitization level)
    {
        if (level == SqlTextSanitization.Raw) return (raw, nq, false);
        if (nq.TokenizeFailed)
            return (Placeholder, nq with { NormalizedSql = Placeholder }, true);
        return (nq.NormalizedSql, nq, false);
    }
}
```

`NormalizedHash` survives the substitution — it is a non-reversible hash and is needed for
fingerprint joins. This mirrors `PrepareProc:130` exactly.

### 4.3 Two limits that must be documented, not glossed

**Sanitization is per-run; `normalized_queries` is project-wide.** `UpsertSignature` is
`ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at = $ts` — it never overwrites
`normalized_sql`. A hash first inserted by a `Raw` run keeps that run's text forever, and a later
sanitized import into the same project will not clean it. **A project is only safe to share if
every run in it was sanitized.** `ingestion_runs.sql_text_policy` (§5) is what makes that
checkable.

**`Literals` does not hide your schema.** Table, column and procedure names remain in both
`sql_text_raw` and `normalized_sql`, including inside an `sp_executesql` RPC: the inner statement
argument is unwrapped and normalized in place (`SpExecuteSqlUnwrapper`), so its identifiers survive
the same way a plain batch's do. The level removes values, not structure. Anyone deciding whether
to share a project needs that stated plainly. The unwrap has one residual: when the statement
argument is not a string literal (passed via a variable) or the inner statement fails to
tokenize, the whole call falls back to the fully collapsed form and its identifiers are lost too
— a loss of readability, not of privacy, since no value survives either path.

## 5. Provenance, versioning and schema

### 5.1 Version constant

Mirrors the existing `QueryNormalizer.Version = 1` invariant. `SqlTextSanitizer.Version = 1`,
declared beside the rule that governs it:

**Changing the rewrite rules — which token types collapse to `?`, how the placeholder reads, what
`Literals` substitutes — requires bumping `Version`.** Text produced under two versions is not
comparable, and a project that mixes them shows it in `ingestion_runs`.

With `Obfuscated` dropped, the version now has no hidden dependency on mutable state: at
`Literals` the output is a pure function of the input text and `QueryNormalizer.Version`. Note
the coupling — a `QueryNormalizer.Version` bump changes sanitized text too, so bumping one
should prompt a look at the other.

### 5.2 Schema

Three columns on `ingestion_runs`, added through the existing
`ALTER TABLE … ADD COLUMN IF NOT EXISTS` block (`Storage/DuckDbProject.cs:73-76`):

```sql
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_policy TEXT;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitizer_version INTEGER;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitize_failures BIGINT;
```

Migration semantics for a project created before this change: `sql_text_policy IS NULL` reads as
`raw`, `sql_text_sanitizer_version IS NULL` as "pre-versioning". No backfill, no rewrite of
existing rows — the historical text genuinely is raw.

`BeginRun` gains a `SqlTextSanitization sqlText` parameter (it has exactly one caller,
`IngestionService.cs:20`) and writes `$stp` / `$stv` into its named-column `INSERT`. `FinishRun`
gains the failure count. `executions` gets **no new column**: `InsertExecution` uses a positional
`INSERT INTO executions VALUES (…)` with no column list, so adding one there would break silently.

The new counter satisfies the standing invariant: nothing is silently dropped, and a new outcome
gets its own mutually exclusive counter.

## 6. Architecture: no new dependency edge

Revision 1 requested an approved `Ingestion → Obfuscation` edge for the `Obfuscated` level. With
that level dropped, **the edge is no longer needed and must not be added.** `SqlTextSanitizer`
lives in `SqlFerret.Core.Normalization`, on which `Ingestion` already depends. `CLAUDE.md`'s
dependency line is unchanged by this work.

`SqlTextSanitizer` and the `SqlTextSanitization` enum are the only new types. Nothing in
`Obfuscation` is touched.

## 7. Consequence: estimated plans

`WorkloadQueries.LoadExecution` (`Analysis/WorkloadQueries.cs:151`) rehydrates an `ExecutionEvent`
from the database, `ReplayBuilder` builds a replay script from `ExecutionEvent.SqlTextRaw`, and
`EstimatedPlanService` sends it to SQL Server under `SET SHOWPLAN_XML ON`.

`SELECT * FROM Customers WHERE Email = ?` is not valid T-SQL. A sanitized project cannot produce
estimated plans.

`EstimatedPlanService` is `(string connectionString, string plansFolder)` and receives only an
`ExecutionEvent`; it has no database handle, and `ExecutionEvent` carries no run identity. The
resolution is to carry the policy on the event rather than give `Server` a database:

- `ExecutionEvent` gains `public string? SqlTextPolicy { get; init; }` (null = unknown/legacy).
- `LoadExecution` joins `executions e JOIN ingestion_runs r ON e.run_id = r.run_id` and projects
  `r.sql_text_policy` into it.
- `CaptureAsync` refuses before opening a connection when the value is present and not `raw`.

This adds no namespace edge — `Model` and `Analysis` are already below `Server`.

Message, in the spirit of the existing `export-events` refusal:

> estimated plan: this execution was imported with `--sanitize-sql-text literals`; the stored
> statement text is not executable. Re-import with `--sanitize-sql-text raw` to capture estimated
> plans.

The refusal is deliberate, like `export-events`' refusal on a `masked` project, and belongs in
`docs/execution-plans.md`.

Other consumers need no code change but do change what they display: `top-slow`, `WorkloadQueries`
lines 71/82/157, and the TUI `DrillDownView:97` will show sanitized text. That is the intended
effect.

## 8. Performance

Nothing measurable. `Literals` reuses a `NormalizedQuery` that was already computed for every
mapped event; the rewrite is a record `with`-expression and a field read. No new ScriptDom pass at
any level.

The ~3 parses per event under `docs/development.md#known-gaps` are unchanged by this work.

## 9. Testing

TDD, red before green, patterned on `BlockingIngestionTests`, which already covers the equivalent
input-buffer behavior.

| Test | Asserts |
|---|---|
| `Raw_KeepsStatementVerbatim` | Default import is byte-identical to today |
| `Literals_RemovesInlinedLiteral` | A known literal is absent from `sql_text_raw`; `?` present |
| `Literals_PreservesIdentifiers` | Table and column names still present (documents §4.3) |
| `Sanitization_DoesNotChangeFingerprint` | `normalized_hash` identical between `Raw` and `Literals` |
| `Literals_UnparseableUsesPlaceholder` | Placeholder in `executions.sql_text_raw`, counter incremented |
| `Literals_UnparseableAlsoScrubsNormalizedSql` | **The B2 regression test**: the literal is absent from `normalized_queries.normalized_sql`, not only from `executions` |
| `Literals_PreservesNormalizedHashOnFailure` | The substitution does not disturb `normalized_hash` |
| `Run_RecordsPolicyAndVersion` | `sql_text_policy` and `sql_text_sanitizer_version` written |
| `LegacyProject_MigratesAndReadsAsRaw` | Opening a pre-change project adds the columns; NULL reads as `raw` |
| `EstimatedPlan_RefusesOnSanitizedRun` | Refusal message, no connection attempt |
| `LoadExecution_ProjectsSqlTextPolicy` | The join populates `ExecutionEvent.SqlTextPolicy` |

`Literals_UnparseableAlsoScrubsNormalizedSql` is the test that would have caught the revision 1
defect; it is not optional.

The unparseable-input tests use a hand-written fake event rather than a `sample/` file, so they
run in the default gate and not the sample-gated one.

Validation: `dotnet build` (0 warnings) and the full `dotnet test` suite — the change touches
ingestion, storage, analysis and the server path, so a filtered run is not sufficient.
`dotnet format` scoped to the touched files.

## 10. Documentation to update

- `docs/privacy.md` — the first row of the "What lands on disk" table and the whole "The one
  thing to internalize" section become wrong as written; both must be rewritten around the new
  option. Must also state, plainly: (a) `Literals` removes values, not schema (§4.3); (b) a
  project is only safe to share if **every** run was sanitized, because `normalized_queries` is
  project-wide and never overwritten (§4.3); (c) a sanitized project can still carry real
  statement text in three other places, listed largest first: `qds_query_text.query_sql_text`
  (verbatim and untruncated, whenever `query-store-import` has run), `plans/**/*.digest.json`
  (truncated `StatementText`), and `.sqlplan` files until `obfuscate-plan` rewrites them; and (d)
  `--redaction off` combined with `literals` still leaves real statement text in
  `blocking_processes.inputbuf` and `blocking_reports.raw_xml`, because the two knobs are
  orthogonal. Points (c) and (d) matter most — a feature that creates false confidence is worse
  than the documented gap it replaces.
- `docs/cli-reference.md` — the `--sanitize-sql-text` flag on `import`.
- `docs/configuration.md` — `ingest.sqlTextSanitization`.
- `docs/data-model.md` — the three new `ingestion_runs` columns and their NULL semantics.
- `docs/execution-plans.md` — the estimated-plan refusal.
- `Project/AuditProject.cs:151` — the generated project `README.md` currently says only
  "Parameter values may be redacted per the project's redaction policy". It is written once at
  creation and would describe the wrong privacy posture for a sanitized project; add a line on
  statement text.
- `CLAUDE.md` — the `SqlTextSanitizer.Version` bump rule beside the `QueryNormalizer.Version`
  invariant, including their coupling (§5.1). The dependency line is **not** changed.

`docs/development.md#known-gaps` needs no entry: this change adds no parse cost and leaves no
known gap of its own.

## 11. Open questions

None. Identifier obfuscation of statement text — the dropped `Obfuscated` level — is a separate
design if it is wanted; it needs the rewriter to mint tokens for identifiers it has never seen,
which a token stream cannot classify into table / column / alias without an AST pass.
