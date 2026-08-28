# Review & Assessment — SQL Text Sanitization Implementation Plan

**Date:** 2026-08-28  
**Reviewer:** Antigravity  
**Target:** `docs/superpowers/plans/2026-08-28-sql-text-sanitization.md`  
**Base Spec:** `docs/superpowers/specs/2026-08-28-sanitize-sql-text-raw-design.md` (Revision 2)  
**Status:** **APPROVED — Ready for Implementation**

---

## 1. Executive Summary & Verdict

**Verdict: Approved for immediate implementation.**

The implementation plan in `docs/superpowers/plans/2026-08-28-sql-text-sanitization.md` is technically sound, comprehensive, and aligns directly with the architectural constraints and invariants of SQLFerret.

### 1.1 Key Strengths of the Plan
1. **Disciplined Scope & Zero New Dependency Edges:** By implementing the Revision 2 design (`Raw` and `Literals`), the plan avoids introducing an `Ingestion → Obfuscation` dependency edge. `SqlTextSanitizer` resides in `SqlFerret.Core.Normalization`, which `Ingestion` already references.
2. **Zero Ingestion Performance Overhead:** Statement sanitization at the `Literals` level reuses the `NormalizedQuery` produced during the existing normalizer pass (`QueryNormalizer.Normalize(e.SqlTextRaw)`). No additional ScriptDom parsing or tokenization passes are added to the hot ingest loop.
3. **Privacy Hole (B2) Completely Plugged:** On tokenize failure, `SqlTextSanitizer.Apply` replaces both `executions.sql_text_raw` and `NormalizedQuery.NormalizedSql` with `"(unparseable sql text; redacted)"`. This prevents raw literal leakage into `normalized_queries` (which shares the DuckDB database and joins on `normalized_hash`).
4. **Strict Backward Compatibility:** Every new record parameter (`IngestionOptions`, `IngestionResult`, `SqlFerretConfig`) and method parameter (`BeginRun`, `FinishRun`) is trailing and defaulted. No existing test call sites or library consumers will experience compilation failures or runtime regressions.
5. **Full Provenance & Versioning Invariants:** The policy (`sql_text_policy`), sanitizer version (`sql_text_sanitizer_version`), and failure counter (`sql_text_sanitize_failures`) are persisted to `ingestion_runs` using the established `ADD COLUMN IF NOT EXISTS` migration pattern, with pre-migration runs gracefully reading as `NULL` (interpreted as `raw`).
6. **Robust Gate for Estimated Plans:** `ExecutionEvent.SqlTextPolicy` is populated via `LEFT JOIN ingestion_runs` in `WorkloadQueries.LoadExecution`, enabling `EstimatedPlanService.CaptureAsync` to reject sanitized statement replay prior to attempting any database connection.

---

## 2. Task-by-Task Technical Review

### Task 1: `SqlTextSanitizer` — The Pure Rewrite
- **Namespace & Placement:** `SqlFerret.Core.Normalization` in `src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs`.
- **Enum:** `SqlTextSanitization { Raw, Literals }`.
- **Constants:** `Version = 1`, `Placeholder = "(unparseable sql text; redacted)"`.
- **Pure Function Signature:** `(string Text, NormalizedQuery Normalized, bool Failed) Apply(string raw, NormalizedQuery nq, SqlTextSanitization level)`.
- **Verification:**
  - `nq with { NormalizedSql = Placeholder }` uses C# record copy-and-update syntax, preserving `NormalizedHash`, `StatementKind`, `PrimaryTable`, and `TokenizeFailed`.
  - At `Raw`, the exact raw text and unmodified `nq` are returned (`failed = false`).
  - At `Literals`, if `nq.TokenizeFailed == false`, `(nq.NormalizedSql, nq, false)` is returned.
  - At `Literals`, if `nq.TokenizeFailed == true`, `(Placeholder, nq with { NormalizedSql = Placeholder }, true)` is returned.
- **Tests:** 6 test cases in `SqlTextSanitizerTests.cs` covering all branches, fingerprint stability, and the version invariant.
- **Assessment:** **No issues found.**

---

### Task 2: Storage & Migration (`DuckDbProject`)
- **Schema Migration:** Added to the existing idempotent migration block in `DuckDbProject.CreateSchema`:
  ```sql
  ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_policy TEXT;
  ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitizer_version INTEGER;
  ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitize_failures BIGINT;
  ```
