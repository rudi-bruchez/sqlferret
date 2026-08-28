# Design & Architecture Review — `sanitizeSqlTextRaw`: sanitizing `executions.sql_text_raw` at ingest

**Date:** 2026-08-28  
**Status:** Approved Design with Technical Architecture Review (Ready for Implementation)  
**Target Branch:** `feat/sanitize-sql-text-raw`  
**Base Document:** `docs/superpowers/specs/2026-08-28-sanitize-sql-text-raw-design.md`  

---

## 1. Executive Summary & Review Findings

This document incorporates a comprehensive architectural and technical review of the `sanitizeSqlTextRaw` design against the current SQLFerret codebase (`SqlFerret.Core`, `SqlFerret.Cli`, `SqlFerret.Tui`).

### 1.1 Key Review Findings & Corrections Addressed

1. **`StatementTextRewriter.Rewrite` Fallback Signal (Correction to Original §4.2 / §4.3):**
   - *Original spec assumption:* Assumed `StatementTextRewriter.Rewrite` could be reused with zero modifications while still reporting fallback counts.
   - *Finding:* `StatementTextRewriter.Rewrite(string, ObfuscationMap)` currently catches parse errors and exceptions internally and delegates to `Fallback(...)` without returning a status flag.
   - *Resolution:* Add an overload/method `StatementTextRewriter.RewriteWithStatus(string sqlFragment, ObfuscationMap map, out bool fallbackUsed)` (or return `(string Text, bool FallbackUsed)`), and keep `Rewrite` as a backward-compatible wrapper. `SqlTextSanitizer.Apply` uses this to increment `sql_text_sanitize_failures` accurately without redundant parsing passes.

2. **`EstimatedPlanService` Policy Inspection Gating (Correction to Original §7):**
   - *Original spec assumption:* Assumed `EstimatedPlanService` could inspect the run's `sql_text_policy` for an `ExecutionEvent`.
   - *Finding:* `EstimatedPlanService` is a standalone service in `SqlFerret.Core.Server` that only receives `ExecutionEvent`. It has no reference to DuckDB or `DuckDbProject`. Furthermore, `ExecutionEvent` did not track `SqlTextPolicy`, and `WorkloadQueries.LoadExecution` did not join `ingestion_runs`.
   - *Resolution:* 
     - Add `public string? SqlTextPolicy { get; init; } = null;` to `ExecutionEvent`.
     - In `WorkloadQueries.LoadExecution(long executionId)`, join `executions e JOIN ingestion_runs r ON e.run_id = r.run_id` and map `r.sql_text_policy` into `ExecutionEvent.SqlTextPolicy`.
     - In `EstimatedPlanService.CaptureAsync(ExecutionEvent ev, ...)`, inspect `ev.SqlTextPolicy`. If not `null` and not `"raw"`, refuse with the documented explicit error message before opening any SQL connection.

3. **`ObfuscationMap` Ingestion Lifecycle:**
   - When `options.SqlText == SqlTextSanitization.Obfuscated`, `IngestionService.Ingest` loads the existing project map once at the beginning (`project.LoadObfuscationMap()`), passes it into `SqlTextSanitizer.Apply`, and persists newly registered tokens in a single transaction at the end via `project.SaveObfuscationMap(map)`.
   - For `Raw` and `Literals` modes, map loading and saving is completely bypassed (0 overhead).

4. **TUI Host Config Alignment:**
   - `ImportPresenter` in `SqlFerret.Tui` will read `project.Config.SanitizeSqlTextRaw` when constructing `IngestionOptions`, ensuring TUI imports respect `sqlferret.config.json` out of the box.

---

## 2. Problem Statement & Motivation

`docs/privacy.md` states the gap plainly today:

> `executions.sql_text_raw` | The statement as captured, including any inlined literals | **Nothing. Always stored.**

The redaction policy (`off` / `hash` / `masked` / `full`) covers *extracted parameter values* only. A parameterized RPC gets its values redacted; a batch with inlined literals is written to `sqlferret.duckdb` verbatim:

