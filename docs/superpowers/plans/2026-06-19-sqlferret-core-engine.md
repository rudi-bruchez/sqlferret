# SQLFerret Core Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the headless SQLFerret Core engine — ingest a `.xel` file or a `logs/` folder, normalize each query, store everything in an embedded DuckDB file, analyze the workload, and optionally fetch estimated execution plans — exercised by a thin `SqlFerret.Cli` host and a full xUnit suite.

**Architecture:** One `SqlFerret.Core` class library of plain, focused classes (no DI/onion/repository ceremony). A streaming ingestion pipeline (`XelReader → EventMapper → QueryNormalizer + ParameterExtractor + RedactionPolicy → ingest filters → DuckDbProject` batched inserts). Analysis is plain SQL in `WorkloadQueries`. A minimal `SqlFerret.Cli` makes it runnable headless; the future TUI (Plan 2) is a second thin host over the same Core.

**Tech Stack:** C# / .NET 10, XELite (`.xel` reader), DuckDB.NET (embedded analytics), ScriptDom (T-SQL token stream + minimal AST), Microsoft.Data.SqlClient (estimated plans), xUnit.

## Global Constraints

- **Target framework:** `net10.0` for every project.
- **KISS (spec §2):** no repository/unit-of-work/onion layering; no `IXxxService` interface unless a real second implementation exists; plain `record`/POCO DTOs; no AutoMapper/MediatR/CQRS. Aggregation lives in DuckDB SQL, not hand-built C#. The only abstraction introduced for testability is `IXeEventData` (Task 11), because the real XELite event and test fakes are two genuine implementations.
- **Durations/CPU stored in microseconds** everywhere in Core; formatting happens in hosts only.
- **Secrets in `.env`** (DB credentials now, LLM keys later), referenced from config via `${ENV_VAR}`; never written into committed files. `.env` is gitignored; `.env.example` is committed.
- **Normalizer version constant:** `QueryNormalizer.Version = 1`. Stored on both `ingestion_runs` and `normalized_queries`.
- **Redaction applied before any parameter value is written to disk.**
- **Nothing silently dropped:** unmapped events, tokenize failures, and per-rule ingest-cleaning drops are all counted in `ingestion_runs` and queryable.
- **Solution layout:** `SqlFerret.Core` (library), `SqlFerret.Cli` (console host), `SqlFerret.Core.Tests` (xUnit).

---

## File Structure

```
sqlferret.sln
src/
  SqlFerret.Core/
    SqlFerret.Core.csproj
    Model/
      EventClass.cs            enum RpcCall|SqlBatch|Statement|Unknown
      ParameterSourceKind.cs   enum RpcParameter|Literal|OutputParameter
      ExecutionEvent.cs        canonical mapped event (record)
      RawParameter.cs          one observed parameter (record)
      NormalizedQuery.cs       normalized sql + hash + kind + table (record)
      ReplayKind.cs            enum RawBatch|ExecProc|SpExecuteSql
      ReplayScript.cs          reconstructed T-SQL + confidence (record)
    Normalization/
      TokenNormalizer.cs       ScriptDom token stream → normalized SQL
      AstClassifier.cs         minimal AST → statement kind + primary table
      Fingerprint.cs           Sha256 hex of normalized SQL
      QueryNormalizer.cs       facade: raw SQL → NormalizedQuery (+ Version)
    Parameters/
      RedactionPolicy.cs       Off|Hash|Masked|Full + per-name rules
      ParameterExtractor.cs    rpc/sp_executesql text → RawParameter[]
    Filtering/
      FilterRule.cs            record (id, field, op, values, stage, action, enabled)
      FilterCompiler.cs        rules → SQL WHERE (view) / predicate (ingest)
    Ingestion/
      XelSource.cs             path → file list (file or *.xel in dir)
      IXeEventData.cs          minimal event abstraction (testable seam)
      XelReader.cs             XELite streamer → IXeEventData callback
      EventMapper.cs           IXeEventData → ExecutionEvent
      IngestionOptions.cs      redaction mode, ingest filter rules, batch size
      IngestionService.cs      orchestrates the streaming import
      IngestionResult.cs       counters returned to caller
    Storage/
      DuckDbProject.cs         schema, ingestion_runs, batched Appender inserts
      PreparedRow.cs           execution row + params ready to insert (record)
    Analysis/
      WorkloadQueries.cs       Top-Slow/Freq/occurrences/session/param/dims/quality
      Results.cs               result record types
    Replay/
      ReplayBuilder.cs         ExecutionEvent → ReplayScript (build-for-SSMS)
    Server/
      EstimatedPlanService.cs  SHOWPLAN_XML → save <id>.sqlplan
    Config/
      DotEnv.cs                stdlib-style KEY=VALUE loader (no dependency)
      SqlFerretConfig.cs       config model + ${ENV} interpolation
      DisplayFormat.cs         microseconds → unit string
      UiState.cs               filters + view layouts (sqlferret.ui.json)
  SqlFerret.Cli/
    SqlFerret.Cli.csproj
    Program.cs                 `import` and `top-slow` commands
tests/
  SqlFerret.Core.Tests/
    SqlFerret.Core.Tests.csproj
    Fixtures/
      generate-fixtures.md     how the committed .xel fixtures were produced
      sample_basic.xel         small committed fixture (generated once)
    *Tests.cs
.gitignore                     includes .env
.env.example                   committed, empty keys
```

---

## Task 1: Solution scaffolding

**Files:**
- Create: `sqlferret.sln`, `src/SqlFerret.Core/SqlFerret.Core.csproj`, `src/SqlFerret.Cli/SqlFerret.Cli.csproj`, `tests/SqlFerret.Core.Tests/SqlFerret.Core.Tests.csproj`, `.gitignore`, `.env.example`

**Interfaces:**
- Produces: a building solution with three `net10.0` projects and a green placeholder test.

- [ ] **Step 1: Create solution and projects**

```bash
cd /home/rudi/Sources/Repos/sqlferret
dotnet new sln -n sqlferret
dotnet new classlib -n SqlFerret.Core -o src/SqlFerret.Core -f net10.0
dotnet new console  -n SqlFerret.Cli  -o src/SqlFerret.Cli  -f net10.0
dotnet new xunit    -n SqlFerret.Core.Tests -o tests/SqlFerret.Core.Tests -f net10.0
rm src/SqlFerret.Core/Class1.cs
dotnet sln add src/SqlFerret.Core src/SqlFerret.Cli tests/SqlFerret.Core.Tests
dotnet add src/SqlFerret.Cli reference src/SqlFerret.Core
dotnet add tests/SqlFerret.Core.Tests reference src/SqlFerret.Core
```

- [ ] **Step 2: Add NuGet packages (latest stable, then commit the pinned versions)**

```bash
dotnet add src/SqlFerret.Core package Microsoft.SqlServer.XEvent.XELite
dotnet add src/SqlFerret.Core package DuckDB.NET.Data.Full
dotnet add src/SqlFerret.Core package Microsoft.SqlServer.TransactSql.ScriptDom
dotnet add src/SqlFerret.Core package Microsoft.Data.SqlClient
```

- [ ] **Step 3: Enable nullable + implicit usings in Core**

Ensure `src/SqlFerret.Core/SqlFerret.Core.csproj` `<PropertyGroup>` contains:

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<LangVersion>latest</LangVersion>
```

- [ ] **Step 4: Write `.gitignore` additions and `.env.example`**

Append to `.gitignore`:

```
bin/
obj/
*.duckdb
plans/
.env
```

Create `.env.example`:

```
# Copy to .env and fill in. .env is gitignored.
# Connection auth fragment referenced by sqlferret.config.json ${SQLFERRET_AUTH}
SQLFERRET_AUTH=User ID=sa;Password=CHANGE_ME;TrustServerCertificate=True
```

- [ ] **Step 5: Build and run the placeholder test**

Run: `dotnet test`
Expected: PASS (1 default xUnit test), solution builds.

- [ ] **Step 6: Commit**

```bash
rtk git add -A && rtk git commit -m "chore: scaffold SqlFerret solution (Core, Cli, Tests)"
```

---

## Task 2: Domain model records

**Files:**
- Create: `src/SqlFerret.Core/Model/EventClass.cs`, `ParameterSourceKind.cs`, `ExecutionEvent.cs`, `RawParameter.cs`, `NormalizedQuery.cs`, `ReplayKind.cs`, `ReplayScript.cs`
- Test: `tests/SqlFerret.Core.Tests/ModelTests.cs`

**Interfaces:**
- Produces:
  - `enum EventClass { RpcCall, SqlBatch, Statement, Unknown }`
  - `enum ParameterSourceKind { RpcParameter, Literal, OutputParameter }`
  - `enum ReplayKind { RawBatch, ExecProc, SpExecuteSql }`
  - `record RawParameter(int Ordinal, string? Name, ParameterSourceKind SourceKind, string? SqlTypeGuess, string ValueText, double ParseConfidence)`
  - `record ExecutionEvent` with init-only properties: `DateTime CapturedAt; string EventName; EventClass EventClass; string? ObjectName; bool IsSystem; string? DatabaseName; string? LoginName; string? ClientHostname; string? ClientAppName; int? SessionId; long? DurationUs; long? CpuTimeUs; long? LogicalReads; long? PhysicalReads; long? Writes; long? RowCount; string? QueryHash; string? QueryPlanHash; string SqlTextRaw; IReadOnlyList<RawParameter> Parameters; string XeFileName; long FileOffset`
  - `record NormalizedQuery(string NormalizedSql, string NormalizedHash, string StatementKind, string? PrimaryTable, bool TokenizeFailed)`
  - `record ReplayScript(string Sql, ReplayKind Kind, double Confidence)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ModelTests.cs
using SqlFerret.Core.Model;
using Xunit;

public class ModelTests
{
    [Fact]
    public void ExecutionEvent_defaults_parameters_to_empty()
    {
        var e = new ExecutionEvent { SqlTextRaw = "select 1", EventName = "sql_batch_completed", XeFileName = "a.xel" };
        Assert.Empty(e.Parameters);
        Assert.Equal(EventClass.Unknown, e.EventClass);
    }