- **`BeginRun` Method:** Trailing parameter `SqlTextSanitization sqlText = SqlTextSanitization.Raw`. Writes `$stp` (`sqlText.ToString().ToLowerInvariant()`) and `$stv` (`SqlTextSanitizer.Version`).
- **`FinishRun` Method:** Trailing parameter `long sqlTextSanitizeFailures = 0`. Updates `sql_text_sanitize_failures = $stsf`.
- **Verification:**
  - `executions` table is intentionally untouched, preserving positional parameter ordering in `InsertExecution` (`INSERT INTO executions VALUES (...)`).
  - DuckDB parameter helper `Add(c, "$name", value)` correctly strips leading `$` as required by DuckDB.NET 1.5.3.
  - Migration test simulates pre-migration databases (dropping columns and reopening) to assert `NULL` reads.
- **Assessment:** **No issues found.**

---

### Task 3: Ingestion Pipeline Integration (`IngestionService`)
- **`IngestionOptions`:** Trailing parameter `SqlTextSanitization SqlText = SqlTextSanitization.Raw`.
- **`IngestionResult`:** Trailing parameter `long SqlTextSanitizeFailures = 0`.
- **`IngestionService.Ingest` Loop:**
  ```csharp
  var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
  if (nq.TokenizeFailed) tokenizeFailures++;

  var (sqlText, safeNq, sanitizeFailed) =
      SqlTextSanitizer.Apply(e.SqlTextRaw, nq, options.SqlText);
  if (sanitizeFailed) sqlTextSanitizeFailures++;

  buffer.Add(new PreparedRow(e with { SqlTextRaw = sqlText }, safeNq, RedactParams(e)));
  mapped++;
  ```
- **Verification:**
  - `e with { SqlTextRaw = sqlText }` ensures `executions.sql_text_raw` stores the sanitized text.
  - `safeNq` is passed into `PreparedRow`, ensuring `DuckDbProject.UpsertSignature` writes the scrubbed `safeNq.NormalizedSql` to `normalized_queries`.
  - `safeNq.NormalizedHash` remains the original unredacted fingerprint, ensuring cross-table joins, queries, and aggregations remain identical across all sanitization modes.
  - `FinishRun` and `IngestionResult` correctly receive `sqlTextSanitizeFailures`.
- **Tests:** 5 comprehensive integration tests in `SqlTextSanitizationIngestionTests.cs` verifying raw retention, literal removal, tokenize-failure scrubbing in both tables, fingerprint equality, and run policy provenance.
- **Assessment:** **No issues found.**

---

### Task 4: Configuration & Host Surfaces (CLI & TUI)
- **`SqlFerretConfig`:** Trailing parameter `string SqlTextPolicy = "raw"`. Reads JSON property `ingest.sqlTextSanitization`.
- **CLI (`SqlFerret.Cli/Program.cs`):**
  - Parses `--sanitize-sql-text` (defaulting to `project.Config.SqlTextPolicy`).
  - Validates value using `Enum.TryParse<SqlTextSanitization>(..., ignoreCase: true)`. Exits `1` on invalid input with explicit error message.
  - Updates CLI usage string.
- **TUI (`SqlFerret.Tui/Presenters/ImportPresenter.cs`):**
  - Parses `project.Config.SqlTextPolicy`, falling back to `SqlTextSanitization.Raw`. Passes policy to `IngestionOptions`.
- **Verification:**
  - Config naming: `ingest.sqlTextSanitization` avoids contradictory key-value pairs like `sanitizeSqlTextRaw: "raw"`.
  - CLI flag `--sanitize-sql-text` validation executes before checking file existence on disk, ensuring predictable error output.
- **Assessment:** **No issues found.**

---

### Task 5: Estimated Plans Refusal (`EstimatedPlanService`)
- **`ExecutionEvent` Model:** Added `public string? SqlTextPolicy { get; init; }`.
- **`WorkloadQueries.LoadExecution`:**
  ```csharp
  cmd.CommandText = """
    SELECT e.event_name, e.event_class, e.object_name, e.database_name, e.login_name,
           e.client_hostname, e.client_app_name, e.session_id, e.captured_at, e.duration_us,
           e.sql_text_raw, e.xe_file_name, e.file_offset, r.sql_text_policy
    FROM executions e
    LEFT JOIN ingestion_runs r ON r.run_id = e.run_id
    WHERE e.execution_id = $id
    """;
  ```
