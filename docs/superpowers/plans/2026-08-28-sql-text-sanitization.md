# SQL Text Sanitization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in ingest-time option that stores literal-free statement text in
`executions.sql_text_raw` (and in `normalized_queries.normalized_sql`), recorded per run with its
own version constant.

**Architecture:** A pure function `SqlTextSanitizer.Apply` in `SqlFerret.Core.Normalization`
rewrites the text where `IngestionService` already builds each `PreparedRow`, reusing the
`NormalizedQuery` the normalizer computed anyway — no new parse, no new namespace dependency.
Provenance lands in three new `ingestion_runs` columns added through the existing
`ADD COLUMN IF NOT EXISTS` migration block. Because sanitized text is not executable T-SQL,
`EstimatedPlanService` learns the policy through a new field on `ExecutionEvent` and refuses
before opening a connection.

**Tech Stack:** .NET 10 / C# 14, DuckDB.NET.Data.Full 1.5.3, ScriptDom 180.37.3 (already used by
the normalizer; this change adds no call to it), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-28-sanitize-sql-text-raw-design.md` (revision 2)

## Global Constraints

- **Do not add the `Ingestion → Obfuscation` dependency edge.** Revision 1 of the spec approved
  it; revision 2 dropped the level that needed it. `SqlTextSanitizer` goes in `Normalization`,
  which `Ingestion` already depends on. `CLAUDE.md`'s dependency line is not modified.
- **Nothing in `src/SqlFerret.Core/Obfuscation/` is touched by this plan.**
- **Branch first.** `CLAUDE.md` C.8: never commit on `main` without consent. Run
  `git checkout -b feat/sql-text-sanitization` before Task 1, and confirm with the user that
  committing is wanted at all — the commit steps below assume yes.
- **Every new parameter is trailing and defaulted.** `BeginRun` has 21 four-argument call sites in
  tests and `FinishRun` has one nine-argument call site; a non-defaulted parameter breaks them all.
- **DuckDB.NET 1.5.3 parameter names carry no leading `$`.** SQL uses `$name`; the `Add` helper
  already does `name.TrimStart('$')`. Use the existing `Add` helper, never a raw parameter.
- **Microseconds stay in Core.** This change touches no duration value.
- **No new NuGet dependency. `net10.0`. 0 build warnings.**
- **C# 14 style as used in the file you are editing**: collection expressions `[]`, raw string
  literals `"""` for SQL, `record` with `init`, primary constructors.
- **The exact placeholder string is** `"(unparseable sql text; redacted)"` — distinct from the
  existing `"(unparseable inputbuf; redacted)"` used for blocking input buffers.
- **One name per concept.** Enum type `SqlTextSanitization` (values `Raw`, `Literals`); the
  string form is `SqlTextPolicy` everywhere it is carried as text — `SqlFerretConfig.SqlTextPolicy`,
  `ExecutionEvent.SqlTextPolicy`, `ingestion_runs.sql_text_policy`. Config key is
  `ingest.sqlTextSanitization`; CLI flag is `--sanitize-sql-text`; on-disk and on-the-wire values
  are lowercase `raw` / `literals`. Never name a member the same as the enum type.
- `dotnet format` scoped to touched files only, never the whole tree.

---

### Task 1: `SqlTextSanitizer` — the pure rewrite

**Files:**
- Create: `src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs`
- Test: `tests/SqlFerret.Core.Tests/SqlTextSanitizerTests.cs`

**Interfaces:**
- Consumes: `SqlFerret.Core.Model.NormalizedQuery(string NormalizedSql, string NormalizedHash,
  string StatementKind, string? PrimaryTable, bool TokenizeFailed)` — existing record.
- Produces:
  - `enum SqlFerret.Core.Normalization.SqlTextSanitization { Raw, Literals }`
  - `SqlTextSanitizer.Version` — `const int`, value `1`
  - `SqlTextSanitizer.Placeholder` — `const string`
  - `(string Text, NormalizedQuery Normalized, bool Failed) SqlTextSanitizer.Apply(string raw,
    NormalizedQuery nq, SqlTextSanitization level)`

This file mirrors `src/SqlFerret.Core/Parameters/RedactionPolicy.cs`, which likewise holds an enum
and its policy type together.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/SqlTextSanitizerTests.cs`:

```csharp
// tests/SqlFerret.Core.Tests/SqlTextSanitizerTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using Xunit;

public class SqlTextSanitizerTests
{
    private const string Pii = "alice@example.com";

    [Fact]
    public void Raw_returns_the_statement_and_the_normalized_query_untouched()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Email = 'alice@example.com'";
        var nq = QueryNormalizer.Normalize(sql);

        var (text, normalized, failed) = SqlTextSanitizer.Apply(sql, nq, SqlTextSanitization.Raw);

        Assert.Equal(sql, text);
        Assert.Same(nq, normalized);
        Assert.False(failed);
    }

    [Fact]
    public void Literals_stores_the_normalized_sql_and_drops_the_value()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Email = 'alice@example.com'";
        var nq = QueryNormalizer.Normalize(sql);
        Assert.False(nq.TokenizeFailed);   // guards the premise of this test

        var (text, normalized, failed) = SqlTextSanitizer.Apply(sql, nq, SqlTextSanitization.Literals);

        Assert.Equal(nq.NormalizedSql, text);
        Assert.DoesNotContain(Pii, text);
        Assert.Contains("?", text);
        Assert.False(failed);
    }

    [Fact]
    public void Literals_keeps_identifiers_because_it_removes_values_not_schema()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Email = 'alice@example.com'";
        var nq = QueryNormalizer.Normalize(sql);

        var (text, _, _) = SqlTextSanitizer.Apply(sql, nq, SqlTextSanitization.Literals);

        Assert.Contains("Customers", text);
    }

    // ScriptDom fails on an unterminated string literal; TokenNormalizer then falls back to
    // FallbackCollapse, which only lowercases and collapses whitespace — the literal survives.
    // Both the statement text AND the normalized SQL must be replaced by the placeholder.
    [Fact]
    public void Literals_on_tokenize_failure_scrubs_text_and_normalized_sql()
    {
        const string truncated = $"exec dbo.X @Email='{Pii}";   // no closing quote
        var nq = QueryNormalizer.Normalize(truncated);
        Assert.True(nq.TokenizeFailed);           // guards the premise of this test
        Assert.Contains(Pii, nq.NormalizedSql);   // documents why the guard is needed

        var (text, normalized, failed) = SqlTextSanitizer.Apply(truncated, nq, SqlTextSanitization.Literals);

        Assert.Equal(SqlTextSanitizer.Placeholder, text);
        Assert.Equal(SqlTextSanitizer.Placeholder, normalized.NormalizedSql);
        Assert.DoesNotContain(Pii, normalized.NormalizedSql);
        Assert.True(failed);
    }

    [Fact]
    public void Literals_on_tokenize_failure_preserves_the_fingerprint()
    {
        const string truncated = $"exec dbo.X @Email='{Pii}";
        var nq = QueryNormalizer.Normalize(truncated);

        var (_, normalized, _) = SqlTextSanitizer.Apply(truncated, nq, SqlTextSanitization.Literals);

        Assert.Equal(nq.NormalizedHash, normalized.NormalizedHash);
        Assert.Equal(nq.StatementKind, normalized.StatementKind);
    }

    [Fact]
    public void Version_is_one()
    {
        Assert.Equal(1, SqlTextSanitizer.Version);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter SqlTextSanitizerTests`
Expected: build failure — `SqlTextSanitizer` and `SqlTextSanitization` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs`:

```csharp
// src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Normalization;

/// <summary>How much of the captured statement text is written to disk.</summary>
public enum SqlTextSanitization
{
    /// <summary>The statement exactly as captured, inlined literals included. Historical default.</summary>
    Raw,
    /// <summary>The normalized form: literals collapsed to '?'. Identifiers are NOT removed.</summary>
    Literals,
}

/// <summary>
/// Rewrites statement text before it reaches storage. Reuses the <see cref="NormalizedQuery"/>
/// the ingestion loop already computed, so no level costs an additional parse.
/// </summary>
public static class SqlTextSanitizer
{
    /// <summary>
    /// Bump when the rewrite rules change: text produced under two versions is not comparable.
    /// Persisted per run on <c>ingestion_runs.sql_text_sanitizer_version</c>. Coupled to
    /// <see cref="QueryNormalizer.Version"/> — bumping that one changes sanitized text too.
    /// </summary>
    public const int Version = 1;

    /// <summary>Substituted when tokenization failed and the fallback left literals intact.</summary>
    public const string Placeholder = "(unparseable sql text; redacted)";

    /// <summary>
    /// Returns the text to store, the <see cref="NormalizedQuery"/> to store alongside it, and
    /// whether the unsafe-fallback path was taken.
    /// </summary>
    /// <remarks>
    /// <see cref="TokenNormalizer"/>'s fallback only lowercases and collapses whitespace, so a
    /// tokenize failure leaves literals intact in <see cref="NormalizedQuery.NormalizedSql"/> as
    /// well as in the raw text. Protecting only the raw text would move the literal into
    /// <c>normalized_queries</c>, which lives in the same file and joins on the same hash.
    /// <see cref="NormalizedQuery.NormalizedHash"/> is a non-reversible hash and is preserved so
    /// fingerprint joins keep working. Mirrors <c>IngestionService.PrepareProc</c>.
    /// </remarks>
    public static (string Text, NormalizedQuery Normalized, bool Failed) Apply(
        string raw, NormalizedQuery nq, SqlTextSanitization level)
    {
        if (level == SqlTextSanitization.Raw) return (raw, nq, false);
        if (nq.TokenizeFailed) return (Placeholder, nq with { NormalizedSql = Placeholder }, true);
        return (nq.NormalizedSql, nq, false);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter SqlTextSanitizerTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Format and commit**

```bash
dotnet format --include src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs tests/SqlFerret.Core.Tests/SqlTextSanitizerTests.cs
git add src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs tests/SqlFerret.Core.Tests/SqlTextSanitizerTests.cs
git commit -m "feat(normalization): add SqlTextSanitizer with Raw and Literals levels"
```

---

### Task 2: Persist the policy, the version and the failure count

**Files:**
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs` — migration block at lines 73-76,
  `BeginRun` at 103-121, `FinishRun` at 184-202
- Test: `tests/SqlFerret.Core.Tests/SqlTextPolicyStorageTests.cs` (create)

**Interfaces:**
- Consumes: `SqlTextSanitization` from Task 1.
- Produces:
  - `long BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy,
    SqlTextSanitization sqlText = SqlTextSanitization.Raw)`
  - `void FinishRun(…, long planWriteFailures = 0, long sqlTextSanitizeFailures = 0)`
  - Columns `ingestion_runs.sql_text_policy` (TEXT), `.sql_text_sanitizer_version` (INTEGER),
    `.sql_text_sanitize_failures` (BIGINT)

Both new parameters are **trailing and defaulted**: 21 test call sites pass `BeginRun` four
arguments and `DuckDbProjectInsertTests.cs:33` passes `FinishRun` nine.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/SqlTextPolicyStorageTests.cs`:

```csharp
// tests/SqlFerret.Core.Tests/SqlTextPolicyStorageTests.cs
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Storage;
using Xunit;

public class SqlTextPolicyStorageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    [Fact]
    public void BeginRun_records_the_policy_and_the_sanitizer_version()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked", SqlTextSanitization.Literals);

            using var c = db.Connection.CreateCommand();
            c.CommandText =
                "SELECT sql_text_policy, sql_text_sanitizer_version FROM ingestion_runs WHERE run_id = " + run;
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("literals", r.GetString(0));
            Assert.Equal(SqlTextSanitizer.Version, r.GetInt32(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BeginRun_defaults_to_raw()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked");

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT sql_text_policy FROM ingestion_runs WHERE run_id = " + run;
            Assert.Equal("raw", (string)c.ExecuteScalar()!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FinishRun_records_the_sanitize_failure_count()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked", SqlTextSanitization.Literals);
            db.FinishRun(run, read: 1, mapped: 1, unmapped: 0, cleaned: 0, tokenizeFailures: 1,
                blocking: 0, deadlocks: 0, blockingParseFailures: 0, sqlTextSanitizeFailures: 1);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT sql_text_sanitize_failures FROM ingestion_runs WHERE run_id = " + run;
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Simulates a project created before this change: drop the three columns from an open
    // project, insert a legacy-shaped row, close, reopen. Initialize must re-add them and the
    // historical row must read as NULL — which callers interpret as "raw, pre-versioning".
    [Fact]
    public void Reopening_a_project_without_the_columns_migrates_and_reads_null()
    {
        var path = TempDb();
        try
        {
            long run;
            using (var db = DuckDbProject.Open(path))
            {
                run = db.BeginRun("logs/", 1, 0, "masked");
                using var drop = db.Connection.CreateCommand();
                drop.CommandText = """
                  ALTER TABLE ingestion_runs DROP COLUMN sql_text_policy;
                  ALTER TABLE ingestion_runs DROP COLUMN sql_text_sanitizer_version;
                  ALTER TABLE ingestion_runs DROP COLUMN sql_text_sanitize_failures;
                  """;
                drop.ExecuteNonQuery();
            }

            using var reopened = DuckDbProject.Open(path);
            using var c = reopened.Connection.CreateCommand();
            c.CommandText =
                "SELECT sql_text_policy, sql_text_sanitizer_version FROM ingestion_runs WHERE run_id = " + run;
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.True(r.IsDBNull(0));
            Assert.True(r.IsDBNull(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter SqlTextPolicyStorageTests`
Expected: build failure — `BeginRun` has no fifth parameter, `FinishRun` no
`sqlTextSanitizeFailures`.

- [ ] **Step 3: Add the three columns to the migration block**

In `src/SqlFerret.Core/Storage/DuckDbProject.cs`, the existing block ends at line 76 with
`plan_write_failures`. Append three statements inside the same raw string literal, before the
closing `"""`:

```csharp
              ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS events_plan_profiles BIGINT;
              ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS plan_parse_failures BIGINT;
              ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS plan_write_failures BIGINT;
              ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_policy TEXT;
              ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitizer_version INTEGER;
              ALTER TABLE ingestion_runs ADD COLUMN IF NOT EXISTS sql_text_sanitize_failures BIGINT;
              """;
```

Do **not** add a column to `executions`: `InsertExecution` uses
`INSERT INTO executions VALUES (…)` with no column list, so a new column there shifts every
positional binding silently.

- [ ] **Step 4: Extend `BeginRun`**

Replace the method at lines 103-121 with:

```csharp
    public long BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy,
        SqlTextSanitization sqlText = SqlTextSanitization.Raw)
    {
        if (_nextRunId < 0) _nextRunId = Scalar("SELECT COALESCE(MAX(run_id),0) FROM ingestion_runs") + 1;
        long runId = _nextRunId++;
        using var c = Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO ingestion_runs(run_id, source_path, files_count, bytes_total, started_at,
            finished_at, events_read, events_mapped, events_unmapped, events_cleaned,
            tokenize_failures, events_blocking, events_deadlocks, blocking_parse_failures,
            normalizer_version, redaction_policy,
            events_plan_profiles, plan_parse_failures, plan_write_failures,
            sql_text_policy, sql_text_sanitizer_version, sql_text_sanitize_failures)
          VALUES ($id,$src,$fc,$bt, now(), NULL, 0,0,0,0,0,0,0,0, $nv, $rp, 0,0,0, $stp, $stv, 0)
          """;
        Add(c, "$id", runId); Add(c, "$src", sourcePath); Add(c, "$fc", filesCount);
        Add(c, "$bt", bytesTotal); Add(c, "$nv", QueryNormalizer.Version); Add(c, "$rp", redactionPolicy);
        Add(c, "$stp", sqlText.ToString().ToLowerInvariant()); Add(c, "$stv", SqlTextSanitizer.Version);
        c.ExecuteNonQuery();
        return runId;
    }