    [Fact]
    public void RawParameter_holds_values()
    {
        var p = new RawParameter(0, "@id", ParameterSourceKind.RpcParameter, "int", "42", 0.9);
        Assert.Equal("@id", p.Name);
        Assert.Equal("42", p.ValueText);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ModelTests`
Expected: FAIL (compile error — types not defined).

- [ ] **Step 3: Create the model files**

```csharp
// src/SqlFerret.Core/Model/EventClass.cs
namespace SqlFerret.Core.Model;
public enum EventClass { RpcCall, SqlBatch, Statement, Unknown }
```

```csharp
// src/SqlFerret.Core/Model/ParameterSourceKind.cs
namespace SqlFerret.Core.Model;
public enum ParameterSourceKind { RpcParameter, Literal, OutputParameter }
```

```csharp
// src/SqlFerret.Core/Model/ReplayKind.cs
namespace SqlFerret.Core.Model;
public enum ReplayKind { RawBatch, ExecProc, SpExecuteSql }
```

```csharp
// src/SqlFerret.Core/Model/RawParameter.cs
namespace SqlFerret.Core.Model;
public record RawParameter(
    int Ordinal, string? Name, ParameterSourceKind SourceKind,
    string? SqlTypeGuess, string ValueText, double ParseConfidence);
```

```csharp
// src/SqlFerret.Core/Model/ExecutionEvent.cs
namespace SqlFerret.Core.Model;

public record ExecutionEvent
{
    public DateTime CapturedAt { get; init; }
    public required string EventName { get; init; }
    public EventClass EventClass { get; init; } = EventClass.Unknown;
    public string? ObjectName { get; init; }
    public bool IsSystem { get; init; }
    public string? DatabaseName { get; init; }
    public string? LoginName { get; init; }
    public string? ClientHostname { get; init; }
    public string? ClientAppName { get; init; }
    public int? SessionId { get; init; }
    public long? DurationUs { get; init; }
    public long? CpuTimeUs { get; init; }
    public long? LogicalReads { get; init; }
    public long? PhysicalReads { get; init; }
    public long? Writes { get; init; }
    public long? RowCount { get; init; }
    public string? QueryHash { get; init; }
    public string? QueryPlanHash { get; init; }
    public required string SqlTextRaw { get; init; }
    public IReadOnlyList<RawParameter> Parameters { get; init; } = Array.Empty<RawParameter>();
    public required string XeFileName { get; init; }
    public long FileOffset { get; init; }
}
```

```csharp
// src/SqlFerret.Core/Model/NormalizedQuery.cs
namespace SqlFerret.Core.Model;
public record NormalizedQuery(
    string NormalizedSql, string NormalizedHash,
    string StatementKind, string? PrimaryTable, bool TokenizeFailed);
```

```csharp
// src/SqlFerret.Core/Model/ReplayScript.cs
namespace SqlFerret.Core.Model;
public record ReplayScript(string Sql, ReplayKind Kind, double Confidence);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): add domain model records"
```

---

## Task 3: TokenNormalizer (ScriptDom token stream)

**Files:**
- Create: `src/SqlFerret.Core/Normalization/TokenNormalizer.cs`
- Test: `tests/SqlFerret.Core.Tests/TokenNormalizerTests.cs`

**Interfaces:**
- Produces: `static class TokenNormalizer` with `(string normalizedSql, bool tokenizeFailed) Normalize(string rawSql)`. Replaces literal tokens with `?`, drops comments/whitespace runs (single space), lowercases keywords, collapses `IN (?, ?, …)` → `IN (?)`. On any parse/tokenize exception returns `(collapsed-whitespace-lowercased rawSql, tokenizeFailed: true)`.

- [ ] **Step 1: Write the failing tests (golden pairs)**

```csharp
// tests/SqlFerret.Core.Tests/TokenNormalizerTests.cs
using SqlFerret.Core.Normalization;
using Xunit;

public class TokenNormalizerTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 42",      "select * from dbo.Users where Id = ?")]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 99",      "select * from dbo.Users where Id = ?")]
    [InlineData("EXEC dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR'",
                "exec dbo.GetOrder @OrderId = ?, @Culture = ?")]
    [InlineData("SELECT 1 -- a comment\nFROM t",              "select ? from t")]
    [InlineData("SELECT * FROM t WHERE c IN (1, 2, 3)",       "select * from t where c in (?)")]
    [InlineData("SELECT * FROM [my table] WHERE x = 0x1A",    "select * from [my table] where x = ?")]
    [InlineData("SELECT * FROM t WHERE s = 'it''s'",          "select * from t where s = ?")]
    public void Normalizes_literals_and_shape(string raw, string expected)
    {
        var (normalized, failed) = TokenNormalizer.Normalize(raw);
        Assert.False(failed);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Unparseable_input_falls_back_and_flags()
    {
        var (normalized, failed) = TokenNormalizer.Normalize("@@@ not sql ((");
        Assert.True(failed);
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter TokenNormalizerTests`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement TokenNormalizer**

```csharp
// src/SqlFerret.Core/Normalization/TokenNormalizer.cs
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

public static class TokenNormalizer
{
    private static readonly HashSet<TSqlTokenType> LiteralTokens = new()
    {
        TSqlTokenType.Integer, TSqlTokenType.Numeric, TSqlTokenType.Money,
        TSqlTokenType.Real, TSqlTokenType.HexLiteral,
        TSqlTokenType.AsciiStringLiteral, TSqlTokenType.UnicodeStringLiteral,
    };

    public static (string normalizedSql, bool tokenizeFailed) Normalize(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
            return (string.Empty, false);

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(rawSql);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(reader, out IList<ParseError> errors);
            if (errors.Count > 0)
                return (FallbackCollapse(rawSql), true);

            var sb = new StringBuilder();
            bool lastWasSpace = false;
            foreach (var t in tokens)
            {
                switch (t.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                    case TSqlTokenType.EndOfFile:
                        if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                        continue;
                }

                string text = LiteralTokens.Contains(t.TokenType)
                    ? "?"
                    : IsKeyword(t.TokenType) ? t.Text.ToLowerInvariant() : t.Text;

                sb.Append(text);
                lastWasSpace = false;
            }

            var collapsed = CollapseInList(sb.ToString().Trim());
            return (collapsed, false);
        }
        catch
        {
            return (FallbackCollapse(rawSql), true);
        }
    }

    private static bool IsKeyword(TSqlTokenType type) =>
        // ScriptDom keyword token types are >= the first reserved keyword; simplest robust check:
        type.ToString().StartsWith("Identifier") == false && IsWordToken(type);

    private static bool IsWordToken(TSqlTokenType type) =>
        type != TSqlTokenType.Identifier &&
        type != TSqlTokenType.QuotedIdentifier &&
        type != TSqlTokenType.Variable &&
        type != TSqlTokenType.Dot &&
        type != TSqlTokenType.Star &&
        type != TSqlTokenType.Comma &&
        type != TSqlTokenType.LeftParenthesis &&
        type != TSqlTokenType.RightParenthesis &&
        type != TSqlTokenType.EqualsSign &&
        char.IsLetter(type.ToString()[0]); // keyword token types are named words

    // Collapse "in (?, ?, ?)" → "in (?)"  (case-insensitive, whitespace-tolerant)
    private static string CollapseInList(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(
            sql, @"(?i)\bin\s*\(\s*\?(?:\s*,\s*\?)+\s*\)", "in (?)");

    private static string FallbackCollapse(string raw) =>
        System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim().ToLowerInvariant();
}
```

> **Implementer note:** ScriptDom token-type→keyword detection is fiddly. If the `IsKeyword` heuristic mis-cases an identifier in a golden test, prefer an explicit allow-list of the keyword `TSqlTokenType` values you actually encounter (e.g. `Select, From, Where, Exec, In, And, Or, Join, On, Order, Group, By`) rather than the heuristic. Keep the raw identifier casing intact (`dbo.Users` stays as written). Adjust the golden expectations only if SQL semantics truly require it.

- [ ] **Step 4: Run to verify pass (iterate on keyword casing until green)**

Run: `dotnet test --filter TokenNormalizerTests`
Expected: PASS for all theory rows.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): ScriptDom token-stream normalizer"
```

---

## Task 4: AstClassifier (statement kind + primary table)

**Files:**
- Create: `src/SqlFerret.Core/Normalization/AstClassifier.cs`
- Test: `tests/SqlFerret.Core.Tests/AstClassifierTests.cs`

**Interfaces:**
- Produces: `static class AstClassifier` with `(string statementKind, string? primaryTable) Classify(string rawSql)`. `statementKind` ∈ `SELECT|INSERT|UPDATE|DELETE|EXEC|OTHER`. `primaryTable` is the first named table/object, else `null`. Never throws (returns `("OTHER", null)` on parse failure).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/AstClassifierTests.cs
using SqlFerret.Core.Normalization;
using Xunit;

public class AstClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 1", "SELECT", "dbo.Users")]
    [InlineData("INSERT INTO Orders (Id) VALUES (1)",    "INSERT", "Orders")]
    [InlineData("UPDATE dbo.T SET x = 1 WHERE id = 2",   "UPDATE", "dbo.T")]
    [InlineData("DELETE FROM Logs WHERE d < '2020-01-01'", "DELETE", "Logs")]
    [InlineData("EXEC dbo.GetOrder @id = 1",             "EXEC",   "dbo.GetOrder")]
    public void Classifies_kind_and_table(string raw, string kind, string? table)
    {
        var (k, t) = AstClassifier.Classify(raw);
        Assert.Equal(kind, k);
        Assert.Equal(table, t);
    }

    [Fact]
    public void Garbage_is_OTHER()
    {
        var (k, t) = AstClassifier.Classify("@@@ ((");
        Assert.Equal("OTHER", k);
        Assert.Null(t);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter AstClassifierTests`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement AstClassifier**

```csharp
// src/SqlFerret.Core/Normalization/AstClassifier.cs
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

public static class AstClassifier
{
    public static (string statementKind, string? primaryTable) Classify(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql)) return ("OTHER", null);
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(rawSql);
            var fragment = parser.Parse(reader, out IList<ParseError> errors);
            if (errors.Count > 0 || fragment is null) return ("OTHER", null);

            var visitor = new ClassifyVisitor();
            fragment.Accept(visitor);
            return (visitor.Kind ?? "OTHER", visitor.PrimaryTable);
        }
        catch { return ("OTHER", null); }
    }

    private sealed class ClassifyVisitor : TSqlFragmentVisitor
    {
        public string? Kind { get; private set; }
        public string? PrimaryTable { get; private set; }

        public override void Visit(SelectStatement node)  => Set("SELECT", FirstTable(node));
        public override void Visit(InsertStatement node)   => Set("INSERT", NamedTarget(node.InsertSpecification?.Target));
        public override void Visit(UpdateStatement node)   => Set("UPDATE", NamedTarget(node.UpdateSpecification?.Target));
        public override void Visit(DeleteStatement node)   => Set("DELETE", NamedTarget(node.DeleteSpecification?.Target));
        public override void Visit(ExecuteStatement node)  => Set("EXEC", ProcName(node));

        private void Set(string kind, string? table)
        {
            Kind ??= kind;            // first statement wins
            PrimaryTable ??= table;
        }

        private static string? FirstTable(SelectStatement s)
        {
            if (s.QueryExpression is QuerySpecification qs &&
                qs.FromClause?.TableReferences.FirstOrDefault() is NamedTableReference n)
                return Name(n.SchemaObject);
            return null;
        }

        private static string? NamedTarget(TableReference? tr) =>
            tr is NamedTableReference n ? Name(n.SchemaObject) : null;

        private static string? ProcName(ExecuteStatement e) =>
            (e.ExecuteSpecification?.ExecutableEntity as ExecutableProcedureReference)
                ?.ProcedureReference?.ProcedureReference?.Name is { } id ? Name(id) : null;

        private static string Name(SchemaObjectName o) =>
            string.Join(".", o.Identifiers.Select(i => i.Value));
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter AstClassifierTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): minimal AST classifier (kind + primary table)"
```

---

## Task 5: Fingerprint + QueryNormalizer facade

**Files:**
- Create: `src/SqlFerret.Core/Normalization/Fingerprint.cs`, `src/SqlFerret.Core/Normalization/QueryNormalizer.cs`
- Test: `tests/SqlFerret.Core.Tests/QueryNormalizerTests.cs`

**Interfaces:**
- Produces:
  - `static class Fingerprint` → `string Hash(string s)` (lowercase hex SHA-256).
  - `static class QueryNormalizer` → `const int Version = 1;` and `NormalizedQuery Normalize(string rawSql)` combining TokenNormalizer + AstClassifier + Fingerprint.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/QueryNormalizerTests.cs
using SqlFerret.Core.Normalization;
using Xunit;

public class QueryNormalizerTests
{
    [Fact]
    public void Same_shape_different_literals_share_hash()
    {
        var a = QueryNormalizer.Normalize("SELECT * FROM dbo.Users WHERE Id = 42");
        var b = QueryNormalizer.Normalize("SELECT * FROM dbo.Users WHERE Id = 99");
        Assert.Equal(a.NormalizedHash, b.NormalizedHash);
        Assert.Equal("SELECT", a.StatementKind);
        Assert.Equal("dbo.Users", a.PrimaryTable);
        Assert.False(a.TokenizeFailed);
    }

    [Fact]
    public void Different_shape_differs()
    {
        var a = QueryNormalizer.Normalize("SELECT a FROM t");
        var b = QueryNormalizer.Normalize("SELECT b FROM t");
        Assert.NotEqual(a.NormalizedHash, b.NormalizedHash);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter QueryNormalizerTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Normalization/Fingerprint.cs
using System.Security.Cryptography;
using System.Text;

namespace SqlFerret.Core.Normalization;

public static class Fingerprint
{
    public static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes);
    }
}
```

```csharp
// src/SqlFerret.Core/Normalization/QueryNormalizer.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Normalization;

public static class QueryNormalizer
{
    public const int Version = 1;

    public static NormalizedQuery Normalize(string rawSql)
    {
        var (normalized, failed) = TokenNormalizer.Normalize(rawSql);
        var (kind, table) = AstClassifier.Classify(rawSql);
        var hash = Fingerprint.Hash(normalized);
        return new NormalizedQuery(normalized, hash, kind, table, failed);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter QueryNormalizerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): fingerprint + QueryNormalizer facade"
```

---

## Task 6: RedactionPolicy

**Files:**
- Create: `src/SqlFerret.Core/Parameters/RedactionPolicy.cs`
- Test: `tests/SqlFerret.Core.Tests/RedactionPolicyTests.cs`

**Interfaces:**
- Produces:
  - `enum RedactionMode { Off, Hash, Masked, Full }`
  - `class RedactionPolicy(RedactionMode mode, IReadOnlyList<string>? sensitiveNameSubstrings = null)` with `(string storedValue, bool redacted) Apply(string? paramName, string valueText)`. `Off` → returns `("", true)` (caller skips writing the row). Per-name substrings (default `password, token, secret, email`) force `Hash` regardless of mode. `Masked` keeps length-shape (`***` of similar length). `Full` returns value unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/RedactionPolicyTests.cs
using SqlFerret.Core.Parameters;
using Xunit;

public class RedactionPolicyTests
{
    [Fact]
    public void Full_keeps_value()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Full).Apply("@id", "42");
        Assert.Equal("42", v);
        Assert.False(redacted);
    }

    [Fact]
    public void Hash_replaces_with_fingerprint()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Hash).Apply("@id", "42");
        Assert.NotEqual("42", v);
        Assert.True(redacted);
        Assert.Equal(64, v.Length); // sha256 hex
    }

    [Fact]
    public void Sensitive_name_forces_hash_even_in_full_mode()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Full).Apply("@Password", "hunter2");
        Assert.NotEqual("hunter2", v);
        Assert.True(redacted);
    }

    [Fact]
    public void Masked_hides_content_keeps_shape()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Masked).Apply("@name", "Alice");
        Assert.DoesNotContain("Alice", v);
        Assert.True(redacted);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter RedactionPolicyTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Parameters/RedactionPolicy.cs