- **`EstimatedPlanService.CaptureAsync` Gate:**
  ```csharp
  if (ev.SqlTextPolicy is not null
      && !ev.SqlTextPolicy.Equals("raw", StringComparison.OrdinalIgnoreCase))
  {
      throw new InvalidOperationException(
          $"estimated plan: this execution was imported with --sanitize-sql-text {ev.SqlTextPolicy}; " +
          "the stored statement text is not executable. Re-import with --sanitize-sql-text raw " +
          "to capture estimated plans.");
  }
  ```
- **Verification:**
  - `LEFT JOIN` guarantees that an execution without a corresponding `ingestion_runs` row remains loadable.
  - `CaptureAsync` checks `ev.SqlTextPolicy` as its very first action before `ReplayBuilder.Build(ev)` and before opening a `SqlConnection`, preventing useless connection attempts or unhandled syntax error exceptions from SQL Server.
- **Assessment:** **No issues found.**

---

### Task 6: Documentation & Privacy Caveats
- Updates are thorough across all 7 relevant files:
  - `docs/privacy.md`: "What lands on disk" table, level explanations, project-wide `normalized_queries` sharing warnings, plan digests caveat.
  - `docs/cli-reference.md`: `--sanitize-sql-text` documentation.
  - `docs/configuration.md`: `ingest.sqlTextSanitization` setting.
  - `docs/data-model.md`: `sql_text_policy`, `sql_text_sanitizer_version`, and `sql_text_sanitize_failures` columns.
  - `docs/execution-plans.md`: Estimated-plan refusal documentation.
  - `AuditProject.cs`: Generated project `README.md` notes template.
  - `CLAUDE.md`: `SqlTextSanitizer.Version = 1` invariant.
- **Assessment:** **No issues found.**

---

## 3. Invariants & Code Standards Compliance

| Invariant / Guideline | Plan Compliance | Evidence |
|---|---|---|
| **No Unneeded Abstractions (KISS)** | **Compliant** | Uses static pure functions (`SqlTextSanitizer.Apply`), record DTOs, no DI containers or repository wrappers. |
| **No Dependency Violations** | **Compliant** | `SqlTextSanitizer` in `Normalization`; no `Ingestion → Obfuscation` edge added; dependency line in `CLAUDE.md` remains unchanged. |
| **Microseconds in Core** | **Compliant** | No durations modified. |
| **Nothing Silently Dropped** | **Compliant** | Added `sql_text_sanitize_failures` counter to `ingestion_runs` and `IngestionResult`. |
| **DuckDB Parameter Naming** | **Compliant** | Uses `Add(c, "$name", value)` helper throughout. |
| **C# 14 Baseline** | **Compliant** | Uses collection expressions (`[]`), raw string literals (`"""`), record `with` expressions, primary constructors. |
| **TDD (Red-to-Green)** | **Compliant** | Every task defines failing tests first, followed by implementation and test execution verification. |
| **Scoped Formatting** | **Compliant** | `dotnet format` commands explicitly scoped via `--include` to touched files. |

---

## 4. Execution Guidance for Implementers

1. **Branch Preparation:**
   Ensure working tree is on `feat/sql-text-sanitization` (or agreed feature branch) prior to starting Task 1.
2. **Task Order:**
   Follow Tasks 1 through 6 sequentially as written. Tasks 2 and 3 depend on Task 1 types; Task 4 and 5 depend on Tasks 2 and 3.
3. **Validation Baseline:**
   - Pre-implementation baseline: 377 passing, 10 skipped (due to environment gates: `sample/` and `SQLFERRET_TEST_CONN`).
   - Expected post-implementation baseline: 399 passing (377 + 22 new tests), 10 skipped.
   - Build validation: `dotnet build` with 0 warnings and 0 errors.

---

## 5. Conclusion

The plan `docs/superpowers/plans/2026-08-28-sql-text-sanitization.md` is complete, precise, and ready to be executed task-by-task.