```

The file already has `using SqlFerret.Core.Normalization;` for `QueryNormalizer` — verify it, and
add it if absent.

- [ ] **Step 5: Extend `FinishRun`**

At line 184, add the trailing parameter and the column assignment:

```csharp
    public void FinishRun(long runId, long read, long mapped, long unmapped, long cleaned,
        long tokenizeFailures, long blocking, long deadlocks, long blockingParseFailures,
        long planProfiles = 0, long planParseFailures = 0, long planWriteFailures = 0,
        long sqlTextSanitizeFailures = 0)
    {
        using var c = Connection.CreateCommand();
        c.CommandText = """
          UPDATE ingestion_runs SET finished_at=now(), events_read=$r, events_mapped=$m,
            events_unmapped=$u, events_cleaned=$c, tokenize_failures=$tf,
            events_blocking=$bl, events_deadlocks=$dl, blocking_parse_failures=$bpf,
            events_plan_profiles=$pp, plan_parse_failures=$ppf, plan_write_failures=$pwf,
            sql_text_sanitize_failures=$stsf
          WHERE run_id=$id
          """;
        Add(c, "$r", read); Add(c, "$m", mapped); Add(c, "$u", unmapped); Add(c, "$c", cleaned);
        Add(c, "$tf", tokenizeFailures); Add(c, "$bl", blocking); Add(c, "$dl", deadlocks);
        Add(c, "$bpf", blockingParseFailures);
        Add(c, "$pp", planProfiles); Add(c, "$ppf", planParseFailures); Add(c, "$pwf", planWriteFailures);
        Add(c, "$stsf", sqlTextSanitizeFailures);
        Add(c, "$id", runId);
        c.ExecuteNonQuery();
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter SqlTextPolicyStorageTests`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run the storage suites for regressions**

Run: `dotnet test --filter "DuckDbProjectInsertTests|PlanStorageTests|PlanDigestTests"`
Expected: PASS, no change in counts. These are the suites that call `BeginRun` and `FinishRun`
positionally.

- [ ] **Step 8: Format and commit**

```bash
dotnet format --include src/SqlFerret.Core/Storage/DuckDbProject.cs tests/SqlFerret.Core.Tests/SqlTextPolicyStorageTests.cs
git add src/SqlFerret.Core/Storage/DuckDbProject.cs tests/SqlFerret.Core.Tests/SqlTextPolicyStorageTests.cs
git commit -m "feat(storage): record sql text policy, sanitizer version and failure count per run"
```

---

### Task 3: Wire the sanitizer into ingestion

**Files:**
- Modify: `src/SqlFerret.Core/Ingestion/IngestionOptions.cs`
- Modify: `src/SqlFerret.Core/Ingestion/IngestionResult.cs`
- Modify: `src/SqlFerret.Core/Ingestion/IngestionService.cs:20-21` (BeginRun call), `:23` (counter
  declarations), `:86-89` (the PreparedRow build), `:101-105` (FinishRun and the result)
- Test: `tests/SqlFerret.Core.Tests/SqlTextSanitizationIngestionTests.cs` (create)

**Interfaces:**
- Consumes: `SqlTextSanitizer.Apply` (Task 1), `BeginRun`/`FinishRun` overloads (Task 2).
- Produces:
  - `IngestionOptions(…, SqlTextSanitization SqlText = SqlTextSanitization.Raw)`
  - `IngestionResult(…, long SqlTextSanitizeFailures = 0)`

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/SqlTextSanitizationIngestionTests.cs`:

```csharp
// tests/SqlFerret.Core.Tests/SqlTextSanitizationIngestionTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class SqlTextSanitizationIngestionTests
{
    private const string Pii = "alice@example.com";

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Batch(string sql, long offset = 0) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
            new Dictionary<string, object?> { ["batch_text"] = sql, ["duration"] = 1000L },
            new Dictionary<string, object?> { ["database_name"] = "Sales", ["session_id"] = 1 }),
         "s_0.xel", offset);

    private static string Scalar(DuckDbProject db, string sql)
    {
        using var c = db.Connection.CreateCommand();
        c.CommandText = sql;
        return (string)c.ExecuteScalar()!;
    }

    [Fact]
    public void Raw_keeps_the_statement_verbatim()
    {
        const string sql = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Batch(sql)]);

            Assert.Equal(sql, Scalar(db, "SELECT sql_text_raw FROM executions"));
            // Guards against the two counters double-counting one event.
            Assert.Equal(0, result.SqlTextSanitizeFailures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Literals_removes_the_inlined_literal_from_executions()
    {
        const string sql = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch(sql)]);

            var stored = Scalar(db, "SELECT sql_text_raw FROM executions");
            Assert.DoesNotContain(Pii, stored);
            Assert.Contains("?", stored);
            Assert.Contains("Customers", stored);   // identifiers survive: values, not schema
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The regression test for the defect found in revision 1 of the design: protecting only
    // sql_text_raw moves the literal into normalized_queries, in the same file, joinable on
    // normalized_hash. This test is not optional.
    [Fact]
    public void Literals_on_tokenize_failure_scrubs_normalized_queries_too()
    {
        const string truncated = $"exec dbo.X @Email='{Pii}";   // unterminated string literal
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch(truncated)]);

            Assert.DoesNotContain(Pii, Scalar(db, "SELECT sql_text_raw FROM executions"));
            Assert.DoesNotContain(Pii, Scalar(db, "SELECT normalized_sql FROM normalized_queries"));
            Assert.Equal(1, result.TokenizeFailures);
            Assert.Equal(1, result.SqlTextSanitizeFailures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Sanitization_does_not_change_the_fingerprint()
    {
        const string sql = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";
        var rawPath = TempDb();
        var litPath = TempDb();
        try
        {
            string rawHash, litHash;
            using (var db = DuckDbProject.Open(rawPath))
            {
                new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                    .Ingest("logs/", [Batch(sql)]);
                rawHash = Scalar(db, "SELECT normalized_hash FROM executions");
            }
            using (var db = DuckDbProject.Open(litPath))
            {
                new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                        SqlText: SqlTextSanitization.Literals))
                    .Ingest("logs/", [Batch(sql)]);
                litHash = Scalar(db, "SELECT normalized_hash FROM executions");
            }
            Assert.Equal(rawHash, litHash);
        }
        finally
        {
            if (File.Exists(rawPath)) File.Delete(rawPath);
            if (File.Exists(litPath)) File.Delete(litPath);
        }
    }

    [Fact]
    public void Ingest_records_the_policy_on_the_run()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch("SELECT 1")]);

            Assert.Equal("literals",
                Scalar(db, "SELECT sql_text_policy FROM ingestion_runs WHERE run_id = " + result.RunId));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter SqlTextSanitizationIngestionTests`
Expected: build failure — `IngestionOptions` has no `SqlText`, `IngestionResult` no
`SqlTextSanitizeFailures`.

- [ ] **Step 3: Extend the two records**

`src/SqlFerret.Core/Ingestion/IngestionOptions.cs` — add the `using` and the trailing member:

```csharp
// src/SqlFerret.Core/Ingestion/IngestionOptions.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;

namespace SqlFerret.Core.Ingestion;

public record IngestionOptions(RedactionMode Redaction, IReadOnlyList<FilterRule> Filters,
    int BatchSize = 5000, string? PlanProfileDir = null,
    SqlTextSanitization SqlText = SqlTextSanitization.Raw);
```

`src/SqlFerret.Core/Ingestion/IngestionResult.cs`:

```csharp
// src/SqlFerret.Core/Ingestion/IngestionResult.cs
namespace SqlFerret.Core.Ingestion;

public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned,
    long TokenizeFailures, long Blocking, long Deadlocks, long BlockingParseFailures,
    long PlanProfiles = 0, long PlanParseFailures = 0, long PlanWriteFailures = 0,
    long SqlTextSanitizeFailures = 0);
```

- [ ] **Step 4: Wire `IngestionService`**

Four edits in `src/SqlFerret.Core/Ingestion/IngestionService.cs`.

(a) The `BeginRun` call at lines 20-21:

```csharp
        long runId = project.BeginRun(sourcePath, filesCount, bytesTotal,
            redactionPolicy: options.Redaction.ToString().ToLowerInvariant(),
            sqlText: options.SqlText);
```

(b) The counter declaration at line 23:

```csharp
        long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
        long sqlTextSanitizeFailures = 0;
```

(c) The `PreparedRow` build at lines 86-89 — replace:

```csharp
            var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
            if (nq.TokenizeFailed) tokenizeFailures++;

            buffer.Add(new PreparedRow(e, nq, RedactParams(e)));
            mapped++;
```

with:

```csharp
            var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
            if (nq.TokenizeFailed) tokenizeFailures++;

            // Sanitize before the row is built, so unredacted literals never reach storage.
            // safeNq carries the substituted NormalizedSql: on tokenize failure the normalizer's
            // fallback leaves literals intact, and normalized_queries lives in the same file.
            var (sqlText, safeNq, sanitizeFailed) =
                SqlTextSanitizer.Apply(e.SqlTextRaw, nq, options.SqlText);
            if (sanitizeFailed) sqlTextSanitizeFailures++;

            buffer.Add(new PreparedRow(e with { SqlTextRaw = sqlText }, safeNq, RedactParams(e)));
            mapped++;
```

(d) `FinishRun` and the returned result at lines 101-105:

```csharp
        project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures,
            blocking, deadlocks, blockingParseFailures, planProfiles, planParseFailures, planWriteFailures,
            sqlTextSanitizeFailures);
        return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures,
            blocking, deadlocks, blockingParseFailures, planProfiles, planParseFailures, planWriteFailures,
            sqlTextSanitizeFailures);
```

`IngestionService.cs` already has `using SqlFerret.Core.Normalization;` at line 4, so
`SqlTextSanitizer` resolves without a new import.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter SqlTextSanitizationIngestionTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: the pre-change baseline of 377 passing plus the 15 tests added by Tasks 1-3, and the
same 10 skips. The skips are environment gates (`sample/` absent, `SQLFERRET_TEST_CONN` unset),
not regressions.

- [ ] **Step 7: Format and commit**

```bash
dotnet format --include src/SqlFerret.Core/Ingestion/IngestionService.cs src/SqlFerret.Core/Ingestion/IngestionOptions.cs src/SqlFerret.Core/Ingestion/IngestionResult.cs tests/SqlFerret.Core.Tests/SqlTextSanitizationIngestionTests.cs
git add src/SqlFerret.Core/Ingestion tests/SqlFerret.Core.Tests/SqlTextSanitizationIngestionTests.cs
git commit -m "feat(ingestion): apply sql text sanitization before rows reach storage"
```

---

### Task 4: Expose the option in the config and both hosts

**Files:**
- Modify: `src/SqlFerret.Core/Config/SqlFerretConfig.cs`
- Modify: `src/SqlFerret.Cli/Program.cs:41` (usage string), `:59-68` (the import block)
- Modify: `src/SqlFerret.Tui/Presenters/ImportPresenter.cs:16-17`
- Test: `tests/SqlFerret.Core.Tests/SqlFerretConfigSqlTextTests.cs` (create)

**Interfaces:**
- Consumes: `SqlTextSanitization` (Task 1), `IngestionOptions.SqlText` (Task 3).
- Produces: `SqlFerretConfig.SqlTextPolicy` — a `string`, default `"raw"`, read from
  `ingest.sqlTextSanitization`.

The config member is a `string`, not the enum, matching how `RedactionPolicy` is already carried
as a string and parsed at the host boundary.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/SqlFerretConfigSqlTextTests.cs`:

```csharp
// tests/SqlFerret.Core.Tests/SqlFerretConfigSqlTextTests.cs
using SqlFerret.Core.Config;
using Xunit;

public class SqlFerretConfigSqlTextTests
{
    [Fact]
    public void Defaults_to_raw_when_the_key_is_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "ingest": { "redactionPolicy": "hash" } }""");
            var cfg = SqlFerretConfig.Load(path);
            Assert.Equal("raw", cfg.SqlTextPolicy);
            Assert.Equal("hash", cfg.RedactionPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reads_the_configured_level()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "ingest": { "sqlTextSanitization": "literals" } }""");
            Assert.Equal("literals", SqlFerretConfig.Load(path).SqlTextPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_to_raw_when_there_is_no_config_file()
    {
        Assert.Equal("raw", SqlFerretConfig.Load(null).SqlTextPolicy);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter SqlFerretConfigSqlTextTests`
Expected: build failure — `SqlFerretConfig` has no `SqlTextPolicy` member.

- [ ] **Step 3: Extend `SqlFerretConfig`**

Two edits in `src/SqlFerret.Core/Config/SqlFerretConfig.cs`. The record header gains a trailing
defaulted member (non-breaking: five-argument positional construction still compiles):

```csharp
public record SqlFerretConfig(string DurationUnit, string CpuUnit, string RedactionPolicy,
    string? ConnectionString, string PlansFolder, string SqlTextPolicy = "raw")
```

and the `ingest` block inside `Load` — the current single-line form reads one key, so it becomes:

Declare the new local beside the existing ones at the top of `Load`, so it is in scope for the
return when no config file exists:

```csharp
            string durationUnit = "ms", cpuUnit = "ms", redaction = "masked", plans = "./plans";
            string sqlTextPolicy = "raw";
```

```csharp
            if (root.TryGetProperty("ingest", out var i))
            {
                if (i.TryGetProperty("redactionPolicy", out var r))
                    redaction = r.GetString() ?? redaction;
                if (i.TryGetProperty("sqlTextSanitization", out var st))
                    sqlTextPolicy = st.GetString() ?? sqlTextPolicy;
            }
```

and the return:

```csharp
        return new SqlFerretConfig(durationUnit, cpuUnit, redaction, conn, plans, sqlTextPolicy);
```

- [ ] **Step 4: Run the config tests to verify they pass**

Run: `dotnet test --filter SqlFerretConfigSqlTextTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Wire the CLI**

In `src/SqlFerret.Cli/Program.cs`, immediately after the `--redaction` validation block that ends
at line 65, insert:

```csharp
            var sanitizeStr = Arg("--sanitize-sql-text", project.Config.SqlTextPolicy);
            if (!Enum.TryParse<SqlTextSanitization>(sanitizeStr, ignoreCase: true, out var sqlText))
            {
                Console.Error.WriteLine($"import: invalid --sanitize-sql-text value '{sanitizeStr}'. Valid: raw, literals");
                return 1;
            }
```

and pass it into the options at lines 67-68:

```csharp
            var options = new IngestionOptions(redaction, Array.Empty<FilterRule>(),
                PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()),
                SqlText: sqlText);
```

Add `using SqlFerret.Core.Normalization;` to the file's usings if it is not already present.

In the usage string at line 41, change the `import` fragment from:

```
import <path> --project <dir>
```

to:

```
import <path> --project <dir> [--redaction off|hash|masked|full] [--sanitize-sql-text raw|literals]
```

- [ ] **Step 6: Surface the new counter in the CLI summary**

`Program.cs:96-101` prints every other counter, `planWriteFailures` included. A counter nobody
sees cannot answer "how much of this project went through a path I should not trust". Append one
fragment to the existing `Console.WriteLine`:

```csharp
                $"planWriteFailures={result.PlanWriteFailures} " +
                $"sqlTextSanitizeFailures={result.SqlTextSanitizeFailures}");
```

Deliberately **do not** extend `ImportProgress` / `ImportProgressText`: the live gauge is a
progress readout, not a provenance record, and the run row plus the summary already carry the
number.

`CliSmokeTests` is sample-gated and will not catch a changed summary line in a default
`dotnet test` run. If it asserts on that string, update it in this task.

- [ ] **Step 7: Wire the TUI**

In `src/SqlFerret.Tui/Presenters/ImportPresenter.cs`, replace lines 16-17:

```csharp
            // The configured level applies in the TUI too: leaving it on Raw here would make the
            // host silently ignore a project's privacy setting.
            var sqlText = Enum.TryParse<SqlTextSanitization>(
                project.Config.SqlTextPolicy, ignoreCase: true, out var parsed)
                ? parsed
                : SqlTextSanitization.Raw;

            var options = new IngestionOptions(redaction, [],
                PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()),
                SqlText: sqlText);
```

Add `using SqlFerret.Core.Normalization;` to the file's usings.

- [ ] **Step 8: Verify both hosts build and behave**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Run, from the repository root — note the throwaway path, and note that it **will be created**:

```bash
dotnet run --project src/SqlFerret.Cli -- import nope.xel --project ../sfplan-check --sanitize-sql-text bogus
```

Expected: `import: invalid --sanitize-sql-text value 'bogus'. Valid: raw, literals`, exit code 1.
The flag error must fire before the missing input file is reported.

`Program.cs:56-58` calls `OpenProject()` *before* the flag validation, and `AuditProject` creates
the directory on first use, so this check leaves a project tree behind. It is outside the repo, so
`git status` stays clean; delete it afterwards:

```bash
rm -rf ../sfplan-check
```

Run: `dotnet test --filter "ImportPresenterTests|CliSmokeTests"`
Expected: unchanged — these are sample-gated and will skip without `sample/`. A skip here is the
documented baseline, not a failure.

- [ ] **Step 9: Format and commit**

```bash
dotnet format --include src/SqlFerret.Core/Config/SqlFerretConfig.cs src/SqlFerret.Cli/Program.cs src/SqlFerret.Tui/Presenters/ImportPresenter.cs tests/SqlFerret.Core.Tests/SqlFerretConfigSqlTextTests.cs
git add src/SqlFerret.Core/Config/SqlFerretConfig.cs src/SqlFerret.Cli/Program.cs src/SqlFerret.Tui/Presenters/ImportPresenter.cs tests/SqlFerret.Core.Tests/SqlFerretConfigSqlTextTests.cs
git commit -m "feat(hosts): add --sanitize-sql-text and ingest.sqlTextSanitization"
```

---

### Task 5: Refuse estimated-plan capture on a sanitized run

**Files:**
- Modify: `src/SqlFerret.Core/Model/ExecutionEvent.cs`
- Modify: `src/SqlFerret.Core/Analysis/WorkloadQueries.cs:151-220` (`LoadExecution`)
- Modify: `src/SqlFerret.Core/Server/EstimatedPlanService.cs:42` (`CaptureAsync`)
- Test: `tests/SqlFerret.Core.Tests/SqlTextPolicyGateTests.cs` (create)

**Interfaces:**
- Consumes: `ingestion_runs.sql_text_policy` (Task 2), the ingestion path (Task 3).
- Produces: `ExecutionEvent.SqlTextPolicy` — `string?`, null meaning unknown or pre-versioning.

`EstimatedPlanService` has no database handle and `ExecutionEvent` has no run identity, so the
policy travels on the event. This adds **no** namespace edge: `Model` and `Analysis` already sit
below `Server`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlFerret.Core.Tests/SqlTextPolicyGateTests.cs`:

```csharp
// tests/SqlFerret.Core.Tests/SqlTextPolicyGateTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;
using Xunit;

public class SqlTextPolicyGateTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static (IXeEventData, string, long) Batch(string sql) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026, 1, 1),
            new Dictionary<string, object?> { ["batch_text"] = sql, ["duration"] = 1000L },
            new Dictionary<string, object?> { ["database_name"] = "Sales", ["session_id"] = 1 }),
         "s_0.xel", 0L);

    [Fact]
    public void LoadExecution_projects_the_run_policy()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, [],
                    SqlText: SqlTextSanitization.Literals))
                .Ingest("logs/", [Batch("SELECT 1")]);

            var ev = new WorkloadQueries(db.Connection).LoadExecution(1);
            Assert.Equal("literals", ev.SqlTextPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoadExecution_reports_raw_for_an_unsanitized_run()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            new IngestionService(db, new IngestionOptions(RedactionMode.Masked, []))
                .Ingest("logs/", [Batch("SELECT 1")]);

            Assert.Equal("raw", new WorkloadQueries(db.Connection).LoadExecution(1).SqlTextPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The refusal must happen before any connection attempt: the connection string below is
    // unusable, so a SqlException instead of our InvalidOperationException means the gate is
    // in the wrong place.
    [Fact]
    public async Task CaptureAsync_refuses_a_sanitized_execution_before_connecting()
    {
        var ev = new ExecutionEvent
        {
            EventName = "sql_batch_completed",
            SqlTextRaw = "select * from dbo.Customers where Email = ?",
            XeFileName = "s_0.xel",
            SqlTextPolicy = "literals",
        };
        var svc = new EstimatedPlanService("Server=(invalid);Connect Timeout=1", Path.GetTempPath());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureAsync(ev, "plan1"));

        Assert.Contains("--sanitize-sql-text literals", ex.Message);
        Assert.Contains("not executable", ex.Message);
    }

    [Fact]
    public async Task CaptureAsync_does_not_refuse_when_the_policy_is_raw_or_unknown()
    {
        var ev = new ExecutionEvent
        {
            EventName = "sql_batch_completed",
            SqlTextRaw = "SELECT 1",
            XeFileName = "s_0.xel",
            SqlTextPolicy = null,       // legacy project
        };
        var svc = new EstimatedPlanService("Server=(invalid);Connect Timeout=1", Path.GetTempPath());

        // Reaches the connection attempt and fails there. The assertion is that the failure is
        // NOT our refusal — a legacy (null policy) execution must still be attempted.
        // This is the one network-dependent assertion in an otherwise offline suite. If it proves
        // slow or flaky in practice, assert on the exception TYPE instead of driving a real
        // connection — the point is only that InvalidOperationException with our message is absent.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => svc.CaptureAsync(ev, "plan1"));
        Assert.DoesNotContain("not executable", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter SqlTextPolicyGateTests`
Expected: build failure — `ExecutionEvent` has no `SqlTextPolicy`.

- [ ] **Step 3: Add the field to `ExecutionEvent`**

In `src/SqlFerret.Core/Model/ExecutionEvent.cs`, after `SqlTextRaw`:

```csharp
    public required string SqlTextRaw { get; init; }

    /// <summary>
    /// The ingest-time sanitization policy of the run this execution came from
    /// (<c>ingestion_runs.sql_text_policy</c>). Null for an event that was never loaded from a
    /// project, or for a project created before the column existed. Anything other than
    /// <c>"raw"</c> means <see cref="SqlTextRaw"/> is not executable T-SQL.
    /// </summary>
    public string? SqlTextPolicy { get; init; }
```

- [ ] **Step 4: Project the column in `LoadExecution`**

In `src/SqlFerret.Core/Analysis/WorkloadQueries.cs`, change the query at lines 154-159 to join the
run and select the policy as column index 13:

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

`LEFT JOIN`, not `JOIN`: a missing run row must not make the execution unloadable.

Declare the local beside the others at line 163 and read it in the reader block after
`fileOffset = r.GetInt64(12);`:

```csharp
        string? sqlTextPolicy;
```

```csharp
            fileOffset = r.GetInt64(12);
            sqlTextPolicy = r.IsDBNull(13) ? null : r.GetString(13);
```

and add it to the returned object initializer:

```csharp
            SqlTextRaw = sqlRaw,
            SqlTextPolicy = sqlTextPolicy,
```

The reader variable is already named `r`; the join alias is also `r` but lives only inside the SQL
string, so there is no C# conflict.

- [ ] **Step 5: Gate `CaptureAsync`**

In `src/SqlFerret.Core/Server/EstimatedPlanService.cs`, make it the first statement of
`CaptureAsync`, before `ReplayBuilder.Build`:

```csharp
    public async Task<string> CaptureAsync(ExecutionEvent ev, string planId, CancellationToken ct = default)
    {
        // A sanitized run stores literal-free text: "… WHERE Email = ?" is not valid T-SQL.
        // Refuse here rather than sending it to the server and surfacing a syntax error.
        if (ev.SqlTextPolicy is not null
            && !ev.SqlTextPolicy.Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"estimated plan: this execution was imported with --sanitize-sql-text {ev.SqlTextPolicy}; " +
                "the stored statement text is not executable. Re-import with --sanitize-sql-text raw " +
                "to capture estimated plans.");
        }

        ReplayScript script = ReplayBuilder.Build(ev);
        ...
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter SqlTextPolicyGateTests`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run the suites that touch `LoadExecution`**

Run: `dotnet test --filter "LoadExecutionTests|WorkloadQueriesTests|EstimatedPlanServiceTests"`
Expected: PASS. `EstimatedPlanServiceTests` skips without `SQLFERRET_TEST_CONN` — that is the
documented baseline.

- [ ] **Step 8: Format and commit**

```bash
dotnet format --include src/SqlFerret.Core/Model/ExecutionEvent.cs src/SqlFerret.Core/Analysis/WorkloadQueries.cs src/SqlFerret.Core/Server/EstimatedPlanService.cs tests/SqlFerret.Core.Tests/SqlTextPolicyGateTests.cs
git add src/SqlFerret.Core/Model/ExecutionEvent.cs src/SqlFerret.Core/Analysis/WorkloadQueries.cs src/SqlFerret.Core/Server/EstimatedPlanService.cs tests/SqlFerret.Core.Tests/SqlTextPolicyGateTests.cs
git commit -m "feat(server): refuse estimated plan capture on a sanitized run"
```

---

### Task 6: Documentation

No code changes. This task is where the feature stops creating false confidence, so it is not
optional and not a footnote to Task 5.

**Files:**
- Modify: `docs/privacy.md`
- Modify: `docs/cli-reference.md`
- Modify: `docs/configuration.md`
- Modify: `docs/data-model.md`
- Modify: `docs/execution-plans.md`
- Modify: `src/SqlFerret.Core/Project/AuditProject.cs:151`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Rewrite the affected part of `docs/privacy.md`**

Change the first row of the "What lands on disk" table from:

```
| `executions.sql_text_raw` | The statement as captured, including any inlined literals | **Nothing. Always stored.** |
```

to:

```
| `executions.sql_text_raw` | The statement as captured, including any inlined literals | `--sanitize-sql-text` (default `raw`: always stored) |
| `normalized_queries.normalized_sql` | The statement shape; literals `?`. Real literals on tokenize failure unless sanitized | Same flag |
```

Then rewrite "The one thing to internalize", which currently ends "no redaction policy will
protect you". Add, as a new subsection after it, four points stated plainly:

1. **The levels.** `raw` (default, unchanged) and `literals` (statement text stored with literals
   collapsed to `?`). Set per import with `--sanitize-sql-text`, or in
   `sqlferret.config.json` under `ingest.sqlTextSanitization`. Independent of `--redaction`.
2. **`literals` removes values, not schema.** Table, column and procedure names remain in both
   `sql_text_raw` and `normalized_sql`.
3. **A project is only safe to share if every run in it was sanitized.**
   `normalized_queries` is project-wide and its `ON CONFLICT` clause updates only `last_seen_at` —
   a shape first seen during a `raw` import keeps that text forever, and a later sanitized import
   into the same project will not clean it. Check with:
   `SELECT run_id, sql_text_policy FROM ingestion_runs;`
4. **A sanitized project can still carry real statement text elsewhere — three places.**
   - `qds_query_text.query_sql_text` (`Storage/DuckDbProject.QueryStore.cs:30`). A project that
     ran `query-store-import` stores Query Store statement text verbatim
     (`Server/QueryStoreImportService.cs:106-114`), in the same `sqlferret.duckdb`, untruncated,
     regardless of `--sanitize-sql-text`. **This is the largest of the three** and must be listed
     first.
   - `plans/**/*.digest.json` — a truncated `StatementText` this option does not touch.
   - `.sqlplan` files — statement text until `obfuscate-plan` rewrites them.
5. **`--redaction off` and `--sanitize-sql-text literals` combine into a misleading project.**
   `PrepareProc` (`Ingestion/IngestionService.cs:120-123`) returns `InputBufRaw` unchanged under
   `off`, and the raw blocking XML is retained under the same condition (`:45`). Neither is
   affected by the new option — they are deliberately orthogonal knobs. So one command can produce
   a project whose `executions.sql_text_raw` is sanitized while `blocking_processes.inputbuf` and
   `blocking_reports.raw_xml` still hold real statement text and literals. Say so beside the
   existing `off` sharp-edge section.

- [ ] **Step 1b: Two smaller surfaces in the same pass**

`docs/privacy.md:201-206` carries a "review `sql_text_raw` before sharing" SQL recipe that reads
oddly once sanitization exists — add a clause noting the recipe applies to `raw` runs, and that
`ingestion_runs.sql_text_policy` tells you which runs those are.

`docs/normalization.md` documents the `QueryNormalizer.Version` rule. Add one sentence: the
sanitizer's own version is coupled to it, because at `literals` the stored statement text *is* the
normalizer's output.

- [ ] **Step 2: Update `docs/cli-reference.md`**

Under `import`, document the flag beside `--redaction`:

```
--sanitize-sql-text <raw|literals>   Statement text written to sql_text_raw and
                                     normalized_sql. Default: raw (or the project's
                                     ingest.sqlTextSanitization). See docs/privacy.md.
                                     An invalid value exits 1.
```

Note that a run imported at `literals` cannot produce estimated plans.

- [ ] **Step 3: Update `docs/configuration.md`**

Document `ingest.sqlTextSanitization`, default `"raw"`, overridden by `--sanitize-sql-text`, and
honored by both the CLI and the TUI:

```json
{ "ingest": { "redactionPolicy": "masked", "sqlTextSanitization": "literals" } }
```

- [ ] **Step 4: Update `docs/data-model.md`**

Add the three `ingestion_runs` columns: `sql_text_policy` (TEXT, `raw` / `literals`; **NULL means
a run imported before this column existed — read it as `raw`**), `sql_text_sanitizer_version`
(INTEGER, NULL for pre-versioning runs), `sql_text_sanitize_failures` (BIGINT, events whose text
could not be tokenized and were replaced by the placeholder).

- [ ] **Step 5: Update `docs/execution-plans.md`**

Document the refusal, alongside the existing `export-events` refusal precedent: capturing an
estimated plan for an execution from a `literals` run fails with an explicit message, because the
stored text is not valid T-SQL. Re-import with `--sanitize-sql-text raw` if estimated plans are
needed.

Word it as the behavior of the **estimated-plan capture path**, not as something a user hits from
a command: `EstimatedPlanService.CaptureAsync` has no production caller today — the only
invocation in the repository is `EstimatedPlanServiceTests.cs:46`, and neither host reaches it.
Task 5 is the contract for whoever wires it up, not a user-visible change yet.

- [ ] **Step 6: Update the generated project README**

In `src/SqlFerret.Core/Project/AuditProject.cs`, the "Notes" list at line 151 currently reads:

```
        - Parameter values may be redacted per the project's redaction policy before being written to disk.
```

Add immediately after it:

```
        - Statement text may be sanitized per the project's `ingest.sqlTextSanitization` setting: at `literals`, inlined literal values are replaced by `?` before being written to disk. Identifiers are not removed. See `docs/privacy.md`.
```

This README is written once at project creation, so it describes the configured posture, not a
per-run fact.

- [ ] **Step 7: Update `CLAUDE.md`**

In section B, "Invariants", beside the `QueryNormalizer.Version` bullet, add:

```
- **`SqlTextSanitizer.Version = 1`**, persisted on `ingestion_runs.sql_text_sanitizer_version`.
  Changing what the sanitizer stores requires bumping it. It is coupled to
  `QueryNormalizer.Version`: at the `literals` level the stored text *is* the normalizer's
  output, so bumping one should prompt a look at the other.
```

Extend the redaction bullet to say that redaction covers parameter values while
`--sanitize-sql-text` covers statement text, and that the two are independent.

**Do not modify the namespace dependency line.** This change adds no edge — `SqlTextSanitizer`
lives in `Normalization`, which `Ingestion` already depends on.

- [ ] **Step 8: Verify and commit**

Run: `dotnet build`
Expected: 0 warnings — `AuditProject.cs` is C#, so its README string must still compile.

Run: `dotnet test --filter AuditProject`
Expected: PASS. If a test asserts the README's exact content, update that assertion in the same
commit.

```bash
dotnet format --include src/SqlFerret.Core/Project/AuditProject.cs
git add docs/privacy.md docs/cli-reference.md docs/configuration.md docs/data-model.md docs/execution-plans.md src/SqlFerret.Core/Project/AuditProject.cs CLAUDE.md
git commit -m "docs: document sql text sanitization, its limits and the estimated-plan refusal"
```

---

## Final verification

- [ ] Run: `dotnet build` — expected 0 warnings, 0 errors.
- [ ] Run: `dotnet test` — expected the 377-pass baseline plus the 22 tests added here, with the
      same 10 environment-gated skips.
- [ ] Run: `git status` — expected clean apart from the intended files. `CLAUDE.md` warns that an
      end-to-end run can leave artifacts; nothing under `sample/`, `plans/`, `exports/`, `.env` or
      any `*.duckdb` may be staged.
- [ ] Optional end-to-end check, only with a local capture present, writing outside the repo:

```bash
dotnet run --project src/SqlFerret.Cli -- import sample/trace_0.xel --project /tmp/wl-lit --sanitize-sql-text literals
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/wl-lit --limit 5
```

Expected: the `top-slow` output shows `?` in place of literal values, and
`SELECT sql_text_policy, sql_text_sanitize_failures FROM ingestion_runs;` in the DuckDB CLI shows
`literals` and a plausible failure count.