using SqlFerret.Core.Normalization;

namespace SqlFerret.Core.Parameters;

public enum RedactionMode { Off, Hash, Masked, Full }

public class RedactionPolicy
{
    private static readonly string[] DefaultSensitive = { "password", "token", "secret", "email" };
    private readonly RedactionMode _mode;
    private readonly IReadOnlyList<string> _sensitive;

    public RedactionPolicy(RedactionMode mode, IReadOnlyList<string>? sensitiveNameSubstrings = null)
    {
        _mode = mode;
        _sensitive = sensitiveNameSubstrings ?? DefaultSensitive;
    }

    public (string storedValue, bool redacted) Apply(string? paramName, string valueText)
    {
        bool forced = paramName is not null &&
            _sensitive.Any(s => paramName.Contains(s, StringComparison.OrdinalIgnoreCase));

        var mode = forced ? RedactionMode.Hash : _mode;
        return mode switch
        {
            RedactionMode.Off    => ("", true),
            RedactionMode.Full   => (valueText, false),
            RedactionMode.Hash   => (Fingerprint.Hash(valueText), true),
            RedactionMode.Masked => (new string('*', Math.Clamp(valueText.Length, 1, 8)), true),
            _ => (valueText, false)
        };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter RedactionPolicyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): redaction policy"
```

---

## Task 7: ParameterExtractor

**Files:**
- Create: `src/SqlFerret.Core/Parameters/ParameterExtractor.cs`
- Test: `tests/SqlFerret.Core.Tests/ParameterExtractorTests.cs`

**Interfaces:**
- Produces: `static class ParameterExtractor` with `IReadOnlyList<RawParameter> Extract(EventClass eventClass, string sqlText)`. For `RpcCall` text shaped like `exec proc @a = 1, @b = N'x'`, parse named params. For batches containing `sp_executesql N'...', N'@p int', @p = 5`, parse the trailing assignments. Returns `[]` when none found. Confidence: named RPC params `0.9`; sp_executesql `0.7`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/ParameterExtractorTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using Xunit;

public class ParameterExtractorTests
{
    [Fact]
    public void Extracts_named_rpc_parameters()
    {
        var ps = ParameterExtractor.Extract(EventClass.RpcCall, "exec dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR'");
        Assert.Equal(2, ps.Count);
        Assert.Equal("@OrderId", ps[0].Name);
        Assert.Equal("123", ps[0].ValueText);
        Assert.Equal("@Culture", ps[1].Name);
        Assert.Equal("N'fr-FR'", ps[1].ValueText);
    }

    [Fact]
    public void No_params_returns_empty()
    {
        var ps = ParameterExtractor.Extract(EventClass.SqlBatch, "select * from t");
        Assert.Empty(ps);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ParameterExtractorTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Parameters/ParameterExtractor.cs
using System.Text.RegularExpressions;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Parameters;

public static class ParameterExtractor
{
    // Matches "@name = <value>" where value is a quoted string (incl. N'') or a bare token.
    private static readonly Regex Assignment = new(
        @"(?<name>@\w+)\s*=\s*(?<value>N?'(?:[^']|'')*'|[^,;\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<RawParameter> Extract(EventClass eventClass, string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText)) return Array.Empty<RawParameter>();

        double confidence = sqlText.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase) ? 0.7
                          : eventClass == EventClass.RpcCall ? 0.9 : 0.6;

        var result = new List<RawParameter>();
        int ordinal = 0;
        foreach (Match m in Assignment.Matches(sqlText))
        {
            result.Add(new RawParameter(
                Ordinal: ordinal++,
                Name: m.Groups["name"].Value,
                SourceKind: ParameterSourceKind.RpcParameter,
                SqlTypeGuess: GuessType(m.Groups["value"].Value),
                ValueText: m.Groups["value"].Value,
                ParseConfidence: confidence));
        }
        return result;
    }

    private static string GuessType(string value)
    {
        if (value.StartsWith("N'")) return "nvarchar";
        if (value.StartsWith("'"))  return "varchar";
        if (long.TryParse(value, out _)) return "int";
        if (decimal.TryParse(value, out _)) return "decimal";
        return "unknown";
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ParameterExtractorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): parameter extractor"
```

---

## Task 8: FilterRule + FilterCompiler

**Files:**
- Create: `src/SqlFerret.Core/Filtering/FilterRule.cs`, `src/SqlFerret.Core/Filtering/FilterCompiler.cs`
- Test: `tests/SqlFerret.Core.Tests/FilterCompilerTests.cs`

**Interfaces:**
- Produces:
  - `record FilterRule(string Id, string Field, string Op, string[]? Values, string? Value, string Stage, string Action, bool Enabled)` (`Op` ∈ `eq,neq,gt,lt,gte,lte,in,like`; `Stage` ∈ `ingest,view`; `Action` ∈ `exclude,keep`).
  - `static class FilterCompiler` with:
    - `string ToWhereClause(IEnumerable<FilterRule> rules)` → a SQL boolean over the `executions` columns (only `Enabled && Stage=="view"` rules), AND-combined, returning `"1=1"` when none. Field/value are whitelisted/escaped (single-quote doubling); numeric ops emitted unquoted.
    - `Func<ExecutionEvent, bool> ToIngestPredicate(IEnumerable<FilterRule> rules)` → true = keep the event (only `Enabled && Stage=="ingest"` rules applied).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/FilterCompilerTests.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Model;
using Xunit;

public class FilterCompilerTests
{
    [Fact]
    public void View_exclude_in_list_becomes_not_in()
    {
        var r = new FilterRule("noise", "object_name", "in",
            new[] { "sp_cursorclose", "sp_unprepare" }, null, "view", "exclude", true);
        var where = FilterCompiler.ToWhereClause(new[] { r });
        Assert.Contains("object_name NOT IN ('sp_cursorclose', 'sp_unprepare')", where);
    }

    [Fact]
    public void View_numeric_gt_unquoted()
    {
        var r = new FilterRule("slow", "duration_us", "gt", null, "10000", "view", "keep", true);
        var where = FilterCompiler.ToWhereClause(new[] { r });
        Assert.Contains("duration_us > 10000", where);
    }

    [Fact]
    public void Disabled_and_ingest_rules_ignored_in_where()
    {
        var disabled = new FilterRule("d", "database_name", "eq", null, "tempdb", "view", "exclude", false);
        var ingest   = new FilterRule("i", "is_system", "eq", null, "true", "ingest", "exclude", true);
        Assert.Equal("1=1", FilterCompiler.ToWhereClause(new[] { disabled, ingest }));
    }

    [Fact]
    public void Ingest_predicate_drops_excluded_object()
    {
        var r = new FilterRule("noise", "object_name", "eq", null, "sp_reset_connection", "ingest", "exclude", true);
        var keep = FilterCompiler.ToIngestPredicate(new[] { r });
        var dropped = new ExecutionEvent { EventName="rpc_completed", SqlTextRaw="x", XeFileName="a", ObjectName="sp_reset_connection" };
        var kept    = new ExecutionEvent { EventName="rpc_completed", SqlTextRaw="x", XeFileName="a", ObjectName="dbo.Real" };
        Assert.False(keep(dropped));
        Assert.True(keep(kept));
    }