```sql
EXEC dbo.GetCustomer @Email = 'alice@example.com'           -- @Email hashed/masked
SELECT * FROM Customers WHERE Email = 'alice@example.com'   -- stored verbatim in sql_text_raw
```

Normalization already replaces literals with `?` in `normalized_queries.normalized_sql`, but that is a grouping mechanism, not a privacy one — the raw column keeps the original text. Consequence: a project directory built from a literal-heavy workload must be treated as production data, which blocks the main sharing use case (handing an assessment project to a colleague, client, or external LLM tool).

The same problem was already solved in this codebase for blocking input buffers (`IngestionService.PrepareProc` stores `nq.NormalizedSql` in place of `InputBufRaw` under any policy except `off`, with a safe placeholder when tokenization fails). This design applies the same treatment to `executions.sql_text_raw`, and adds a second, stronger level built on the existing identifier obfuscation machinery.

---

## 3. Scope & Boundaries

### In Scope
- An ingest-time, destructive option that rewrites statement text *before* it reaches `InsertBatch`, ensuring unredacted literals never touch the DuckDB database file.
- Provenance tracking, schema migrations, and a version constant for the sanitizer.
- Explicit gating in `EstimatedPlanService` when attempting to capture estimated plans on sanitized runs.
- Updates to documentation (`docs/privacy.md`, `docs/cli-reference.md`, `docs/configuration.md`, `docs/data-model.md`, `docs/execution-plans.md`, `docs/development.md`, `CLAUDE.md`).

### Out of Scope
- Sanitizing an already-imported project after the fact (retroactive database scrubbing).
- Sanitizing the truncated `StatementText` in `plans/**/*.digest.json`.
- Sharing ScriptDom token streams across normalizer and rewriter (tracked as a future performance optimization).
- Any change to parameter redaction rules, blocking XML retention gating, or deadlock graph redaction.

### Explicitly Rejected
- Deriving the new behavior from `--redaction`. The two knobs stay orthogonal: `docs/privacy.md` already documents one sharp edge where `off` means two different things, and overloading `--redaction` would make the privacy policy unexplainable.

---

## 4. Design & Architecture

### 4.1 The Option & Levels

```csharp
namespace SqlFerret.Core.Obfuscation;

public enum SqlTextSanitization
{
    Raw,
    Literals,
    Obfuscated
}
```

| Level | `sql_text_raw` contains | Ingest Cost | Reversible |
|---|---|---|---|
| `Raw` *(default)* | The statement as captured | None | N/A |
| `Literals` | `nq.NormalizedSql` — literals `?`, identifiers real | None (reuses normalizer pass) | No |
| `Obfuscated` | `StatementTextRewriter` output — literals `?`, identifiers tokenized, comments dropped | ~2 extra ScriptDom passes/event | Yes, with `obfuscation_map` |

`Raw` is the default so that existing projects, scripts, and expectations remain unchanged. Privacy is opt-in, as documented in `docs/privacy.md`.

### 4.2 Public Surface Changes

#### 1. `IngestionOptions` (`src/SqlFerret.Core/Ingestion/IngestionOptions.cs`)
Gains a trailing parameter with default value:

```csharp
namespace SqlFerret.Core.Ingestion;

public record IngestionOptions(
    RedactionMode Redaction,
    IReadOnlyList<FilterRule> Filters,
    int BatchSize = 5000,
    string? PlanProfileDir = null,
    SqlTextSanitization SqlText = SqlTextSanitization.Raw);
```

#### 2. `SqlFerretConfig` (`src/SqlFerret.Core/Config/SqlFerretConfig.cs`)
Reads `ingest.sanitizeSqlTextRaw` from `sqlferret.config.json`, default `"raw"`:

```csharp
public record SqlFerretConfig(
    string DurationUnit,
    string CpuUnit,
    string RedactionPolicy,
    string? ConnectionString,
    string PlansFolder,
    string SanitizeSqlTextRaw = "raw")
{
    public static SqlFerretConfig Load(string? jsonPath)
    {
        string durationUnit = "ms", cpuUnit = "ms", redaction = "masked", plans = "./plans";
        string sanitizeSqlText = "raw";
        string? conn = null;

        if (jsonPath is not null && File.Exists(jsonPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("display", out var d))
            {
                if (d.TryGetProperty("durationUnit", out var v)) durationUnit = v.GetString() ?? durationUnit;
                if (d.TryGetProperty("cpuUnit", out var v2)) cpuUnit = v2.GetString() ?? cpuUnit;
            }
            if (root.TryGetProperty("ingest", out var i))
            {
                if (i.TryGetProperty("redactionPolicy", out var r)) redaction = r.GetString() ?? redaction;
                if (i.TryGetProperty("sanitizeSqlTextRaw", out var sstr)) sanitizeSqlText = sstr.GetString() ?? sanitizeSqlText;
            }
            if (root.TryGetProperty("server", out var s))
            {
                if (s.TryGetProperty("connectionString", out var cs)) conn = cs.GetString();
                if (s.TryGetProperty("plansFolder", out var pf)) plans = pf.GetString() ?? plans;
            }
        }

        if (conn is not null)
            conn = Regex.Replace(conn, @"\$\{(\w+)\}",
                m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

        return new SqlFerretConfig(durationUnit, cpuUnit, redaction, conn, plans, sanitizeSqlText);
    }
}
```

#### 3. CLI Host (`src/SqlFerret.Cli/Program.cs`)
Parses `--sanitize-sql-text <raw|literals|obfuscated>`:

```csharp
var sanitizeStr = Arg("--sanitize-sql-text", project.Config.SanitizeSqlTextRaw);
if (!Enum.TryParse<SqlTextSanitization>(sanitizeStr, ignoreCase: true, out var sanitizeSqlText))
{
    Console.Error.WriteLine($"import: invalid --sanitize-sql-text value '{sanitizeStr}'. Valid: raw, literals, obfuscated");
    return 1;
}

var options = new IngestionOptions(
    redaction,
    Array.Empty<FilterRule>(),
    PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()),
    SqlText: sanitizeSqlText);
```

#### 4. TUI Host (`src/SqlFerret.Tui/Presenters/ImportPresenter.cs`)
Passes the configured sanitization policy into `IngestionOptions`:

```csharp
var sanitizeSqlText = Enum.TryParse<SqlTextSanitization>(project.Config.SanitizeSqlTextRaw, ignoreCase: true, out var s)
    ? s
    : SqlTextSanitization.Raw;

var options = new IngestionOptions(
    redaction,
    [],
    PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()),
    SqlText: sanitizeSqlText);
```

---

## 5. Implementation Details

### 5.1 `SqlTextSanitizer` Type (`src/SqlFerret.Core/Obfuscation/SqlTextSanitizer.cs`)

`SqlTextSanitizer` lives in `SqlFerret.Core.Obfuscation` and serves as the single entry point for statement text sanitization.

```csharp
namespace SqlFerret.Core.Obfuscation;

using SqlFerret.Core.Model;

public static class SqlTextSanitizer
{
    public const int Version = 1;
    public const string FallbackRedactedPlaceholder = "(unparseable sql text; redacted)";

    public static (string Text, bool Failed) Apply(
        string raw,
        NormalizedQuery nq,
        SqlTextSanitization level,
        ObfuscationMap? map)
    {
        if (string.IsNullOrEmpty(raw))
            return (string.Empty, false);

        switch (level)
        {
            case SqlTextSanitization.Raw:
                return (raw, false);

            case SqlTextSanitization.Literals:
                if (nq.TokenizeFailed)
                    return (FallbackRedactedPlaceholder, true);
                return (nq.NormalizedSql, false);

            case SqlTextSanitization.Obfuscated:
                if (map is null)
                    throw new ArgumentNullException(nameof(map), "ObfuscationMap is required for Obfuscated sql text sanitization");

                return StatementTextRewriter.RewriteWithStatus(raw, map);

            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }
}
```

### 5.2 `StatementTextRewriter` Enhancement (`src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs`)

Exposes status reporting while maintaining full backward compatibility:

```csharp
public static (string Text, bool FallbackUsed) RewriteWithStatus(string sqlFragment, ObfuscationMap map)
{
    if (string.IsNullOrWhiteSpace(sqlFragment))
        return (sqlFragment ?? string.Empty, false);

    try
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using (var pr = new StringReader(sqlFragment))
        {
            parser.Parse(pr, out IList<ParseError> perr);
            if (perr.Count > 0) return (Fallback(sqlFragment, map), true);
        }
        using var r = new StringReader(sqlFragment);
        IList<TSqlParserToken> tokens = parser.GetTokenStream(r, out IList<ParseError> err);
        if (err.Count > 0) return (Fallback(sqlFragment, map), true);

        var lookup = map.BuildTextLookup();
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            switch (t.TokenType)
            {
                case TSqlTokenType.EndOfFile:
                    continue;
                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                    sb.Append(' ');
                    continue;
                case TSqlTokenType.Variable:
                    if (t.Text.StartsWith("@@", StringComparison.Ordinal))
                        sb.Append(t.Text);
                    else
                        sb.Append(map.Token(NameKind.Parameter, t.Text));
                    continue;
            }
            if (Literals.Contains(t.TokenType)) { sb.Append('?'); continue; }
            if (t.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
            {
                var stripped = ObfuscationMap.Strip(t.Text);
                if (stripped.StartsWith('#'))
                {
                    var tok = map.Token(NameKind.TempTable, ObfuscationMap.NormalizeTempName(stripped));
                    sb.Append(t.TokenType == TSqlTokenType.QuotedIdentifier ? "[" + tok + "]" : tok);
                    continue;
                }
                var key = stripped.ToLowerInvariant();
                if (lookup.TryGetValue(key, out var tok2))
                {
                    sb.Append(t.TokenType == TSqlTokenType.QuotedIdentifier ? "[" + tok2 + "]" : tok2);
                    continue;
                }
            }
            sb.Append(t.Text);
        }
        return (sb.ToString(), false);
    }
    catch
    {
        return (Fallback(sqlFragment, map), true);
    }
}

public static string Rewrite(string sqlFragment, ObfuscationMap map) =>
    RewriteWithStatus(sqlFragment, map).Text;
```

### 5.3 Asymmetric Failure & Fallback Handling

The failure semantics between `Literals` and `Obfuscated` are intentionally asymmetric:

1. **`Literals`:**
   - `QueryNormalizer`'s fallback path (`FallbackCollapse`) leaves literals intact when tokenization fails.
   - Therefore, if `nq.TokenizeFailed == true`, the raw text is unsafe to store.
   - The safe placeholder `"(unparseable sql text; redacted)"` is substituted (matching `PrepareProc`'s existing fallback behavior).
   - `sql_text_sanitize_failures` is incremented.

2. **`Obfuscated`:**
   - `StatementTextRewriter.Fallback` is regex-based and actively scrubs string literals (`'(?:[^']|'')*'`), block/line comments, hex literals (`0x...`), and numeric literals *before* mapping identifiers.
   - Unparseable input degrades to a scrubbed best-effort representation rather than total redaction.
   - `sql_text_sanitize_failures` is incremented to indicate that the event went through a regex fallback path.

### 5.4 Ingestion Pipeline Integration (`src/SqlFerret.Core/Ingestion/IngestionService.cs`)

```csharp
public IngestionResult Ingest(string sourcePath,
    IEnumerable<(IXeEventData ev, string fileName, long offset)> events,
    int filesCount = 1, long bytesTotal = 0,
    IProgress<IngestionProgress>? progress = null)
{
    long runId = project.BeginRun(sourcePath, filesCount, bytesTotal,
        redactionPolicy: options.Redaction.ToString().ToLowerInvariant(),
        sqlTextPolicy: options.SqlText.ToString().ToLowerInvariant());

    ObfuscationMap? map = options.SqlText == SqlTextSanitization.Obfuscated
        ? project.LoadObfuscationMap()
        : null;

    long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
    long blocking = 0, deadlocks = 0, blockingParseFailures = 0;
    long planProfiles = 0, planParseFailures = 0, planWriteFailures = 0;
    long sqlTextSanitizeFailures = 0;

    // ... (blocking & plan profile routing untouched) ...

    foreach (var (ev, fileName, offset) in events)
    {
        // ...
        var e = EventMapper.Map(ev, fileName, offset);
        if (e.EventClass == EventClass.Unknown || string.IsNullOrEmpty(e.SqlTextRaw)) { unmapped++; continue; }
        if (!_ingestKeep(e)) { cleaned++; continue; }

        var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
        if (nq.TokenizeFailed) tokenizeFailures++;

        var (sanitizedText, sanitizeFailed) = SqlTextSanitizer.Apply(e.SqlTextRaw, nq, options.SqlText, map);
        if (sanitizeFailed) sqlTextSanitizeFailures++;

        buffer.Add(new PreparedRow(e with { SqlTextRaw = sanitizedText }, nq, RedactParams(e)));
        mapped++;

        if (buffer.Count >= options.BatchSize)
        {
            project.InsertBatch(runId, buffer); buffer.Clear();
            progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
        }
    }
    if (buffer.Count > 0) project.InsertBatch(runId, buffer);
    if (planBuffer.Count > 0) project.InsertPlanProfileBatch(runId, planBuffer);

    if (map is not null)
        project.SaveObfuscationMap(map);

    progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
    project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures,
        blocking, deadlocks, blockingParseFailures, planProfiles, planParseFailures, planWriteFailures,
        sqlTextSanitizeFailures);

    return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures,
        blocking, deadlocks, blockingParseFailures, planProfiles, planParseFailures, planWriteFailures,
        sqlTextSanitizeFailures);
}
```

> **Fingerprint Invariant:** `QueryNormalizer.Normalize(e.SqlTextRaw)` executes *before* sanitization. Thus, `normalized_hash`, `normalized_queries`, and all workload aggregates keyed on them remain 100% bit-for-bit identical between `Raw`, `Literals`, and `Obfuscated` imports.

---

## 6. Storage Schema & Provenance

### 6.1 Database Schema Migrations (`src/SqlFerret.Core/Storage/DuckDbProject.cs`)

Add three columns to `ingestion_runs` in `CreateSchema`:

```sql
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_policy TEXT;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitizer_version INTEGER;
ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitize_failures BIGINT;
```

Updated `CREATE TABLE IF NOT EXISTS ingestion_runs`:

```sql
CREATE TABLE IF NOT EXISTS ingestion_runs (
  run_id BIGINT PRIMARY KEY, source_path TEXT, files_count INTEGER, bytes_total BIGINT,
  started_at TIMESTAMP, finished_at TIMESTAMP, events_read BIGINT, events_mapped BIGINT,
  events_unmapped BIGINT, events_cleaned BIGINT, tokenize_failures BIGINT,
  events_blocking BIGINT, events_deadlocks BIGINT, blocking_parse_failures BIGINT,
  normalizer_version INTEGER, redaction_policy TEXT,
  events_plan_profiles BIGINT, plan_parse_failures BIGINT, plan_write_failures BIGINT,
  sql_text_policy TEXT, sql_text_sanitizer_version INTEGER, sql_text_sanitize_failures BIGINT);
```

### 6.2 Migration Semantics
- Legacy projects created prior to this feature will have `sql_text_policy IS NULL`, which reads as `raw`.
- `sql_text_sanitizer_version IS NULL` reads as "pre-versioning".
- Historical `executions.sql_text_raw` rows are preserved untouched.
- `executions` table schema remains completely unchanged (`InsertExecution` uses positional `INSERT INTO executions VALUES (...)`, so keeping `executions` columns intact guarantees zero regressions).

### 6.3 `BeginRun` and `FinishRun` Updates
- `BeginRun` accepts `string sqlTextPolicy = "raw"` and inserts `$stp` and `$stv` (`SqlTextSanitizer.Version`).
- `FinishRun` accepts `long sqlTextSanitizeFailures = 0` and updates `sql_text_sanitize_failures = $stsf`.
- `IngestionResult` adds `long SqlTextSanitizeFailures = 0` as a trailing parameter.

---

## 7. Estimated Plans Integration (`EstimatedPlanService`)

### 7.1 Replay Incompatibility with Sanitized Text
- When `SqlTextRaw` is sanitized, inlined literals are replaced with `?` or tokens (`SELECT * FROM Customers WHERE Email = ?`).
- T-SQL batches containing bare `?` parameter markers are invalid syntax and cannot be compiled by SQL Server under `SET SHOWPLAN_XML ON`.

### 7.2 Explicit Refusal Implementation
1. `ExecutionEvent` (`src/SqlFerret.Core/Model/ExecutionEvent.cs`):
   ```csharp
   public string? SqlTextPolicy { get; init; }
   ```
2. `WorkloadQueries.LoadExecution` (`src/SqlFerret.Core/Analysis/WorkloadQueries.cs`):
   ```csharp
   public ExecutionEvent LoadExecution(long executionId)
   {
       using var cmd = conn.CreateCommand();
       cmd.CommandText = """
         SELECT e.event_name, e.event_class, e.object_name, e.database_name, e.login_name,
                e.client_hostname, e.client_app_name, e.session_id, e.captured_at, e.duration_us,
                e.sql_text_raw, e.xe_file_name, e.file_offset, r.sql_text_policy
         FROM executions e
         JOIN ingestion_runs r ON e.run_id = r.run_id
         WHERE e.execution_id = $id
         """;
       Add(cmd, "$id", executionId);
       // ... reads r.sql_text_policy (index 13) and sets SqlTextPolicy on ExecutionEvent ...
   }
   ```
3. `EstimatedPlanService.CaptureAsync` (`src/SqlFerret.Core/Server/EstimatedPlanService.cs`):
   ```csharp
   public async Task<string> CaptureAsync(ExecutionEvent ev, string planId, CancellationToken ct = default)
   {
       if (!string.IsNullOrEmpty(ev.SqlTextPolicy) &&
           !string.Equals(ev.SqlTextPolicy, "raw", StringComparison.OrdinalIgnoreCase))
       {
           throw new InvalidOperationException(
               $"estimated plan: this execution was imported with --sanitize-sql-text {ev.SqlTextPolicy}; " +
               $"the stored statement text is not executable. Re-import with --sanitize-sql-text raw to capture estimated plans.");
       }

       ReplayScript script = ReplayBuilder.Build(ev);
       // ... (proceeds with SqlConnection and SET SHOWPLAN_XML ON) ...
   }
   ```

---

## 8. Dependency Graph Update (`CLAUDE.md`)

Section A & B of `CLAUDE.md` declare:
```text
Model ← Normalization / Parameters / Filtering ← Ingestion / Storage / Analysis / Plans / Obfuscation / Replay / Server ← hosts
```

With `Ingestion` now utilizing `SqlTextSanitizer` and `ObfuscationMap`, `Ingestion` depends on `Obfuscation`.
Updated Core dependency declaration:
```text
Model ← Normalization / Parameters / Filtering ← Obfuscation ← Ingestion / Storage / Analysis / Plans / Replay / Server ← hosts
```

---

## 9. Performance & Ingest Cost

- **`Literals` Mode:** Zero additional parsing overhead. Reuses the `NormalizedQuery` already produced by `QueryNormalizer.Normalize`.
- **`Obfuscated` Mode:** Calls `StatementTextRewriter.RewriteWithStatus`, performing `parser.Parse()` (for semantic validation) and `parser.GetTokenStream()`. This adds ~2 ScriptDom passes per event. Ingestion throughput is expected to be ~40-50% slower at this level.
- Documented in `docs/development.md#known-gaps` as part of the ScriptDom hot-path tracking item.

---

## 10. Comprehensive Test Matrix

Testing follows TDD (red-to-green), patterned after `BlockingIngestionTests.cs`:

| Test Name | Fixture / Location | Asserts |
|---|---|---|
| `Raw_KeepsStatementVerbatim` | Ingestion unit tests | Default import stores exact original SQL with literals |
| `Literals_RemovesInlinedLiteral` | Ingestion unit tests | `sql_text_raw` replaces literal with `?`; original literal is absent |
| `Literals_PreservesIdentifiers` | Ingestion unit tests | Table and column names are kept intact in `sql_text_raw` |
| `Obfuscated_TokenizesIdentifiers` | Ingestion unit tests | Identifiers replaced with `Table1`, `Col1`, `@Param1`; literals replaced with `?` |
| `Obfuscated_PopulatesProjectMap` | Storage / Ingestion tests | `obfuscation_map` table in DuckDB contains entries after run |
| `Obfuscated_ReusesExistingTokens` | Ingestion multi-run tests | Second import run preserves and reuses existing tokens from earlier run |
| `Sanitization_DoesNotChangeFingerprint` | Ingestion comparison test | `normalized_hash` in `executions` and `normalized_queries` is identical across `Raw`, `Literals`, and `Obfuscated` |
| `Literals_UnparseableUsesPlaceholder` | Ingestion fake event test | Unparseable SQL stores `(unparseable sql text; redacted)`; `sql_text_sanitize_failures` incremented |
| `Obfuscated_UnparseableScrubsLiterals` | Ingestion fake event test | Fallback regex path strips literals without crashing; `sql_text_sanitize_failures` incremented |
| `Run_RecordsPolicyAndVersion` | Storage test | `ingestion_runs.sql_text_policy`, `sql_text_sanitizer_version`, and `sql_text_sanitize_failures` correctly persisted |
| `LegacyProject_MigratesAndReadsAsRaw` | Migration test | Opening pre-existing `.duckdb` creates columns without error; `NULL` policy reads as `raw` |
| `EstimatedPlan_RefusesOnSanitizedRun` | `EstimatedPlanServiceTests` | Calling `CaptureAsync` on execution with policy `literals` or `obfuscated` throws `InvalidOperationException` with refusal message |
| `Cli_SanitizeSqlText_Flag_Parsed` | CLI / Smoke test | `--sanitize-sql-text literals` correctly passed to `IngestionOptions` |
| `Config_SanitizeSqlTextRaw_Loaded` | Config tests | `ingest.sanitizeSqlTextRaw` loaded correctly from JSON configuration |

---

## 11. Documentation Updates Checklist

1. **`docs/privacy.md`:**
   - Update the "What lands on disk" table: `executions.sql_text_raw` is controlled by `--sanitize-sql-text`.
   - Update "The one thing to internalize" section to explain `--sanitize-sql-text`.
   - Add explanation of `Raw`, `Literals`, and `Obfuscated` levels.
   - Add prominent security warning: **At `Obfuscated` level, `obfuscation_map` is stored in the `.duckdb` database.** Sharing the project file requires dropping `obfuscation_map` if de-anonymization must be prevented.
2. **`docs/cli-reference.md`:**
   - Document `--sanitize-sql-text <raw|literals|obfuscated>` flag for `import`.
3. **`docs/configuration.md`:**
   - Document `ingest.sanitizeSqlTextRaw` option in `sqlferret.config.json`.
4. **`docs/data-model.md`:**
   - Document `sql_text_policy`, `sql_text_sanitizer_version`, and `sql_text_sanitize_failures` in `ingestion_runs`.
   - Document NULL migration semantics.
5. **`docs/execution-plans.md`:**
   - Document `EstimatedPlanService` refusal behavior on sanitized executions.
6. **`docs/development.md#known-gaps`:**
   - Note ScriptDom parse pass overhead during `Obfuscated` ingestion.
7. **`CLAUDE.md`:**
   - Update Core dependency line (`Obfuscation` before `Ingestion`).
   - Add `SqlTextSanitizer.Version = 1` invariant rule.

---

## 12. Verification & Validation Plan

1. **Build Validation:** `dotnet build` with 0 warnings.
2. **Unit & Integration Tests:** Run full test suite `dotnet test`. All non-skipped tests must pass.
3. **Code Formatting:** `dotnet format` scoped to touched files.