    [Fact]
    public void Sql_injection_in_value_is_escaped()
    {
        var r = new FilterRule("x", "login_name", "eq", null, "a'); DROP TABLE executions;--", "view", "exclude", true);
        var where = FilterCompiler.ToWhereClause(new[] { r });
        Assert.Contains("''", where);            // quote doubled
        Assert.DoesNotContain("DROP TABLE executions;--'", where.Replace("''", "")); // not closed early
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter FilterCompilerTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Filtering/FilterRule.cs
namespace SqlFerret.Core.Filtering;

public record FilterRule(
    string Id, string Field, string Op,
    string[]? Values, string? Value,
    string Stage, string Action, bool Enabled);
```

```csharp
// src/SqlFerret.Core/Filtering/FilterCompiler.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Filtering;

public static class FilterCompiler
{
    private static readonly HashSet<string> NumericFields = new()
    {
        "duration_us", "cpu_time_us", "logical_reads", "physical_reads",
        "writes", "row_count", "session_id"
    };

    private static readonly HashSet<string> AllowedFields = new(NumericFields)
    {
        "object_name", "event_name", "event_class", "database_name", "login_name",
        "client_app_name", "client_hostname", "statement_kind", "is_system", "normalized_hash"
    };

    public static string ToWhereClause(IEnumerable<FilterRule> rules)
    {
        var clauses = rules
            .Where(r => r.Enabled && r.Stage == "view" && AllowedFields.Contains(r.Field))
            .Select(BuildClause)
            .Where(c => c is not null)
            .ToList();
        return clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses);
    }

    private static string? BuildClause(FilterRule r)
    {
        bool numeric = NumericFields.Contains(r.Field);
        string Val(string v) => numeric ? v : $"'{v.Replace("'", "''")}'";

        string predicate = r.Op switch
        {
            "in"  => $"{r.Field} IN ({string.Join(", ", (r.Values ?? Array.Empty<string>()).Select(Val))})",
            "eq"  => $"{r.Field} = {Val(r.Value!)}",
            "neq" => $"{r.Field} <> {Val(r.Value!)}",
            "gt"  => $"{r.Field} > {Val(r.Value!)}",
            "lt"  => $"{r.Field} < {Val(r.Value!)}",
            "gte" => $"{r.Field} >= {Val(r.Value!)}",
            "lte" => $"{r.Field} <= {Val(r.Value!)}",
            "like"=> $"{r.Field} LIKE {Val(r.Value!)}",
            _ => ""
        };
        if (predicate == "") return null;

        // exclude → negate; keep → assert
        return r.Action == "exclude" ? NegateInPlace(r, predicate) : $"({predicate})";
    }

    private static string NegateInPlace(FilterRule r, string predicate) => r.Op switch
    {
        "in"  => predicate.Replace(" IN (", " NOT IN ("),
        "eq"  => predicate.Replace(" = ", " <> "),
        "like"=> $"({predicate.Replace(" LIKE ", " NOT LIKE ")})",
        _     => $"(NOT ({predicate}))"
    };

    public static Func<ExecutionEvent, bool> ToIngestPredicate(IEnumerable<FilterRule> rules)
    {
        var ingest = rules.Where(r => r.Enabled && r.Stage == "ingest" && AllowedFields.Contains(r.Field)).ToList();
        return ev =>
        {
            foreach (var r in ingest)
            {
                bool matches = Matches(r, ev);
                if (r.Action == "exclude" && matches) return false; // drop
                if (r.Action == "keep" && !matches) return false;   // keep-only: drop non-matches
            }
            return true;
        };
    }

    private static bool Matches(FilterRule r, ExecutionEvent ev)
    {
        string? field = r.Field switch
        {
            "object_name" => ev.ObjectName,
            "event_name" => ev.EventName,
            "event_class" => ev.EventClass.ToString(),
            "database_name" => ev.DatabaseName,
            "login_name" => ev.LoginName,
            "client_app_name" => ev.ClientAppName,
            "client_hostname" => ev.ClientHostname,
            "is_system" => ev.IsSystem.ToString().ToLowerInvariant(),
            _ => null
        };
        if (field is null) return false;
        return r.Op switch
        {
            "in"  => r.Values?.Contains(field, StringComparer.OrdinalIgnoreCase) ?? false,
            "eq"  => string.Equals(field, r.Value, StringComparison.OrdinalIgnoreCase),
            "neq" => !string.Equals(field, r.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter FilterCompilerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): filter rule model + compiler"
```

---

## Task 9: ReplayBuilder (build-for-SSMS)

**Files:**
- Create: `src/SqlFerret.Core/Replay/ReplayBuilder.cs`
- Test: `tests/SqlFerret.Core.Tests/ReplayBuilderTests.cs`

**Interfaces:**
- Produces: `static class ReplayBuilder` with `ReplayScript Build(ExecutionEvent ev)`:
  - `SqlBatch`/`Statement` → `ReplayScript(ev.SqlTextRaw, ReplayKind.RawBatch, 1.0)` (literals already inline).
  - `RpcCall` with `ObjectName` and parameters → `EXEC <ObjectName> @a = v, …;` → `ReplayKind.ExecProc`, confidence = min param confidence (default 0.9).
  - `RpcCall` text containing `sp_executesql` → returns the raw text, `ReplayKind.SpExecuteSql`, confidence 0.7.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/ReplayBuilderTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Replay;
using Xunit;

public class ReplayBuilderTests
{
    [Fact]
    public void Raw_batch_copies_verbatim()
    {
        var ev = new ExecutionEvent { EventName="sql_batch_completed", EventClass=EventClass.SqlBatch,
            SqlTextRaw="SELECT * FROM t WHERE id = 5", XeFileName="a" };
        var r = ReplayBuilder.Build(ev);
        Assert.Equal(ReplayKind.RawBatch, r.Kind);
        Assert.Equal("SELECT * FROM t WHERE id = 5", r.Sql);
        Assert.Equal(1.0, r.Confidence);
    }

    [Fact]
    public void Rpc_builds_exec()
    {
        var ev = new ExecutionEvent { EventName="rpc_completed", EventClass=EventClass.RpcCall,
            ObjectName="dbo.GetOrder", SqlTextRaw="", XeFileName="a",
            Parameters=new[] {
                new RawParameter(0,"@OrderId",ParameterSourceKind.RpcParameter,"int","123",0.9),
                new RawParameter(1,"@Culture",ParameterSourceKind.RpcParameter,"nvarchar","N'fr-FR'",0.9) } };
        var r = ReplayBuilder.Build(ev);
        Assert.Equal(ReplayKind.ExecProc, r.Kind);
        Assert.Equal("EXEC dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR';", r.Sql);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ReplayBuilderTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Replay/ReplayBuilder.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Replay;

public static class ReplayBuilder
{
    public static ReplayScript Build(ExecutionEvent ev)
    {
        if (ev.EventClass == EventClass.RpcCall &&
            ev.SqlTextRaw.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase))
            return new ReplayScript(ev.SqlTextRaw, ReplayKind.SpExecuteSql, 0.7);

        if (ev.EventClass == EventClass.RpcCall && ev.ObjectName is { Length: > 0 } && ev.Parameters.Count > 0)
        {
            var args = string.Join(", ", ev.Parameters.Select(p => $"{p.Name} = {p.ValueText}"));
            var confidence = ev.Parameters.Min(p => p.ParseConfidence);
            return new ReplayScript($"EXEC {ev.ObjectName} {args};", ReplayKind.ExecProc, confidence);
        }

        return new ReplayScript(ev.SqlTextRaw, ReplayKind.RawBatch, 1.0);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ReplayBuilderTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): replay builder (build-for-SSMS)"
```

---

## Task 10: XelSource (path resolution)

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/XelSource.cs`
- Test: `tests/SqlFerret.Core.Tests/XelSourceTests.cs`

**Interfaces:**
- Produces: `static class XelSource` with `(IReadOnlyList<string> files, long bytesTotal) Resolve(string path)`. Path-is-file → `[path]`. Path-is-dir → all `*.xel` directly in it (non-recursive), sorted by name. Missing path → `FileNotFoundException`. `bytesTotal` = sum of file lengths.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/XelSourceTests.cs
using SqlFerret.Core.Ingestion;
using Xunit;

public class XelSourceTests
{
    [Fact]
    public void Single_file_returns_itself()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var f = Path.Combine(dir, "s_0.xel"); File.WriteAllText(f, "x");
        var (files, bytes) = XelSource.Resolve(f);
        Assert.Single(files);
        Assert.Equal(1, bytes);
    }

    [Fact]
    public void Folder_returns_only_xel_nonrecursive()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "s_0.xel"), "ab");
        File.WriteAllText(Path.Combine(dir, "s_1.xel"), "c");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignore");
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "deep.xel"), "z");
        var (files, bytes) = XelSource.Resolve(dir);
        Assert.Equal(2, files.Count);
        Assert.Equal(3, bytes);
        Assert.EndsWith("s_0.xel", files[0]);
    }

    [Fact]
    public void Missing_path_throws()
        => Assert.Throws<FileNotFoundException>(() => XelSource.Resolve("/no/such/path.xel"));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter XelSourceTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Ingestion/XelSource.cs
namespace SqlFerret.Core.Ingestion;

public static class XelSource
{
    public static (IReadOnlyList<string> files, long bytesTotal) Resolve(string path)
    {
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.xel", SearchOption.TopDirectoryOnly)
                                 .OrderBy(f => f, StringComparer.Ordinal).ToList();
            return (files, files.Sum(f => new FileInfo(f).Length));
        }
        if (File.Exists(path))
            return (new[] { path }, new FileInfo(path).Length);

        throw new FileNotFoundException("XEL path not found", path);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter XelSourceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): XEL path resolution"
```

---

## Task 11: IXeEventData + EventMapper

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/IXeEventData.cs`, `src/SqlFerret.Core/Ingestion/EventMapper.cs`
- Test: `tests/SqlFerret.Core.Tests/EventMapperTests.cs`

**Interfaces:**
- Consumes: `ExecutionEvent`, `EventClass`, `RawParameter` (Task 2); `ParameterExtractor` (Task 7).
- Produces:
  - `interface IXeEventData { string Name { get; } DateTime Timestamp { get; } IReadOnlyDictionary<string, object?> Fields { get; } IReadOnlyDictionary<string, object?> Actions { get; } }`
  - `static class EventMapper` with `ExecutionEvent Map(IXeEventData ev, string fileName, long fileOffset)`:
    - `event_class`: name contains `rpc` → `RpcCall`; contains `sql_batch` → `SqlBatch`; contains `statement` → `Statement`; else `Unknown`.
    - SQL text by class: `SqlBatch` → field `batch_text`; `RpcCall`/`Statement` → field `statement` (fallback `sql_text`). Missing → `EventClass.Unknown`, `SqlTextRaw = ""`.
    - Reads `object_name`, `is_system` from fields; metric fields `duration`, `cpu_time`, `logical_reads`, `physical_reads`, `writes`, `row_count` (microseconds passthrough for duration/cpu — XE batch/rpc completed report these in µs); dimension **actions** `database_name`, `server_principal_name`→login, `client_hostname`, `client_app_name`, `session_id`.
    - Parameters via `ParameterExtractor.Extract(class, sqlText)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/EventMapperTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class EventMapperTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    [Fact]
    public void Maps_rpc_completed_with_params_and_metrics()
    {
        var ev = new FakeEvent("rpc_completed", new DateTime(2026,1,1),
            Fields: new Dictionary<string, object?> {
                ["statement"] = "exec dbo.GetOrder @OrderId = 123",
                ["object_name"] = "dbo.GetOrder",
                ["duration"] = 4000L, ["cpu_time"] = 1000L, ["logical_reads"] = 50L },
            Actions: new Dictionary<string, object?> {
                ["database_name"] = "Sales", ["session_id"] = 57 });

        var e = EventMapper.Map(ev, "s_0.xel", 7);

        Assert.Equal(EventClass.RpcCall, e.EventClass);
        Assert.Equal("dbo.GetOrder", e.ObjectName);
        Assert.Equal(4000L, e.DurationUs);
        Assert.Equal("Sales", e.DatabaseName);
        Assert.Equal(57, e.SessionId);
        Assert.Single(e.Parameters);
        Assert.Equal(7, e.FileOffset);
    }

    [Fact]
    public void Event_without_sql_is_unknown()
    {
        var ev = new FakeEvent("login", new DateTime(2026,1,1),
            new Dictionary<string, object?>(), new Dictionary<string, object?>());
        var e = EventMapper.Map(ev, "s_0.xel", 0);
        Assert.Equal(EventClass.Unknown, e.EventClass);
        Assert.Equal("", e.SqlTextRaw);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter EventMapperTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Ingestion/IXeEventData.cs
namespace SqlFerret.Core.Ingestion;

public interface IXeEventData
{
    string Name { get; }
    DateTime Timestamp { get; }
    IReadOnlyDictionary<string, object?> Fields { get; }
    IReadOnlyDictionary<string, object?> Actions { get; }
}
```

```csharp
// src/SqlFerret.Core/Ingestion/EventMapper.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;

namespace SqlFerret.Core.Ingestion;

public static class EventMapper
{
    public static ExecutionEvent Map(IXeEventData ev, string fileName, long fileOffset)
    {
        var cls = Classify(ev.Name);
        var sql = ExtractSql(ev, cls);
        if (sql is null) cls = EventClass.Unknown;

        var parameters = sql is null ? Array.Empty<RawParameter>()
            : (IReadOnlyList<RawParameter>)ParameterExtractor.Extract(cls, sql);

        return new ExecutionEvent
        {
            CapturedAt = ev.Timestamp,
            EventName = ev.Name,
            EventClass = cls,
            ObjectName = Str(ev.Fields, "object_name"),
            IsSystem = Bool(ev.Fields, "is_system"),
            DatabaseName = Str(ev.Actions, "database_name"),
            LoginName = Str(ev.Actions, "server_principal_name") ?? Str(ev.Actions, "username"),
            ClientHostname = Str(ev.Actions, "client_hostname"),
            ClientAppName = Str(ev.Actions, "client_app_name"),
            SessionId = Int(ev.Actions, "session_id"),
            DurationUs = Long(ev.Fields, "duration"),
            CpuTimeUs = Long(ev.Fields, "cpu_time"),
            LogicalReads = Long(ev.Fields, "logical_reads"),
            PhysicalReads = Long(ev.Fields, "physical_reads"),
            Writes = Long(ev.Fields, "writes"),
            RowCount = Long(ev.Fields, "row_count"),
            QueryHash = Str(ev.Actions, "query_hash"),
            QueryPlanHash = Str(ev.Actions, "query_plan_hash"),
            SqlTextRaw = sql ?? "",
            Parameters = parameters,
            XeFileName = fileName,
            FileOffset = fileOffset,
        };
    }

    private static EventClass Classify(string name) =>
        name.Contains("rpc", StringComparison.OrdinalIgnoreCase) ? EventClass.RpcCall :
        name.Contains("sql_batch", StringComparison.OrdinalIgnoreCase) ? EventClass.SqlBatch :
        name.Contains("statement", StringComparison.OrdinalIgnoreCase) ? EventClass.Statement :
        EventClass.Unknown;

    private static string? ExtractSql(IXeEventData ev, EventClass cls) => cls switch
    {
        EventClass.SqlBatch => Str(ev.Fields, "batch_text"),
        EventClass.RpcCall or EventClass.Statement => Str(ev.Fields, "statement") ?? Str(ev.Fields, "sql_text"),
        _ => null
    };

    private static string? Str(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null ? v.ToString() : null;
    private static bool Bool(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null && Convert.ToBoolean(v);
    private static int? Int(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null ? Convert.ToInt32(v) : null;
    private static long? Long(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null ? Convert.ToInt64(v) : null;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter EventMapperTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): event mapper + IXeEventData seam"
```

---

## Task 12: DuckDbProject schema

**Files:**
- Create: `src/SqlFerret.Core/Storage/DuckDbProject.cs`, `src/SqlFerret.Core/Storage/PreparedRow.cs`
- Test: `tests/SqlFerret.Core.Tests/DuckDbProjectSchemaTests.cs`

**Interfaces:**
- Produces:
  - `record PreparedRow(ExecutionEvent Event, SqlFerret.Core.Model.NormalizedQuery Normalized, IReadOnlyList<(int Ordinal, string? Name, string SourceKind, string? TypeGuess, string Value, bool Redacted, bool Truncated, double Confidence)> Parameters)`
  - `class DuckDbProject : IDisposable` with `static DuckDbProject Open(string path)` (creates file + schema if absent; opens otherwise) and `DuckDB.NET.Data.DuckDBConnection Connection { get; }`. Schema exactly the four tables from spec §4.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/DuckDbProjectSchemaTests.cs
using SqlFerret.Core.Storage;
using Xunit;

public class DuckDbProjectSchemaTests
{
    [Fact]
    public void Open_creates_all_tables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var project = DuckDbProject.Open(path))
            {
                using var cmd = project.Connection.CreateCommand();
                cmd.CommandText =
                    "SELECT count(*) FROM information_schema.tables " +
                    "WHERE table_name IN ('ingestion_runs','executions','normalized_queries','execution_parameters')";
                Assert.Equal(4L, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter DuckDbProjectSchemaTests`
Expected: FAIL.

- [ ] **Step 3: Implement schema (PreparedRow + DuckDbProject.Open + CreateSchema)**

```csharp
// src/SqlFerret.Core/Storage/PreparedRow.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Storage;

public record PreparedRow(
    ExecutionEvent Event,
    NormalizedQuery Normalized,
    IReadOnlyList<(int Ordinal, string? Name, string SourceKind, string? TypeGuess,
                   string Value, bool Redacted, bool Truncated, double Confidence)> Parameters);
```

```csharp
// src/SqlFerret.Core/Storage/DuckDbProject.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Storage;

public sealed class DuckDbProject : IDisposable
{
    public DuckDBConnection Connection { get; }

    private DuckDbProject(DuckDBConnection conn) => Connection = conn;

    public static DuckDbProject Open(string path)
    {
        var conn = new DuckDBConnection($"Data Source={path}");
        conn.Open();
        CreateSchema(conn);
        return new DuckDbProject(conn);
    }

    private static void CreateSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS ingestion_runs (
          run_id BIGINT PRIMARY KEY, source_path TEXT, files_count INTEGER, bytes_total BIGINT,
          started_at TIMESTAMP, finished_at TIMESTAMP, events_read BIGINT, events_mapped BIGINT,
          events_unmapped BIGINT, events_cleaned BIGINT, tokenize_failures BIGINT,
          normalizer_version INTEGER, redaction_policy TEXT);

        CREATE TABLE IF NOT EXISTS executions (
          execution_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP, event_name TEXT,
          event_class TEXT, object_name TEXT, is_system BOOLEAN, database_name TEXT, login_name TEXT,
          client_hostname TEXT, client_app_name TEXT, session_id INTEGER, duration_us BIGINT,
          cpu_time_us BIGINT, logical_reads BIGINT, physical_reads BIGINT, writes BIGINT, row_count BIGINT,
          query_hash TEXT, query_plan_hash TEXT, sql_text_raw TEXT, normalized_hash TEXT,
          xe_file_name TEXT, file_offset BIGINT);

        CREATE TABLE IF NOT EXISTS normalized_queries (
          normalized_hash TEXT PRIMARY KEY, normalized_sql TEXT, statement_kind TEXT,
          primary_table TEXT, normalizer_version INTEGER, first_seen_at TIMESTAMP, last_seen_at TIMESTAMP);

        CREATE TABLE IF NOT EXISTS execution_parameters (
          execution_id BIGINT, ordinal INTEGER, name TEXT, source_kind TEXT, sql_type_guess TEXT,
          value_text TEXT, value_redacted BOOLEAN, is_truncated BOOLEAN, parse_confidence DOUBLE);
        """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Connection.Dispose();
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter DuckDbProjectSchemaTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): DuckDB project schema"
```

---

## Task 13: DuckDbProject ingestion runs + batch insert

**Files:**
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs`
- Test: `tests/SqlFerret.Core.Tests/DuckDbProjectInsertTests.cs`

**Interfaces:**
- Produces, on `DuckDbProject`:
  - `long BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy)` → inserts an `ingestion_runs` row with `started_at = now`, `normalizer_version = QueryNormalizer.Version`, counters 0, `finished_at = NULL`; returns `run_id` (max+1).
  - `void InsertBatch(long runId, IReadOnlyList<PreparedRow> rows)` → appends to `executions` + `execution_parameters`, and upserts `normalized_queries` (insert if new, else update `last_seen_at`). Uses sequential `execution_id` from a project counter. One transaction.
  - `void FinishRun(long runId, long read, long mapped, long unmapped, long cleaned, long tokenizeFailures)` → sets counters + `finished_at = now`.
  - `long NextExecutionId()` internal counter seeded from `MAX(execution_id)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/DuckDbProjectInsertTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class DuckDbProjectInsertTests
{
    [Fact]
    public void Insert_batch_writes_executions_params_and_signature()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginRun("logs/", 1, 100, "masked");

            var ev = new ExecutionEvent { EventName="rpc_completed", EventClass=EventClass.RpcCall,
                ObjectName="dbo.P", SqlTextRaw="exec dbo.P @a = 1", DatabaseName="Sales",
                SessionId=5, DurationUs=4000, CapturedAt=new DateTime(2026,1,1), XeFileName="s_0.xel" };
            var nq = new NormalizedQuery("exec dbo.p @a = ?", "hash1", "EXEC", "dbo.P", false);
            var row = new PreparedRow(ev, nq, new[] { (0,(string?)"@a","rpc_parameter",(string?)"int","1",false,false,0.9) });

            p.InsertBatch(run, new[] { row });
            p.FinishRun(run, read:1, mapped:1, unmapped:0, cleaned:0, tokenizeFailures:0);

            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM executions"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM execution_parameters"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM normalized_queries"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT finished_at IS NOT NULL FROM ingestion_runs"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter DuckDbProjectInsertTests`
Expected: FAIL.

- [ ] **Step 3: Implement insert methods**

Add to `DuckDbProject` (fields + methods). Use parameterized commands for INSERTs (KISS over the Appender for v1 — correctness first; if profiling later shows batch insert is the bottleneck, switch the `executions` insert to `DuckDBAppender`):

```csharp
// add: using SqlFerret.Core.Model; using SqlFerret.Core.Normalization;
private long _nextExecutionId = -1;
private long _nextRunId = -1;

private long Scalar(string sql)
{
    using var c = Connection.CreateCommand(); c.CommandText = sql;
    var v = c.ExecuteScalar();
    return v is null or DBNull ? 0 : Convert.ToInt64(v);
}

public long BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy)
{
    if (_nextRunId < 0) _nextRunId = Scalar("SELECT COALESCE(MAX(run_id),0) FROM ingestion_runs") + 1;
    long runId = _nextRunId++;
    using var c = Connection.CreateCommand();
    c.CommandText = """
      INSERT INTO ingestion_runs(run_id, source_path, files_count, bytes_total, started_at,
        finished_at, events_read, events_mapped, events_unmapped, events_cleaned,
        tokenize_failures, normalizer_version, redaction_policy)
      VALUES ($id,$src,$fc,$bt, now(), NULL, 0,0,0,0,0, $nv, $rp)
      """;
    Add(c, "$id", runId); Add(c, "$src", sourcePath); Add(c, "$fc", filesCount);
    Add(c, "$bt", bytesTotal); Add(c, "$nv", QueryNormalizer.Version); Add(c, "$rp", redactionPolicy);
    c.ExecuteNonQuery();
    return runId;
}

public long NextExecutionId()
{
    if (_nextExecutionId < 0) _nextExecutionId = Scalar("SELECT COALESCE(MAX(execution_id),0) FROM executions");
    return ++_nextExecutionId;
}

public void InsertBatch(long runId, IReadOnlyList<PreparedRow> rows)
{
    using var tx = Connection.BeginTransaction();
    foreach (var r in rows)
    {
        long id = NextExecutionId();
        InsertExecution(tx, id, runId, r);
        UpsertSignature(tx, r);
        foreach (var p in r.Parameters) InsertParameter(tx, id, p);
    }
    tx.Commit();
}

private void InsertExecution(DuckDB.NET.Data.DuckDBTransaction tx, long id, long runId, PreparedRow r)
{
    var e = r.Event;
    using var c = Connection.CreateCommand(); c.Transaction = tx;
    c.CommandText = """
      INSERT INTO executions VALUES ($id,$run,$ts,$en,$ec,$obj,$sys,$db,$login,$host,$app,$sid,
        $dur,$cpu,$lr,$pr,$w,$rows,$qh,$qph,$raw,$nh,$file,$off)
      """;
    Add(c,"$id",id); Add(c,"$run",runId); Add(c,"$ts",e.CapturedAt); Add(c,"$en",e.EventName);
    Add(c,"$ec",e.EventClass.ToString()); Add(c,"$obj",(object?)e.ObjectName); Add(c,"$sys",e.IsSystem);
    Add(c,"$db",(object?)e.DatabaseName); Add(c,"$login",(object?)e.LoginName);
    Add(c,"$host",(object?)e.ClientHostname); Add(c,"$app",(object?)e.ClientAppName);
    Add(c,"$sid",(object?)e.SessionId); Add(c,"$dur",(object?)e.DurationUs); Add(c,"$cpu",(object?)e.CpuTimeUs);
    Add(c,"$lr",(object?)e.LogicalReads); Add(c,"$pr",(object?)e.PhysicalReads); Add(c,"$w",(object?)e.Writes);
    Add(c,"$rows",(object?)e.RowCount); Add(c,"$qh",(object?)e.QueryHash); Add(c,"$qph",(object?)e.QueryPlanHash);
    Add(c,"$raw",e.SqlTextRaw); Add(c,"$nh",r.Normalized.NormalizedHash);
    Add(c,"$file",e.XeFileName); Add(c,"$off",e.FileOffset);
    c.ExecuteNonQuery();
}

private void UpsertSignature(DuckDB.NET.Data.DuckDBTransaction tx, PreparedRow r)
{
    var n = r.Normalized;
    using var c = Connection.CreateCommand(); c.Transaction = tx;
    c.CommandText = """
      INSERT INTO normalized_queries VALUES ($h,$sql,$kind,$tbl,$ver,$ts,$ts)
      ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at = $ts
      """;
    Add(c,"$h",n.NormalizedHash); Add(c,"$sql",n.NormalizedSql); Add(c,"$kind",n.StatementKind);
    Add(c,"$tbl",(object?)n.PrimaryTable); Add(c,"$ver",QueryNormalizer.Version); Add(c,"$ts",r.Event.CapturedAt);
    c.ExecuteNonQuery();
}

private void InsertParameter(DuckDB.NET.Data.DuckDBTransaction tx, long execId,
    (int Ordinal, string? Name, string SourceKind, string? TypeGuess, string Value, bool Redacted, bool Truncated, double Confidence) p)
{
    using var c = Connection.CreateCommand(); c.Transaction = tx;
    c.CommandText = "INSERT INTO execution_parameters VALUES ($id,$ord,$name,$sk,$tg,$val,$red,$trunc,$conf)";
    Add(c,"$id",execId); Add(c,"$ord",p.Ordinal); Add(c,"$name",(object?)p.Name); Add(c,"$sk",p.SourceKind);
    Add(c,"$tg",(object?)p.TypeGuess); Add(c,"$val",p.Value); Add(c,"$red",p.Redacted);
    Add(c,"$trunc",p.Truncated); Add(c,"$conf",p.Confidence);
    c.ExecuteNonQuery();
}

public void FinishRun(long runId, long read, long mapped, long unmapped, long cleaned, long tokenizeFailures)
{
    using var c = Connection.CreateCommand();
    c.CommandText = """
      UPDATE ingestion_runs SET finished_at = now(), events_read=$r, events_mapped=$m,
        events_unmapped=$u, events_cleaned=$cl, tokenize_failures=$tf WHERE run_id=$id
      """;
    Add(c,"$r",read); Add(c,"$m",mapped); Add(c,"$u",unmapped); Add(c,"$cl",cleaned);
    Add(c,"$tf",tokenizeFailures); Add(c,"$id",runId);
    c.ExecuteNonQuery();
}

private static void Add(System.Data.IDbCommand c, string name, object? value)
{
    var p = c.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; c.Parameters.Add(p);
}
```

> **Implementer note:** DuckDB.NET named parameters use `$name`. If the installed DuckDB.NET version requires positional parameters instead, switch `CommandText` placeholders to `?` and add parameters in order — keep the method shapes identical. Verify with the Task 12/13 tests.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter DuckDbProjectInsertTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): DuckDB ingestion runs + batch insert"
```

---

## Task 14: IngestionService (offline pipeline, fake reader)

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/IngestionOptions.cs`, `src/SqlFerret.Core/Ingestion/IngestionResult.cs`, `src/SqlFerret.Core/Ingestion/IngestionService.cs`
- Test: `tests/SqlFerret.Core.Tests/IngestionServiceTests.cs`

**Interfaces:**
- Consumes: EventMapper, QueryNormalizer, ParameterExtractor (already inside EventMapper), RedactionPolicy, FilterCompiler, DuckDbProject.
- Produces:
  - `record IngestionOptions(RedactionMode Redaction, IReadOnlyList<FilterRule> Filters, int BatchSize = 5000)`
  - `record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures)`
  - `class IngestionService(DuckDbProject project, IngestionOptions options)` with `IngestionResult Ingest(string sourcePath, IEnumerable<(IXeEventData ev, string fileName, long offset)> events)`. (The real XELite reader is injected as that `events` sequence in Task 15; this task tests the pipeline with a hand-built sequence so it needs no `.xel`.)
  - Pipeline per event: map → if `Unknown`/empty SQL → `unmapped++`, skip; apply ingest filter predicate → if dropped, `cleaned++`, skip; normalize → build PreparedRow with redacted params → buffer; flush every `BatchSize`. Track `tokenizeFailures` from `Normalized.TokenizeFailed`. Calls `BeginRun`/`InsertBatch`/`FinishRun`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/IngestionServiceTests.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class IngestionServiceTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    private static (IXeEventData, string, long) Batch(string sql, long offset, string db = "Sales") =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026,1,1),
            new Dictionary<string, object?> { ["batch_text"]=sql, ["duration"]=1000L },
            new Dictionary<string, object?> { ["database_name"]=db, ["session_id"]=1 }), "s_0.xel", offset);

    [Fact]
    public void Ingests_maps_normalizes_and_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var opts = new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>(), BatchSize: 2);
            var svc = new IngestionService(project, opts);

            var events = new[] {
                Batch("SELECT * FROM dbo.Users WHERE Id = 1", 0),
                Batch("SELECT * FROM dbo.Users WHERE Id = 2", 1),     // same signature
                (new FakeEvent("login", new DateTime(2026,1,1), new Dictionary<string,object?>(),
                    new Dictionary<string,object?>()), "s_0.xel", 2L), // unmapped
            };

            var result = svc.Ingest("logs/", events);

            Assert.Equal(3, result.Read);
            Assert.Equal(2, result.Mapped);
            Assert.Equal(1, result.Unmapped);

            using var c = project.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM normalized_queries";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar())); // grouped
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ingest_filter_drops_and_counts_cleaned()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var rule = new FilterRule("noise","database_name","eq",null,"tempdb","ingest","exclude",true);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, new[]{rule}));
            var result = svc.Ingest("logs/", new[] { Batch("SELECT 1", 0, db:"tempdb") });
            Assert.Equal(1, result.Cleaned);
            Assert.Equal(0, result.Mapped);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter IngestionServiceTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Ingestion/IngestionOptions.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Parameters;

namespace SqlFerret.Core.Ingestion;

public record IngestionOptions(RedactionMode Redaction, IReadOnlyList<FilterRule> Filters, int BatchSize = 5000);
```

```csharp
// src/SqlFerret.Core/Ingestion/IngestionResult.cs
namespace SqlFerret.Core.Ingestion;

public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures);
```

```csharp
// src/SqlFerret.Core/Ingestion/IngestionService.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Ingestion;

public class IngestionService
{
    private readonly DuckDbProject _project;
    private readonly IngestionOptions _options;
    private readonly RedactionPolicy _redaction;
    private readonly Func<ExecutionEvent, bool> _ingestKeep;

    public IngestionService(DuckDbProject project, IngestionOptions options)
    {
        _project = project;
        _options = options;
        _redaction = new RedactionPolicy(options.Redaction);
        _ingestKeep = FilterCompiler.ToIngestPredicate(options.Filters);
    }

    public IngestionResult Ingest(string sourcePath, IEnumerable<(IXeEventData ev, string fileName, long offset)> events)
    {
        long runId = _project.BeginRun(sourcePath, filesCount: 1, bytesTotal: 0,
            redactionPolicy: _options.Redaction.ToString().ToLowerInvariant());

        long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
        var buffer = new List<PreparedRow>(_options.BatchSize);

        foreach (var (ev, fileName, offset) in events)
        {
            read++;
            var e = EventMapper.Map(ev, fileName, offset);
            if (e.EventClass == EventClass.Unknown || string.IsNullOrEmpty(e.SqlTextRaw)) { unmapped++; continue; }
            if (!_ingestKeep(e)) { cleaned++; continue; }

            var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
            if (nq.TokenizeFailed) tokenizeFailures++;

            buffer.Add(new PreparedRow(e, nq, RedactParams(e)));
            mapped++;

            if (buffer.Count >= _options.BatchSize) { _project.InsertBatch(runId, buffer); buffer.Clear(); }
        }
        if (buffer.Count > 0) _project.InsertBatch(runId, buffer);

        _project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures);
        return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures);
    }

    private List<(int, string?, string, string?, string, bool, bool, double)> RedactParams(ExecutionEvent e)
    {
        var list = new List<(int, string?, string, string?, string, bool, bool, double)>();
        foreach (var p in e.Parameters)
        {
            var (stored, redacted) = _redaction.Apply(p.Name, p.ValueText);
            if (_options.Redaction == RedactionMode.Off) continue; // off → no parameter rows
            list.Add((p.Ordinal, p.Name, p.SourceKind.ToString().ToLowerInvariant(),
                      p.SqlTypeGuess, stored, redacted, false, p.ParseConfidence));
        }
        return list;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter IngestionServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): ingestion service pipeline"
```

---

## Task 15: XelReader (real XELite) + committed fixture

**Files:**
- Create: `src/SqlFerret.Core/Ingestion/XelReader.cs`, `tests/SqlFerret.Core.Tests/Fixtures/generate-fixtures.md`, `tests/SqlFerret.Core.Tests/Fixtures/sample_basic.xel`
- Test: `tests/SqlFerret.Core.Tests/XelReaderTests.cs`
- Modify: `tests/SqlFerret.Core.Tests/SqlFerret.Core.Tests.csproj` (copy fixtures to output)

**Interfaces:**
- Produces: `class XelReader` with `IEnumerable<(IXeEventData ev, string fileName, long offset)> Read(IReadOnlyList<string> files)`. Internally uses XELite `XEFileEventStreamer`, adapting each `IXEvent` to a private `XeEventDataAdapter : IXeEventData`. `offset` is a per-file 0-based event ordinal (XELite does not expose byte offsets; documented surrogate for resume/dedup).

- [ ] **Step 1: Generate and commit a small fixture (one-time, documented)**

Write `tests/SqlFerret.Core.Tests/Fixtures/generate-fixtures.md`:

````markdown
# Generating .xel fixtures

Run once on a machine with Docker/Podman. Produces `sample_basic.xel`, committed to the repo.

```bash
podman run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Strong!Passw0rd' \
  -p 1433:1433 -d --name sfedge mcr.microsoft.com/azure-sql-edge:latest

# Create an event session writing to /var/opt/mssql/log, run a few queries, stop it:
sqlcmd -S localhost -U sa -P 'Strong!Passw0rd' -Q "
CREATE EVENT SESSION sf ON SERVER
  ADD EVENT sqlserver.sql_batch_completed,
  ADD EVENT sqlserver.rpc_completed
  ADD TARGET package0.event_file(SET filename='/var/opt/mssql/log/sample_basic.xel', max_rollover_files=1)
  WITH (MAX_DISPATCH_LATENCY=1 SECONDS);
ALTER EVENT SESSION sf ON SERVER STATE = START;
SELECT * FROM sys.databases WHERE database_id = 1;
EXEC sp_who;
WAITFOR DELAY '00:00:02';
ALTER EVENT SESSION sf ON SERVER STATE = STOP;"

podman cp sfedge:/var/opt/mssql/log/sample_basic.xel ./sample_basic.xel
# Trim/rename the rollover suffix if present so the committed file is `sample_basic.xel`.
```

The committed fixture must contain at least one `sql_batch_completed` and one `rpc_completed`
event with non-empty SQL text.
````

> If no container runtime is available to the implementer, mark this fixture step blocked and
> ask the user to produce `sample_basic.xel` from any SQL Server instance using the script above.
> The reader code (Step 3) can still be written; the integration test is skipped (see Step 4)
> until the fixture exists.

- [ ] **Step 2: Make fixtures copy to test output**

Add to `tests/SqlFerret.Core.Tests/SqlFerret.Core.Tests.csproj`:

```xml
<ItemGroup>
  <None Include="Fixtures/**/*.xel" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 3: Implement XelReader**

```csharp
// src/SqlFerret.Core/Ingestion/XelReader.cs
using Microsoft.SqlServer.XEvent.XELite;

namespace SqlFerret.Core.Ingestion;

public class XelReader
{
    public IEnumerable<(IXeEventData ev, string fileName, long offset)> Read(IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            long ordinal = 0;
            var collected = new List<IXeEventData>();
            var streamer = new XEFileEventStreamer(file);

            // XELite is push/async; collect synchronously per file (KISS). Files are processed
            // one at a time so memory stays bounded to one file's events; for very large single
            // files, switch to a Channel-bridged IAsyncEnumerable in a later iteration.
            streamer.ReadEventStream(
                xevent =>
                {
                    collected.Add(new XeEventDataAdapter(xevent));
                    return Task.CompletedTask;
                },
                CancellationToken.None).GetAwaiter().GetResult();

            foreach (var ev in collected)
                yield return (ev, name, ordinal++);
        }
    }

    private sealed class XeEventDataAdapter : IXeEventData
    {
        private readonly IXEvent _e;
        public XeEventDataAdapter(IXEvent e) => _e = e;
        public string Name => _e.Name;
        public DateTime Timestamp => _e.Timestamp.UtcDateTime;
        public IReadOnlyDictionary<string, object?> Fields =>
            _e.Fields.ToDictionary(k => k.Key, v => (object?)v.Value);
        public IReadOnlyDictionary<string, object?> Actions =>
            _e.Actions.ToDictionary(k => k.Key, v => (object?)v.Value);
    }
}
```

> **Implementer note:** confirm the XELite type/method names against the installed package
> (`XEFileEventStreamer`, `IXEvent`, `.Fields`, `.Actions`, `.Timestamp`). If the installed
> version exposes `ReadEventStream(Func<IXEvent,Task>, CancellationToken)` with a different
> signature, adapt the call; the adapter shape stays the same.

- [ ] **Step 4: Write the fixture integration test**

```csharp
// tests/SqlFerret.Core.Tests/XelReaderTests.cs
using SqlFerret.Core.Ingestion;
using Xunit;

public class XelReaderTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_basic.xel");

    [SkippableFact]
    public void Reads_events_from_fixture()
    {
        Skip.IfNot(File.Exists(FixturePath), "sample_basic.xel fixture not present");
        var events = new XelReader().Read(new[] { FixturePath }).ToList();
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.ev.Name.Contains("batch") || e.ev.Name.Contains("rpc"));
    }
}
```

Add the `Xunit.SkippableFact` package to the test project:

```bash
dotnet add tests/SqlFerret.Core.Tests package Xunit.SkippableFact
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter XelReaderTests`
Expected: PASS (or SKIPPED if the fixture isn't present yet).

- [ ] **Step 6: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): XELite reader + fixture harness"
```

---

## Task 16: WorkloadQueries (analysis)

**Files:**
- Create: `src/SqlFerret.Core/Analysis/Results.cs`, `src/SqlFerret.Core/Analysis/WorkloadQueries.cs`
- Test: `tests/SqlFerret.Core.Tests/WorkloadQueriesTests.cs`

**Interfaces:**
- Consumes: `DuckDbProject.Connection`, `FilterCompiler.ToWhereClause`.
- Produces:
  - `Results.cs` records: `QueryStat(string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql, long Count, double AvgDurationUs, long P95DurationUs, long MaxDurationUs, long TotalDurationUs)`; `Occurrence(long ExecutionId, DateTime CapturedAt, string? Database, string? Login, long? DurationUs, string SqlTextRaw)`; `ParamImpact(string ValueText, long Count, double AvgDurationUs, long P95DurationUs, long MaxDurationUs)`; `DimensionStat(string Value, long Count, long TotalDurationUs)`; `QualityStat(long EventsRead, long EventsMapped, long EventsUnmapped, long EventsCleaned, long TokenizeFailures)`.
  - `class WorkloadQueries(DuckDBConnection conn)` with:
    - `IReadOnlyList<QueryStat> TopSlow(int limit, string sortColumn, IEnumerable<FilterRule> viewFilters)` (`sortColumn` ∈ `total_duration_us|p95_duration_us|max_duration_us|avg_duration_us`, validated against an allow-list).
    - `IReadOnlyList<QueryStat> TopFrequent(int limit, IEnumerable<FilterRule> viewFilters)` (adds derived `cumulative_cost = count*avg` ordering via `total_duration_us`).
    - `IReadOnlyList<Occurrence> Occurrences(string normalizedHash, int limit)`.
    - `IReadOnlyList<Occurrence> SessionFlow(int sessionId, DateTime from, DateTime to)`.
    - `IReadOnlyList<ParamImpact> ParameterImpact(string normalizedHash, string paramName)`.
    - `IReadOnlyList<DimensionStat> Dimension(string field)` (`field` allow-listed: `database_name|login_name|client_hostname|client_app_name`).
    - `QualityStat Quality(long runId)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/WorkloadQueriesTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class WorkloadQueriesTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    private static (IXeEventData, string, long) Batch(string sql, long dur, long offset) =>
        (new FakeEvent("sql_batch_completed", new DateTime(2026,1,1,0,0,(int)(offset%60)),
            new Dictionary<string, object?> { ["batch_text"]=sql, ["duration"]=dur },
            new Dictionary<string, object?> { ["database_name"]="Sales", ["session_id"]=1 }), "s_0.xel", offset);

    [Fact]
    public void TopSlow_groups_and_orders_by_total()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var project = DuckDbProject.Open(path);
            var svc = new IngestionService(project, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            svc.Ingest("logs/", new[] {
                Batch("SELECT * FROM dbo.A WHERE id = 1", 1000, 0),
                Batch("SELECT * FROM dbo.A WHERE id = 2", 3000, 1),  // same sig as above → total 4000
                Batch("SELECT * FROM dbo.B WHERE id = 9", 500, 2),
            });

            var q = new WorkloadQueries(project.Connection);
            var top = q.TopSlow(10, "total_duration_us", Array.Empty<FilterRule>());

            Assert.Equal(2, top.Count);
            Assert.Equal("dbo.A", top[0].PrimaryTable);   // highest total first
            Assert.Equal(2, top[0].Count);
            Assert.Equal(4000, top[0].TotalDurationUs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter WorkloadQueriesTests`
Expected: FAIL.

- [ ] **Step 3: Implement Results + WorkloadQueries**

```csharp
// src/SqlFerret.Core/Analysis/Results.cs
namespace SqlFerret.Core.Analysis;

public record QueryStat(string NormalizedHash, string StatementKind, string? PrimaryTable,
    string NormalizedSql, long Count, double AvgDurationUs, long P95DurationUs,
    long MaxDurationUs, long TotalDurationUs);
public record Occurrence(long ExecutionId, DateTime CapturedAt, string? Database, string? Login,
    long? DurationUs, string SqlTextRaw);
public record ParamImpact(string ValueText, long Count, double AvgDurationUs, long P95DurationUs, long MaxDurationUs);
public record DimensionStat(string Value, long Count, long TotalDurationUs);
public record QualityStat(long EventsRead, long EventsMapped, long EventsUnmapped, long EventsCleaned, long TokenizeFailures);
```

```csharp
// src/SqlFerret.Core/Analysis/WorkloadQueries.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Filtering;

namespace SqlFerret.Core.Analysis;

public class WorkloadQueries
{
    private static readonly HashSet<string> SortCols = new()
    { "total_duration_us", "p95_duration_us", "max_duration_us", "avg_duration_us" };
    private static readonly HashSet<string> DimFields = new()
    { "database_name", "login_name", "client_hostname", "client_app_name" };

    private readonly DuckDBConnection _conn;
    public WorkloadQueries(DuckDBConnection conn) => _conn = conn;

    public IReadOnlyList<QueryStat> TopSlow(int limit, string sortColumn, IEnumerable<FilterRule> viewFilters)
    {
        if (!SortCols.Contains(sortColumn)) sortColumn = "total_duration_us";
        return QueryStats(limit, sortColumn, viewFilters);
    }

    public IReadOnlyList<QueryStat> TopFrequent(int limit, IEnumerable<FilterRule> viewFilters)
        => QueryStats(limit, "cnt", viewFilters);

    private IReadOnlyList<QueryStat> QueryStats(int limit, string orderBy, IEnumerable<FilterRule> viewFilters)
    {
        string where = FilterCompiler.ToWhereClause(viewFilters);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
          SELECT e.normalized_hash, n.statement_kind, n.primary_table, n.normalized_sql,
                 count(*) AS cnt,
                 avg(e.duration_us) AS avg_duration_us,
                 quantile_cont(e.duration_us, 0.95) AS p95_duration_us,
                 max(e.duration_us) AS max_duration_us,
                 sum(e.duration_us) AS total_duration_us
          FROM executions e JOIN normalized_queries n USING (normalized_hash)
          WHERE {where}
          GROUP BY e.normalized_hash, n.statement_kind, n.primary_table, n.normalized_sql
          ORDER BY {orderBy} DESC
          LIMIT {limit}
          """;
        using var r = cmd.ExecuteReader();
        var list = new List<QueryStat>();
        while (r.Read())
            list.Add(new QueryStat(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(3 - 1),
                r.GetString(3), r.GetInt64(4), r.GetDouble(5),
                (long)r.GetDouble(6), r.GetInt64(7), r.GetInt64(8)));
        return list;
    }

    public IReadOnlyList<Occurrence> Occurrences(string normalizedHash, int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
          SELECT execution_id, captured_at, database_name, login_name, duration_us, sql_text_raw
          FROM executions WHERE normalized_hash = $h ORDER BY captured_at LIMIT $l
          """;
        Add(cmd, "$h", normalizedHash); Add(cmd, "$l", limit);
        return ReadOccurrences(cmd);
    }

    public IReadOnlyList<Occurrence> SessionFlow(int sessionId, DateTime from, DateTime to)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
          SELECT execution_id, captured_at, database_name, login_name, duration_us, sql_text_raw
          FROM executions WHERE session_id = $s AND captured_at BETWEEN $f AND $t ORDER BY captured_at
          """;
        Add(cmd, "$s", sessionId); Add(cmd, "$f", from); Add(cmd, "$t", to);
        return ReadOccurrences(cmd);
    }

    public IReadOnlyList<ParamImpact> ParameterImpact(string normalizedHash, string paramName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
          SELECT p.value_text, count(*) AS cnt, avg(e.duration_us),
                 quantile_cont(e.duration_us, 0.95), max(e.duration_us)
          FROM executions e
          JOIN execution_parameters p ON p.execution_id = e.execution_id
          WHERE e.normalized_hash = $h AND p.name = $n
          GROUP BY p.value_text ORDER BY avg(e.duration_us) DESC
          """;
        Add(cmd, "$h", normalizedHash); Add(cmd, "$n", paramName);
        using var r = cmd.ExecuteReader();
        var list = new List<ParamImpact>();
        while (r.Read())
            list.Add(new ParamImpact(r.GetString(0), r.GetInt64(1), r.GetDouble(2), (long)r.GetDouble(3), r.GetInt64(4)));
        return list;
    }

    public IReadOnlyList<DimensionStat> Dimension(string field)
    {
        if (!DimFields.Contains(field)) throw new ArgumentException($"field not allowed: {field}");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
          SELECT COALESCE({field}, '(none)') AS v, count(*) AS cnt, sum(duration_us) AS total
          FROM executions GROUP BY v ORDER BY total DESC
          """;
        using var r = cmd.ExecuteReader();
        var list = new List<DimensionStat>();
        while (r.Read()) list.Add(new DimensionStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public QualityStat Quality(long runId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
          SELECT events_read, events_mapped, events_unmapped, events_cleaned, tokenize_failures
          FROM ingestion_runs WHERE run_id = $r
          """;
        Add(cmd, "$r", runId);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new QualityStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    private static IReadOnlyList<Occurrence> ReadOccurrences(System.Data.IDbCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Occurrence>();
        while (r.Read())
            list.Add(new Occurrence(r.GetInt64(0), r.GetDateTime(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.GetString(5)));
        return list;
    }

    private static void Add(System.Data.IDbCommand c, string name, object value)
    { var p = c.CreateParameter(); p.ParameterName = name; p.Value = value; c.Parameters.Add(p); }
}
```

> **Implementer note:** the `QueryStat` reader uses ordinal indices — double-check them against the
> SELECT list when wiring up (the `primary_table` null check reads column index 2, value index 2).
> Fix any off-by-one revealed by the test rather than guessing.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter WorkloadQueriesTests`
Expected: PASS.

- [ ] **Step 5: Add tests for Occurrences, SessionFlow, ParameterImpact, Dimension, Quality**

Add focused tests mirroring Step 1's ingest setup, asserting: `Occurrences(hash).Count`, `SessionFlow` ordering by `captured_at`, `ParameterImpact` returns slowest value-set first, `Dimension("database_name")` groups, `Quality(runId)` echoes counters. Run `dotnet test --filter WorkloadQueriesTests` → PASS.

- [ ] **Step 6: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): workload analysis queries"
```

---

## Task 17: Config — DotEnv, SqlFerretConfig, DisplayFormat

**Files:**
- Create: `src/SqlFerret.Core/Config/DotEnv.cs`, `src/SqlFerret.Core/Config/SqlFerretConfig.cs`, `src/SqlFerret.Core/Config/DisplayFormat.cs`
- Test: `tests/SqlFerret.Core.Tests/ConfigTests.cs`

**Interfaces:**
- Produces:
  - `static class DotEnv` → `void Load(string path)`: parse `KEY=VALUE` lines (ignore blanks and `#` comments, strip optional `export ` prefix and surrounding quotes); set each var **only if absent** from the environment (`Environment.GetEnvironmentVariable` null check). Missing file → silent no-op.
  - `record SqlFerretConfig(string DurationUnit, string CpuUnit, string RedactionPolicy, string? ConnectionString, string PlansFolder)` with `static SqlFerretConfig Load(string? jsonPath)`: defaults (`ms,ms,masked,null,./plans`) → overlay JSON file if present → interpolate `${VAR}` in `ConnectionString` from environment.
  - `static class DisplayFormat` → `string Duration(long microseconds, string unit)` (`us|ms|s`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SqlFerret.Core.Tests/ConfigTests.cs
using SqlFerret.Core.Config;
using Xunit;

public class ConfigTests
{
    [Fact]
    public void DotEnv_sets_absent_keys_only()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "# comment\nexport SF_TEST_A=hello\nSF_TEST_B=\"world\"\n");
        Environment.SetEnvironmentVariable("SF_TEST_A", null);
        Environment.SetEnvironmentVariable("SF_TEST_B", "preset");
        DotEnv.Load(f);
        Assert.Equal("hello", Environment.GetEnvironmentVariable("SF_TEST_A"));
        Assert.Equal("preset", Environment.GetEnvironmentVariable("SF_TEST_B")); // not overwritten
    }

    [Fact]
    public void Config_defaults_when_no_file()
    {
        var c = SqlFerretConfig.Load(null);
        Assert.Equal("ms", c.DurationUnit);
        Assert.Equal("masked", c.RedactionPolicy);
        Assert.Equal("./plans", c.PlansFolder);
    }

    [Fact]
    public void Config_interpolates_env_in_connection_string()
    {
        Environment.SetEnvironmentVariable("SF_AUTH", "User ID=sa;Password=x");
        var f = Path.GetTempFileName();
        File.WriteAllText(f, """{ "server": { "connectionString": "Server=h;${SF_AUTH}" } }""");
        var c = SqlFerretConfig.Load(f);
        Assert.Equal("Server=h;User ID=sa;Password=x", c.ConnectionString);
    }

    [Theory]
    [InlineData(1500000, "ms", "1500 ms")]
    [InlineData(1500000, "s", "1.5 s")]
    [InlineData(1500000, "us", "1500000 us")]
    public void DisplayFormat_converts(long us, string unit, string expected)
        => Assert.Equal(expected, DisplayFormat.Duration(us, unit));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter ConfigTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Config/DotEnv.cs
namespace SqlFerret.Core.Config;

public static class DotEnv
{
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ")) line = line["export ".Length..].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, val);
        }
    }
}
```

```csharp
// src/SqlFerret.Core/Config/SqlFerretConfig.cs
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlFerret.Core.Config;

public record SqlFerretConfig(string DurationUnit, string CpuUnit, string RedactionPolicy,
    string? ConnectionString, string PlansFolder)
{
    public static SqlFerretConfig Load(string? jsonPath)
    {
        string durationUnit = "ms", cpuUnit = "ms", redaction = "masked", plans = "./plans";
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
            if (root.TryGetProperty("ingest", out var i) && i.TryGetProperty("redactionPolicy", out var r))
                redaction = r.GetString() ?? redaction;
            if (root.TryGetProperty("server", out var s))
            {
                if (s.TryGetProperty("connectionString", out var cs)) conn = cs.GetString();
                if (s.TryGetProperty("plansFolder", out var pf)) plans = pf.GetString() ?? plans;
            }
        }

        if (conn is not null)
            conn = Regex.Replace(conn, @"\$\{(\w+)\}",
                m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

        return new SqlFerretConfig(durationUnit, cpuUnit, redaction, conn, plans);
    }
}
```

```csharp
// src/SqlFerret.Core/Config/DisplayFormat.cs
using System.Globalization;

namespace SqlFerret.Core.Config;

public static class DisplayFormat
{
    public static string Duration(long microseconds, string unit) => unit switch
    {
        "s"  => $"{(microseconds / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture)} s",
        "us" => $"{microseconds} us",
        _    => $"{microseconds / 1000} ms",
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ConfigTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): config, dotenv loader, display formatting"
```

---

## Task 18: UiState (filters + view layouts)

**Files:**
- Create: `src/SqlFerret.Core/Config/UiState.cs`
- Test: `tests/SqlFerret.Core.Tests/UiStateTests.cs`

**Interfaces:**
- Consumes: `FilterRule`.
- Produces: `class UiState { List<FilterRule> Filters; Dictionary<string, ViewLayout> Views; static UiState Load(string path); void Save(string path); }` with `record ViewLayout(string[] Columns, string Sort)`. Missing/malformed file → empty defaults (never throws). Round-trips via `System.Text.Json` (indented).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/UiStateTests.cs
using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using Xunit;

public class UiStateTests
{
    [Fact]
    public void Roundtrips_filters_and_layouts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui_{Guid.NewGuid():N}.json");
        try
        {
            var state = UiState.Load(path); // missing → empty
            Assert.Empty(state.Filters);
            state.Filters.Add(new FilterRule("noise","object_name","eq",null,"sp_reset_connection","ingest","exclude",true));
            state.Views["topSlow"] = new UiState.ViewLayout(new[]{"kind","signature","total"}, "total_desc");
            state.Save(path);

            var reloaded = UiState.Load(path);
            Assert.Single(reloaded.Filters);
            Assert.Equal("sp_reset_connection", reloaded.Filters[0].Value);
            Assert.Equal("total_desc", reloaded.Views["topSlow"].Sort);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Malformed_file_yields_empty_state()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{ not json");
        var state = UiState.Load(path);
        Assert.Empty(state.Filters);
        Assert.Empty(state.Views);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter UiStateTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Config/UiState.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFerret.Core.Filtering;

namespace SqlFerret.Core.Config;

public class UiState
{
    public record ViewLayout(string[] Columns, string Sort);

    public List<FilterRule> Filters { get; set; } = new();
    public Dictionary<string, ViewLayout> Views { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static UiState Load(string path)
    {
        if (!File.Exists(path)) return new UiState();
        try { return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Opts) ?? new UiState(); }
        catch { return new UiState(); }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter UiStateTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): UI state (filters + view layouts) persistence"
```

---

## Task 19: EstimatedPlanService

**Files:**
- Create: `src/SqlFerret.Core/Server/EstimatedPlanService.cs`
- Test: `tests/SqlFerret.Core.Tests/EstimatedPlanServiceTests.cs`

**Interfaces:**
- Consumes: `ReplayScript`, `ReplayBuilder`, `ExecutionEvent`.
- Produces: `class EstimatedPlanService(string connectionString, string plansFolder)` with:
  - `string Save(string planId, string showplanXml)` (pure/offline) — writes `<plansFolder>/<planId>.sqlplan`, creating the folder; returns the file path. **Unit-tested.**
  - `Task<string> CaptureAsync(ExecutionEvent ev, string planId, CancellationToken ct = default)` — builds the statement via `ReplayBuilder`, opens a `SqlConnection`, runs `USE [db]` (when `ev.DatabaseName` set), `SET SHOWPLAN_XML ON;`, executes the batch, reads the single-cell XML result, then `Save(...)`. **Integration-only (skipped without a server).**

- [ ] **Step 1: Write the failing unit test (Save)**

```csharp
// tests/SqlFerret.Core.Tests/EstimatedPlanServiceTests.cs
using SqlFerret.Core.Server;
using Xunit;

public class EstimatedPlanServiceTests
{
    [Fact]
    public void Save_writes_sqlplan_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var svc = new EstimatedPlanService(connectionString: "unused", plansFolder: dir);
        var path = svc.Save("abc123", "<ShowPlanXML/>");
        Assert.True(File.Exists(path));
        Assert.EndsWith("abc123.sqlplan", path);
        Assert.Equal("<ShowPlanXML/>", File.ReadAllText(path));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter EstimatedPlanServiceTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/SqlFerret.Core/Server/EstimatedPlanService.cs
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFerret.Core.Model;
using SqlFerret.Core.Replay;

namespace SqlFerret.Core.Server;

public class EstimatedPlanService
{
    private readonly string _connectionString;
    private readonly string _plansFolder;

    public EstimatedPlanService(string connectionString, string plansFolder)
    {
        _connectionString = connectionString;
        _plansFolder = plansFolder;
    }

    public string Save(string planId, string showplanXml)
    {
        Directory.CreateDirectory(_plansFolder);
        var path = Path.Combine(_plansFolder, $"{planId}.sqlplan");
        File.WriteAllText(path, showplanXml);
        return path;
    }

    public async Task<string> CaptureAsync(ExecutionEvent ev, string planId, CancellationToken ct = default)
    {
        ReplayScript script = ReplayBuilder.Build(ev);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (!string.IsNullOrWhiteSpace(ev.DatabaseName))
        {
            await using var use = conn.CreateCommand();
            use.CommandText = $"USE [{ev.DatabaseName!.Replace("]", "]]")}];";
            await use.ExecuteNonQueryAsync(ct);
        }

        await using (var on = conn.CreateCommand())
        { on.CommandText = "SET SHOWPLAN_XML ON;"; await on.ExecuteNonQueryAsync(ct); }

        var xml = new StringBuilder();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = script.Sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                xml.Append(reader.GetString(0));
        }

        return Save(planId, xml.ToString());
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter EstimatedPlanServiceTests`
Expected: PASS (the `Save` unit test; `CaptureAsync` has no test here and is exercised manually / in Plan 2).

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(core): estimated plan service (SHOWPLAN_XML)"
```

---

## Task 20: SqlFerret.Cli host

**Files:**
- Modify: `src/SqlFerret.Cli/Program.cs`
- Test: `tests/SqlFerret.Core.Tests/CliSmokeTests.cs`

**Interfaces:**
- Consumes: `XelSource`, `XelReader`, `DuckDbProject`, `IngestionService`, `IngestionOptions`, `WorkloadQueries`, `SqlFerretConfig`, `DotEnv`, `DisplayFormat`, `RedactionMode`.
- Produces: a console with two commands:
  - `import <path> --project <file.duckdb> [--redaction off|hash|masked|full]` → resolves files, reads via XELite, ingests, prints the `IngestionResult` counters.
  - `top-slow --project <file.duckdb> [--limit N]` → prints the top-slow grid using config `durationUnit`.
  - Loads `.env` (cwd) then `sqlferret.config.json` (cwd) at startup.

- [ ] **Step 1: Implement Program.cs**

```csharp
// src/SqlFerret.Cli/Program.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
var config = SqlFerretConfig.Load(Path.Combine(Directory.GetCurrentDirectory(), "sqlferret.config.json"));

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? "";
}

if (args.Length == 0) { Console.WriteLine("usage: import <path> --project f.duckdb | top-slow --project f.duckdb"); return 1; }

switch (args[0])
{
    case "import":
    {
        var path = args[1];
        var project = Arg("--project", "workload.duckdb");
        var redaction = Enum.Parse<RedactionMode>(
            Arg("--redaction", config.RedactionPolicy), ignoreCase: true);

        var (files, _) = XelSource.Resolve(path);
        using var db = DuckDbProject.Open(project);
        var svc = new IngestionService(db, new IngestionOptions(redaction, Array.Empty<FilterRule>()));
        var result = svc.Ingest(path, new XelReader().Read(files));
        Console.WriteLine($"run {result.RunId}: read={result.Read} mapped={result.Mapped} " +
                          $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures}");
        return 0;
    }
    case "top-slow":
    {
        var project = Arg("--project", "workload.duckdb");
        var limit = int.TryParse(Arg("--limit", "20"), out var l) ? l : 20;
        using var db = DuckDbProject.Open(project);
        var q = new WorkloadQueries(db.Connection);
        foreach (var s in q.TopSlow(limit, "total_duration_us", Array.Empty<FilterRule>()))
            Console.WriteLine($"{s.StatementKind,-7} {s.Count,8}  total={DisplayFormat.Duration(s.TotalDurationUs, config.DurationUnit),-12}  {Trim(s.NormalizedSql)}");
        return 0;
    }
    default:
        Console.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";
```

- [ ] **Step 2: Write a CLI smoke test (in-process ingest + query, no real .xel)**

```csharp
// tests/SqlFerret.Core.Tests/CliSmokeTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;
using Xunit;

public class CliSmokeTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    [Fact]
    public void End_to_end_ingest_then_query()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var svc = new IngestionService(db, new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));
            var ev = new FakeEvent("sql_batch_completed", new DateTime(2026,1,1),
                new Dictionary<string,object?>{["batch_text"]="SELECT 1", ["duration"]=10L},
                new Dictionary<string,object?>());
            svc.Ingest("logs/", new[] { ((IXeEventData)ev, "s_0.xel", 0L) });

            var top = new WorkloadQueries(db.Connection).TopSlow(10, "total_duration_us", Array.Empty<FilterRule>());
            Assert.Single(top);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 3: Run the full suite and build the CLI**

Run: `dotnet test`
Expected: PASS (all tests).
Run: `dotnet build src/SqlFerret.Cli`
Expected: builds with no errors.

- [ ] **Step 4: Manual end-to-end check (if a fixture exists)**

```bash
dotnet run --project src/SqlFerret.Cli -- import tests/SqlFerret.Core.Tests/Fixtures/sample_basic.xel --project /tmp/wl.duckdb
dotnet run --project src/SqlFerret.Cli -- top-slow --project /tmp/wl.duckdb --limit 10
```
Expected: import prints non-zero `mapped`; `top-slow` prints at least one row.

- [ ] **Step 5: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(cli): headless import + top-slow host"
```

---

## Self-Review (completed during planning)

**1. Spec coverage:**
- §3 architecture → Tasks 2–20 build every Core module; `Server/` → Task 19. (TUI host = Plan 2.) ✓
- §4 data model (4 tables, µs, object_name/is_system, redaction-before-write) → Tasks 11, 12, 13, 14. ✓
- §5 ingestion (streaming, folder `*.xel`, field selection, fallback, idempotency surrogate, isolation) → Tasks 10, 11, 14, 15. *(Per-file event-ordinal `file_offset` is a documented surrogate; true byte-offset resume is out of v1 — noted in Task 15.)* ✓
- §6 filtering (one model, ingest + view stages, ops incl. numeric, escaping) → Task 8; persistence → Task 18. *(TUI quick-add/chip-bar UX = Plan 2.)* ✓
- §7 analysis behind the views (Top Slow/Frequent, occurrences, session flow, param impact, dimensions, quality) → Task 16; replay/build-for-SSMS text → Task 9. *(View rendering = Plan 2.)* ✓
- §8 config (user `sqlferret.config.json` + app `sqlferret.ui.json`, `${ENV}`, `.env`) → Tasks 17, 18. ✓
- §9 estimated plans (SHOWPLAN_XML, `<id>.sqlplan`, save) → Task 19. ✓
- §10 error handling (counts, fallback, isolation, malformed-config tolerance) → Tasks 14, 17, 18. ✓
- §11 testing (golden normalizer, table-driven mapper/extractor, filter, integration on fixtures) → throughout; fixtures → Task 15. ✓

**2. Placeholder scan:** no TBD/TODO; every code step shows complete code. ✓

**3. Type consistency:** `IXeEventData`, `ExecutionEvent`, `NormalizedQuery`, `PreparedRow`, `FilterRule`, `IngestionResult`, `QueryStat` signatures are defined once (Tasks 2, 8, 11, 12, 14, 16) and consumed consistently. The `QueryStat` reader ordinal caveat is flagged inline for the implementer to verify against the SELECT list.

**Deferred to Plan 2 (TUI):** Terminal.Gui shell + all 9 views, chip bar, quick-add filters, Filter Builder, column chooser, drill-down panels, Build-for-SSMS clipboard + Get-estimated-plan wiring, Plans view + open-in-SSMS, session-boundary windowing UI.

---

## Out of scope for this plan (later cycles)

Avalonia host; structured replay execution; actual/post-execution plan capture; deadlock & blocked-process timeline + LLM; query-plan-profile LLM; ERRORLOG parsing; `query_rollups`; capture comparison.
