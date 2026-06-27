# Plan Obfuscation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an offline `.sqlplan` anonymizer that replaces object names with deterministic readable tokens, scrubs literals and parameter values, keeps the plan openable in SSMS, and persists a project-shared mapping plus a sidecar `map.json`.

**Architecture:** A pure headless core in `SqlFerret.Core.Obfuscation` (an `ObfuscationMap` token store, a `StatementTextRewriter` over ScriptDom tokens, and a `PlanObfuscator` that transforms the showplan via `XDocument`), persisted at project level in a DuckDB `obfuscation_map` table, and driven by a new `obfuscate-plan` CLI command exposing a project mode and a standalone mode.

**Tech Stack:** .NET 10 / C# 14, `System.Xml.Linq` (XDocument), Microsoft.SqlServer.TransactSql.ScriptDom (`TSql160Parser`), DuckDB.NET.Data, xUnit.

## Global Constraints

- Target framework `net10.0`, Nullable + ImplicitUsings on, LangVersion latest. 0 build warnings expected.
- KISS (spec §2): no repository/UoW, no `IXxx` interfaces, no DI, no AutoMapper/MediatR. Plain `record`/POCO, static utility classes, primary-constructor services only.
- Core stays headless and host-agnostic: `PlanObfuscator.Obfuscate` does no file or DB I/O.
- No regex on raw plan XML; structural transform via `XDocument` only.
- Safety invariant: no original sensitive string (mapped name or literal value) may appear in the obfuscated output.
- The `map.json` sidecar is a per-run export, never embedded in the shared `.sqlplan`; the DuckDB `obfuscation_map` table is the canonical project store.
- Migrations are idempotent (`CREATE TABLE IF NOT EXISTS`), following the existing `blocking_reports.raw_xml` pattern in `DuckDbProject.cs`.
- Tests are TDD: red, green, commit per change. Commits go on branch `feat/plan-obfuscation` (already created); never on `main`.

---

## File Structure

- Create `src/SqlFerret.Core/Obfuscation/ObfuscationMap.cs` — token store: per-kind deterministic assignment, bracket/case normalization, JSON (de)serialization, flat text lookup for the rewriter.
- Create `src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs` — rewrites a T-SQL fragment: map identifiers, literals to `?`, comments stripped, safety fallback on parse failure.
- Create `src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs` — XDocument orchestration: collect, rename attributes, rewrite text, scrub parameter values, whitelist.
- Create `src/SqlFerret.Core/Storage/DuckDbProject.Obfuscation.cs` — partial class: `obfuscation_map` schema, `LoadObfuscationMap`, `SaveObfuscationMap`.
- Modify `src/SqlFerret.Core/Storage/DuckDbProject.cs` — call `CreateObfuscationSchema(conn)` inside `CreateSchema`.
- Create `src/SqlFerret.Core/Obfuscation/ObfuscationRunner.cs` — one-call orchestration (standalone and project modes) shared by every host, mirroring `ImportRunner`. Owns the file + map I/O so the CLI and the future TUI stay thin.
- Modify `src/SqlFerret.Cli/Program.cs` — add the `obfuscate-plan` command (project and standalone modes) as a thin wrapper over `ObfuscationRunner`, update the usage string.
- Create tests under `tests/SqlFerret.Core.Tests/` — `ObfuscationMapTests.cs`, `StatementTextRewriterTests.cs`, `PlanObfuscatorTests.cs`, `ObfuscationStorageTests.cs`, `ObfuscationRunnerTests.cs`.

Note on test conventions: `CliSmokeTests.cs` in this repo exercises the Core directly (it does not spawn the CLI process; there is no `RunCli` harness). Following that convention, the orchestration is tested through `ObfuscationRunner` (Task 7), and the thin CLI wiring (Task 8) is verified with a real `dotnet run` invocation, the same way `CLAUDE.md` documents end-to-end CLI checks.

---

### Task 1: ObfuscationMap token store

**Files:**
- Create: `src/SqlFerret.Core/Obfuscation/ObfuscationMap.cs`
- Test: `tests/SqlFerret.Core.Tests/ObfuscationMapTests.cs`

**Interfaces:**
- Produces:
  - `enum NameKind { Database, Schema, Table, Column, Index, Statistics, Parameter, Alias }`
  - `string ObfuscationMap.Token(NameKind kind, string originalName)` — returns existing or newly assigned token; idempotent per (kind, normalized name).
  - `static string ObfuscationMap.Strip(string name)` — trims and removes surrounding `[` `]`.
  - `IReadOnlyDictionary<string,string> ObfuscationMap.BuildTextLookup()` — lowercased original name to token, across all kinds, with kind precedence Table, Alias, Column, Index, Statistics, Schema, Database, Parameter (first kind to claim a key wins).
  - `IEnumerable<(NameKind kind, string original, string token)> ObfuscationMap.Entries()`
  - `string ObfuscationMap.ToJson()` and `static ObfuscationMap ObfuscationMap.FromJson(string json)`
  - `static ObfuscationMap ObfuscationMap.FromEntries(IEnumerable<(NameKind kind, string original, string token)> entries)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ObfuscationMapTests.cs
using SqlFerret.Core.Obfuscation;
using Xunit;

public class ObfuscationMapTests
{
    [Fact]
    public void Token_is_deterministic_and_per_kind_sequential()
    {
        var m = new ObfuscationMap();
        Assert.Equal("Table1", m.Token(NameKind.Table, "[Customers]"));
        Assert.Equal("Table2", m.Token(NameKind.Table, "Orders"));
        Assert.Equal("Table1", m.Token(NameKind.Table, "customers")); // case-insensitive, bracket-insensitive, reused
        Assert.Equal("Col1", m.Token(NameKind.Column, "SSN"));
    }

    [Fact]
    public void Json_roundtrip_preserves_entries()
    {
        var m = new ObfuscationMap();
        m.Token(NameKind.Table, "Customers");
        m.Token(NameKind.Column, "SSN");
        var back = ObfuscationMap.FromJson(m.ToJson());
        Assert.Equal("Table1", back.Token(NameKind.Table, "Customers"));
        Assert.Equal("Col1", back.Token(NameKind.Column, "ssn"));
    }

    [Fact]
    public void FromEntries_continues_numbering_without_collision()
    {
        var m = ObfuscationMap.FromEntries(new[] { (NameKind.Table, "Customers", "Table1") });
        Assert.Equal("Table1", m.Token(NameKind.Table, "Customers"));
        Assert.Equal("Table2", m.Token(NameKind.Table, "Orders")); // next free number, no clash
    }

    [Fact]
    public void TextLookup_resolves_table_over_column_on_collision()
    {
        var m = new ObfuscationMap();
        m.Token(NameKind.Column, "Name");
        m.Token(NameKind.Table, "Name");
        Assert.Equal("Table2", m.BuildTextLookup()["name"]); // Table precedence wins
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ObfuscationMapTests`
Expected: FAIL with compile error (type `ObfuscationMap` / `NameKind` not found).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Obfuscation/ObfuscationMap.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFerret.Core.Obfuscation;

public enum NameKind { Database, Schema, Table, Column, Index, Statistics, Parameter, Alias }

public sealed class ObfuscationMap
{
    private static readonly Dictionary<NameKind, string> Prefixes = new()
    {
        [NameKind.Database] = "Db", [NameKind.Schema] = "Schema", [NameKind.Table] = "Table",
        [NameKind.Column] = "Col", [NameKind.Index] = "Idx", [NameKind.Statistics] = "Stat",
        [NameKind.Parameter] = "Param", [NameKind.Alias] = "Alias",
    };

    // Precedence for the flat text lookup when one name exists under several kinds.
    private static readonly NameKind[] TextPrecedence =
        [NameKind.Table, NameKind.Alias, NameKind.Column, NameKind.Index,
         NameKind.Statistics, NameKind.Schema, NameKind.Database, NameKind.Parameter];

    // kind -> (lowercased stripped key -> (original stripped, token))
    private readonly Dictionary<NameKind, Dictionary<string, (string Original, string Token)>> _maps = new();
    private readonly Dictionary<NameKind, int> _counters = new();

    public static string Strip(string name) => name.Trim().Trim('[', ']');

    public string Token(NameKind kind, string originalName)
    {
        var stripped = Strip(originalName);
        var key = stripped.ToLowerInvariant();
        var m = _maps.TryGetValue(kind, out var existing) ? existing : _maps[kind] = new();
        if (m.TryGetValue(key, out var hit)) return hit.Token;
        var n = (_counters.TryGetValue(kind, out var c) ? c : 0) + 1;
        _counters[kind] = n;
        var token = Prefixes[kind] + n;
        m[key] = (stripped, token);
        return token;
    }

    public IReadOnlyDictionary<string, string> BuildTextLookup()
    {
        var lookup = new Dictionary<string, string>();
        foreach (var kind in TextPrecedence)
            if (_maps.TryGetValue(kind, out var m))
                foreach (var (key, val) in m)
                    lookup.TryAdd(key, val.Token); // first (higher-precedence) kind wins
        return lookup;
    }

    public IEnumerable<(NameKind Kind, string Original, string Token)> Entries()
    {
        foreach (var (kind, m) in _maps)
            foreach (var (_, val) in m)
                yield return (kind, val.Original, val.Token);
    }

    public string ToJson()
    {
        var root = new JsonObject();
        foreach (var (kind, m) in _maps)
        {
            var section = new JsonObject();
            foreach (var (_, val) in m)
                section[val.Original] = val.Token;
            root[kind.ToString()] = section;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static ObfuscationMap FromJson(string json)
    {
        var map = new ObfuscationMap();
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var (kindName, section) in root)
        {
            var kind = Enum.Parse<NameKind>(kindName, ignoreCase: true);
            foreach (var (original, tokenNode) in section!.AsObject())
                map.Seed(kind, original, tokenNode!.GetValue<string>());
        }
        return map;
    }

    public static ObfuscationMap FromEntries(IEnumerable<(NameKind Kind, string Original, string Token)> entries)
    {
        var map = new ObfuscationMap();
        foreach (var (kind, original, token) in entries)
            map.Seed(kind, original, token);
        return map;
    }

    // Insert a known pair and keep the per-kind counter ahead of any numeric suffix seen.
    private void Seed(NameKind kind, string original, string token)
    {
        var stripped = Strip(original);
        var m = _maps.TryGetValue(kind, out var existing) ? existing : _maps[kind] = new();
        m[stripped.ToLowerInvariant()] = (stripped, token);
        var prefix = Prefixes[kind];
        if (token.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(token.AsSpan(prefix.Length), out var n))
            _counters[kind] = Math.Max(_counters.TryGetValue(kind, out var c) ? c : 0, n);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ObfuscationMapTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
rtk git add src/SqlFerret.Core/Obfuscation/ObfuscationMap.cs tests/SqlFerret.Core.Tests/ObfuscationMapTests.cs
rtk git commit -m "feat(obfuscate): ObfuscationMap deterministic token store"
```

---

### Task 2: StatementTextRewriter happy path

**Files:**
- Create: `src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs`
- Test: `tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs`

**Interfaces:**
- Consumes: `ObfuscationMap.BuildTextLookup()`, `ObfuscationMap.Strip(string)`, `NameKind` (Task 1).
- Produces: `static string StatementTextRewriter.Rewrite(string sqlFragment, ObfuscationMap map)` — returns the fragment with mapped identifiers substituted, literals replaced by `?`, comments removed, whitespace and keyword casing preserved.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs
using SqlFerret.Core.Obfuscation;
using Xunit;

public class StatementTextRewriterTests
{
    private static ObfuscationMap MapWith(params (NameKind, string)[] names)
    {
        var m = new ObfuscationMap();
        foreach (var (k, n) in names) m.Token(k, n);
        return m;
    }

    [Fact]
    public void Maps_identifiers_and_scrubs_literals()
    {
        var m = MapWith((NameKind.Table, "Customers"), (NameKind.Column, "SSN"));
        var outSql = StatementTextRewriter.Rewrite("SELECT SSN FROM Customers WHERE SSN = '123-45-6789'", m);
        Assert.Contains("Col1", outSql);
        Assert.Contains("Table1", outSql);
        Assert.Contains("?", outSql);
        Assert.DoesNotContain("Customers", outSql);
        Assert.DoesNotContain("123-45-6789", outSql);
    }

    [Fact]
    public void Preserves_keywords_and_unmapped_identifiers()
    {
        var m = MapWith((NameKind.Table, "Customers"));
        var outSql = StatementTextRewriter.Rewrite("SELECT getdate() FROM Customers", m);
        Assert.Contains("getdate", outSql);   // built-in preserved (not in map)
        Assert.Contains("SELECT", outSql);     // keyword casing preserved
    }

    [Fact]
    public void Strips_comments_that_might_leak()
    {
        var m = MapWith((NameKind.Table, "Customers"));
        var outSql = StatementTextRewriter.Rewrite("SELECT 1 FROM Customers /* ssn 123-45-6789 */", m);
        Assert.DoesNotContain("123-45-6789", outSql);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter StatementTextRewriterTests`
Expected: FAIL with compile error (`StatementTextRewriter` not found).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Obfuscation;

public static class StatementTextRewriter
{
    private static readonly HashSet<TSqlTokenType> Literals =
    [
        TSqlTokenType.Integer, TSqlTokenType.Numeric, TSqlTokenType.Money,
        TSqlTokenType.Real, TSqlTokenType.HexLiteral,
        TSqlTokenType.AsciiStringLiteral, TSqlTokenType.UnicodeStringLiteral,
    ];

    public static string Rewrite(string sqlFragment, ObfuscationMap map)
    {
        if (string.IsNullOrWhiteSpace(sqlFragment)) return sqlFragment ?? string.Empty;
        var lookup = map.BuildTextLookup();
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using (var pr = new StringReader(sqlFragment))
            {
                parser.Parse(pr, out IList<ParseError> perr);
                if (perr.Count > 0) return Fallback(sqlFragment, lookup);
            }
            using var r = new StringReader(sqlFragment);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(r, out IList<ParseError> err);
            if (err.Count > 0) return Fallback(sqlFragment, lookup);

            var sb = new StringBuilder();
            foreach (var t in tokens)
            {
                switch (t.TokenType)
                {
                    case TSqlTokenType.EndOfFile:
                        continue;
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                        sb.Append(' '); // drop comment bodies (may carry literals)
                        continue;
                }
                if (Literals.Contains(t.TokenType)) { sb.Append('?'); continue; }
                if (t.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
                {
                    var key = ObfuscationMap.Strip(t.Text).ToLowerInvariant();
                    if (lookup.TryGetValue(key, out var tok))
                    {
                        sb.Append(t.TokenType == TSqlTokenType.QuotedIdentifier ? "[" + tok + "]" : tok);
                        continue;
                    }
                }
                sb.Append(t.Text);
            }
            return sb.ToString();
        }
        catch
        {
            return Fallback(sqlFragment, lookup);
        }
    }

    // Safety net: never let an original name or literal escape, even if parsing failed.
    private static string Fallback(string raw, IReadOnlyDictionary<string, string> lookup)
    {
        var s = Regex.Replace(raw, @"'(?:[^']|'')*'", "?");                  // string literals
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);     // block comments
        s = Regex.Replace(s, @"--[^\n]*", " ");                              // line comments
        s = Regex.Replace(s, @"(?<![A-Za-z_@#$0-9.])\d+(\.\d+)?", "?");       // numeric literals
        foreach (var kv in lookup.OrderByDescending(kv => kv.Key.Length))     // longest-first to avoid substrings
            s = Regex.Replace(s, @"(?i)\[?\b" + Regex.Escape(kv.Key) + @"\b\]?", kv.Value);
        return s;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter StatementTextRewriterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
rtk git add src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs
rtk git commit -m "feat(obfuscate): StatementTextRewriter maps identifiers, scrubs literals"
```

---

### Task 3: StatementTextRewriter safety fallback

**Files:**
- Modify: `tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs`

**Interfaces:**
- Consumes: `StatementTextRewriter.Rewrite` (Task 2). No new production API; this task proves the fallback path that already exists guarantees the safety invariant on unparsable input.

- [ ] **Step 1: Write the failing test**

```csharp
// append to tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs
    [Fact]
    public void Fallback_on_unparsable_sql_still_scrubs_names_and_literals()
    {
        var m = MapWith((NameKind.Table, "Customers"));
        // Deliberately broken so ScriptDom.Parse reports errors and the fallback runs.
        var outSql = StatementTextRewriter.Rewrite("@@@ not sql (( Customers 'secret' 42", m);
        Assert.DoesNotContain("Customers", outSql);
        Assert.DoesNotContain("secret", outSql);
        Assert.DoesNotContain("42", outSql);
        Assert.Contains("Table1", outSql);
    }
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test --filter StatementTextRewriterTests`
Expected: PASS if the Task 2 fallback is correct. If it FAILS (an original token survives), fix `Fallback` in `StatementTextRewriter.cs` until the assertions hold; do not weaken the test.

- [ ] **Step 3: Commit**

```bash
rtk git add tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs
rtk git commit -m "test(obfuscate): rewriter fallback upholds safety invariant on bad SQL"
```

---

### Task 4: PlanObfuscator attribute renaming and whitelist

**Files:**
- Create: `src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs`
- Test: `tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs`

**Interfaces:**
- Consumes: `ObfuscationMap`, `NameKind`, `StatementTextRewriter.Rewrite` (Tasks 1, 2).
- Produces: `static (string AnonXml, ObfuscationMap Map) PlanObfuscator.Obfuscate(string showplanXml, ObfuscationMap map)`.

Showplan elements live in namespace `http://schemas.microsoft.com/sqlserver/2004/07/showplan`; compare on `Name.LocalName` so the namespace is irrelevant. Object/ColumnReference attributes (`Database`, `Schema`, `Table`, `Index`, `Statistics`, `Alias`, `Column`) are unqualified.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs
using SqlFerret.Core.Obfuscation;
using Xunit;

public class PlanObfuscatorTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // Minimal but well-formed showplan-shaped fragment exercising object + column + system + worktable.
    private static string Plan() => $"""
    <ShowPlanXML xmlns="{Ns}">
      <RelOp>
        <IndexScan>
          <Object Database="[Sales]" Schema="[dbo]" Table="[Customers]" Index="[PK_Customers]" />
          <Object Database="[Sales]" Schema="[sys]" Table="[indexes]" />
          <Object Table="[Worktable]" />
          <ColumnReference Database="[Sales]" Schema="[dbo]" Table="[Customers]" Column="SSN" />
        </IndexScan>
      </RelOp>
    </ShowPlanXML>
    """;

    [Fact]
    public void Renames_objects_and_columns_to_tokens()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(Plan(), new ObfuscationMap());
        Assert.DoesNotContain("Customers", xml);
        Assert.DoesNotContain("PK_Customers", xml);
        Assert.DoesNotContain("SSN", xml);
        Assert.Contains("Table1", xml);
        Assert.Contains("Col1", xml);
        Assert.Contains("Idx1", xml);
    }

    [Fact]
    public void Leaves_system_objects_and_worktables_intact()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(Plan(), new ObfuscationMap());
        Assert.Contains("[sys]", xml);
        Assert.Contains("indexes", xml);     // system table name preserved
        Assert.Contains("Worktable", xml);   // internal object preserved
    }

    [Fact]
    public void Output_is_well_formed_xml()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(Plan(), new ObfuscationMap());
        var ex = Record.Exception(() => System.Xml.Linq.XDocument.Parse(xml));
        Assert.Null(ex);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter PlanObfuscatorTests`
Expected: FAIL with compile error (`PlanObfuscator` not found).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs
using System.Xml.Linq;

namespace SqlFerret.Core.Obfuscation;

public static class PlanObfuscator
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "tempdb" };
    private static readonly HashSet<string> InternalTables = new(StringComparer.OrdinalIgnoreCase) { "Worktable", "Workfile" };

    public static (string AnonXml, ObfuscationMap Map) Obfuscate(string showplanXml, ObfuscationMap map)
    {
        var doc = XDocument.Parse(showplanXml, LoadOptions.PreserveWhitespace);

        // Pass 1: collect + rename names on Object and ColumnReference nodes.
        foreach (var el in doc.Descendants())
            if (el.Name.LocalName is "Object" or "ColumnReference")
                RenameNode(el, map);

        // Pass 2: rewrite embedded T-SQL and scrub parameter values (map is now complete).
        foreach (var attr in doc.Descendants().Attributes())
        {
            switch (attr.Name.LocalName)
            {
                case "StatementText":
                case "ScalarString":
                    attr.Value = StatementTextRewriter.Rewrite(attr.Value, map);
                    break;
                case "ParameterCompiledValue":
                case "ParameterRuntimeValue":
                    attr.Value = "?";
                    break;
            }
        }

        return (doc.ToString(SaveOptions.DisableFormatting), map);
    }

    private static void RenameNode(XElement el, ObfuscationMap map)
    {
        var db = Val(el, "Database");
        var schema = Val(el, "Schema");
        var table = Val(el, "Table");
        if ((schema is not null && SystemSchemas.Contains(ObfuscationMap.Strip(schema)))
            || (db is not null && SystemDatabases.Contains(ObfuscationMap.Strip(db)))
            || (table is not null && InternalTables.Contains(ObfuscationMap.Strip(table))))
            return; // whitelisted: neither the object nor its columns are mapped

        Set(el, "Database", NameKind.Database, map);
        Set(el, "Schema", NameKind.Schema, map);
        Set(el, "Table", NameKind.Table, map);
        Set(el, "Index", NameKind.Index, map);
        Set(el, "Statistics", NameKind.Statistics, map);
        Set(el, "Alias", NameKind.Alias, map);

        var col = el.Attribute("Column");
        if (col is not null && !string.IsNullOrEmpty(col.Value))
        {
            // Parameter references in <ParameterList> read as Column="@P1".
            var kind = col.Value.TrimStart('[').StartsWith('@') ? NameKind.Parameter : NameKind.Column;
            Rename(col, kind, map);
        }
    }

    private static string? Val(XElement el, string name) => el.Attribute(name)?.Value;

    private static void Set(XElement el, string attrName, NameKind kind, ObfuscationMap map)
    {
        var a = el.Attribute(attrName);
        if (a is not null && !string.IsNullOrEmpty(a.Value)) Rename(a, kind, map);
    }

    private static void Rename(XAttribute a, NameKind kind, ObfuscationMap map)
    {
        var hadBrackets = a.Value.StartsWith('[');
        var token = map.Token(kind, a.Value);
        a.Value = hadBrackets ? "[" + token + "]" : token;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter PlanObfuscatorTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
rtk git add src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs
rtk git commit -m "feat(obfuscate): PlanObfuscator renames objects, honours system whitelist"
```

---

### Task 5: PlanObfuscator text/value scrubbing, safety, idempotency

**Files:**
- Modify: `tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs`

**Interfaces:**
- Consumes: `PlanObfuscator.Obfuscate` (Task 4). No new production API; this task proves StatementText rewriting, parameter scrubbing, the safety invariant, and idempotency on a richer fixture. If an assertion fails, fix `PlanObfuscator.cs`/`StatementTextRewriter.cs` rather than the test.

- [ ] **Step 1: Write the failing test**

```csharp
// append to tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs
    private static string RichPlan() => $"""
    <ShowPlanXML xmlns="{Ns}">
      <StmtSimple StatementText="SELECT SSN FROM Customers WHERE SSN = '123-45-6789'">
        <QueryPlan>
          <RelOp>
            <Filter>
              <Predicate>
                <ScalarOperator ScalarString="[Customers].[SSN]='123-45-6789'" />
              </Predicate>
              <Object Database="[Sales]" Schema="[dbo]" Table="[Customers]" />
              <ColumnReference Database="[Sales]" Schema="[dbo]" Table="[Customers]" Column="SSN" />
            </Filter>
          </RelOp>
          <ParameterList>
            <ColumnReference Column="@P1"
              ParameterCompiledValue="'123-45-6789'" ParameterRuntimeValue="'123-45-6789'" />
          </ParameterList>
        </QueryPlan>
      </StmtSimple>
    </ShowPlanXML>
    """;

    [Fact]
    public void Scrubs_statement_text_predicates_and_parameter_values()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(RichPlan(), new ObfuscationMap());
        Assert.DoesNotContain("123-45-6789", xml);  // literal gone everywhere
        Assert.DoesNotContain("Customers", xml);     // name gone in text + attributes
        Assert.DoesNotContain("SSN", xml);
        Assert.Contains("Table1", xml);
        Assert.Contains("Col1", xml);
        Assert.Contains("Param1", xml);
    }

    [Fact]
    public void Obfuscation_is_idempotent()
    {
        var (once, _) = PlanObfuscator.Obfuscate(RichPlan(), new ObfuscationMap());
        var (twice, _) = PlanObfuscator.Obfuscate(once, new ObfuscationMap());
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Shared_map_gives_same_token_across_plans()
    {
        var map = new ObfuscationMap();
        var (a, _) = PlanObfuscator.Obfuscate(RichPlan(), map);
        var planB = RichPlan().Replace("@P1", "@P9"); // same Customers/SSN, different param
        var (b, _) = PlanObfuscator.Obfuscate(planB, map);
        Assert.Contains("Table1", a);
        Assert.Contains("Table1", b); // Customers -> Table1 in both via the shared map
    }
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test --filter PlanObfuscatorTests`
Expected: PASS. If `Obfuscation_is_idempotent` fails because re-parsing the token `Param1` differs, inspect the rewriter output; tokens are plain identifiers and must round-trip unchanged. Fix production code, not the test.

- [ ] **Step 3: Commit**

```bash
rtk git add tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs
rtk git commit -m "test(obfuscate): scrubbing, safety invariant, idempotency, shared-map consistency"
```

---

### Task 6: DuckDB obfuscation_map persistence

**Files:**
- Create: `src/SqlFerret.Core/Storage/DuckDbProject.Obfuscation.cs`
- Modify: `src/SqlFerret.Core/Storage/DuckDbProject.cs:67` (inside `CreateSchema`, after `CreateQdsSchema(conn);` add `CreateObfuscationSchema(conn);`)
- Test: `tests/SqlFerret.Core.Tests/ObfuscationStorageTests.cs`

**Interfaces:**
- Consumes: `ObfuscationMap.Entries()`, `ObfuscationMap.FromEntries(...)`, `NameKind` (Task 1); the private `static void Add(IDbCommand, string, object?)` helper in `DuckDbProject.cs`.
- Produces: `ObfuscationMap DuckDbProject.LoadObfuscationMap()`, `void DuckDbProject.SaveObfuscationMap(ObfuscationMap map)`, `internal static void DuckDbProject.CreateObfuscationSchema(DuckDBConnection conn)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ObfuscationStorageTests.cs
using SqlFerret.Core.Obfuscation;
using SqlFerret.Core.Storage;
using Xunit;

public class ObfuscationStorageTests
{
    static string NewDb() => Path.Combine(Path.GetTempPath(), $"obf_{Guid.NewGuid():N}.duckdb");

    [Fact]
    public void Save_then_load_roundtrips_and_inserts_only_new_entries()
    {
        var path = NewDb();
        try
        {
            using (var p = DuckDbProject.Open(path))
            {
                var m = new ObfuscationMap();
                m.Token(NameKind.Table, "Customers");
                m.Token(NameKind.Column, "SSN");
                p.SaveObfuscationMap(m);
                p.SaveObfuscationMap(m); // idempotent: ON CONFLICT DO NOTHING

                using var c = p.Connection.CreateCommand();
                c.CommandText = "SELECT COUNT(*) FROM obfuscation_map";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
            using (var p = DuckDbProject.Open(path))
            {
                var loaded = p.LoadObfuscationMap();
                Assert.Equal("Table1", loaded.Token(NameKind.Table, "Customers")); // reused, not re-numbered
                Assert.Equal("Table2", loaded.Token(NameKind.Table, "Orders"));    // continues numbering
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ObfuscationStorageTests`
Expected: FAIL with compile error (`SaveObfuscationMap` not found).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Storage/DuckDbProject.Obfuscation.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Obfuscation;

namespace SqlFerret.Core.Storage;

public sealed partial class DuckDbProject
{
    internal static void CreateObfuscationSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS obfuscation_map (
          kind          VARCHAR NOT NULL,
          original_name VARCHAR NOT NULL,
          token         VARCHAR NOT NULL,
          PRIMARY KEY (kind, original_name)
        );
        """;
        cmd.ExecuteNonQuery();
    }

    public ObfuscationMap LoadObfuscationMap()
    {
        var entries = new List<(NameKind, string, string)>();
        using var c = Connection.CreateCommand();
        c.CommandText = "SELECT kind, original_name, token FROM obfuscation_map";
        using var r = c.ExecuteReader();
        while (r.Read())
            entries.Add((Enum.Parse<NameKind>(r.GetString(0), ignoreCase: true), r.GetString(1), r.GetString(2)));
        return ObfuscationMap.FromEntries(entries);
    }

    public void SaveObfuscationMap(ObfuscationMap map)
    {
        foreach (var (kind, original, token) in map.Entries())
        {
            using var c = Connection.CreateCommand();
            c.CommandText = "INSERT INTO obfuscation_map(kind, original_name, token) VALUES ($k,$o,$t) ON CONFLICT DO NOTHING";
            Add(c, "$k", kind.ToString().ToLowerInvariant());
            Add(c, "$o", original);
            Add(c, "$t", token);
            c.ExecuteNonQuery();
        }
    }
}
```

Then wire the schema call in `src/SqlFerret.Core/Storage/DuckDbProject.cs` (the line currently reads `CreateQdsSchema(conn);` near line 67):

```csharp
        CreateQdsSchema(conn);
        CreateObfuscationSchema(conn);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ObfuscationStorageTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
rtk git add src/SqlFerret.Core/Storage/DuckDbProject.Obfuscation.cs src/SqlFerret.Core/Storage/DuckDbProject.cs tests/SqlFerret.Core.Tests/ObfuscationStorageTests.cs
rtk git commit -m "feat(obfuscate): project-level obfuscation_map persistence in DuckDB"
```

---

### Task 7: ObfuscationRunner (Core orchestration, both modes)

**Files:**
- Create: `src/SqlFerret.Core/Obfuscation/ObfuscationRunner.cs`
- Test: `tests/SqlFerret.Core.Tests/ObfuscationRunnerTests.cs`

**Interfaces:**
- Consumes: `PlanObfuscator.Obfuscate`, `ObfuscationMap` (Tasks 1, 4); `DuckDbProject.LoadObfuscationMap()`, `DuckDbProject.SaveObfuscationMap(...)` (Task 6).
- Produces:
  - `readonly record struct ObfuscationResult(string AnonPath, string MapPath, int NamesMapped)`
  - `static ObfuscationResult ObfuscationRunner.RunStandalone(string inPath, string outPath)` — reads `inPath`, obfuscates with a fresh map, writes `outPath` and a sibling `map.json` (the `.sqlplan` suffix of `outPath` is swapped for `.map.json`, else `.map.json` is appended).
  - `static ObfuscationResult ObfuscationRunner.RunProject(DuckDbProject db, string plansFolder, string planId)` — loads the shared map, reads `plansFolder/<planId>.sqlplan`, obfuscates, writes `<planId>.anon.sqlplan` and `<planId>.map.json`, persists the enriched map. Throws `FileNotFoundException` for a missing source plan (hosts handle it, like `ImportRunner`).
  - `static string ObfuscationRunner.MapJsonPath(string outPath)` (internal helper, exposed for the CLI).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SqlFerret.Core.Tests/ObfuscationRunnerTests.cs
using SqlFerret.Core.Obfuscation;
using SqlFerret.Core.Storage;
using Xunit;

public class ObfuscationRunnerTests
{
    private static string Plan(string tbl) =>
        "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\">" +
        $"<RelOp><Object Database=\"[Sales]\" Schema=\"[dbo]\" Table=\"[{tbl}]\" /></RelOp></ShowPlanXML>";

    [Fact]
    public void RunStandalone_writes_anon_plan_and_sibling_map()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obfrun_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var inPath = Path.Combine(dir, "p.sqlplan");
            var outPath = Path.Combine(dir, "p.anon.sqlplan");
            File.WriteAllText(inPath, Plan("Customers"));

            var result = ObfuscationRunner.RunStandalone(inPath, outPath);

            Assert.True(File.Exists(outPath));
            Assert.True(File.Exists(Path.Combine(dir, "p.anon.map.json")));
            Assert.Equal(Path.Combine(dir, "p.anon.map.json"), result.MapPath);
            var anon = File.ReadAllText(outPath);
            Assert.DoesNotContain("Customers", anon);
            Assert.Contains("Table1", anon);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunProject_shares_tokens_across_plans_via_persisted_map()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obfrunp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "wl.duckdb");
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.sqlplan"), Plan("Customers"));
            File.WriteAllText(Path.Combine(dir, "b.sqlplan"), Plan("Customers"));

            using (var db = DuckDbProject.Open(dbPath))
            {
                ObfuscationRunner.RunProject(db, dir, "a");
                ObfuscationRunner.RunProject(db, dir, "b");
            }

            var a = File.ReadAllText(Path.Combine(dir, "a.anon.sqlplan"));
            var b = File.ReadAllText(Path.Combine(dir, "b.anon.sqlplan"));
            Assert.Contains("Table1", a);
            Assert.Contains("Table1", b); // Customers -> Table1 in both via the shared, persisted map
            Assert.True(File.Exists(Path.Combine(dir, "a.map.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunProject_throws_for_missing_plan()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obfmiss_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var db = DuckDbProject.Open(Path.Combine(dir, "wl.duckdb"));
            Assert.Throws<FileNotFoundException>(() => ObfuscationRunner.RunProject(db, dir, "nope"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ObfuscationRunnerTests`
Expected: FAIL with compile error (`ObfuscationRunner` not found).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SqlFerret.Core/Obfuscation/ObfuscationRunner.cs
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Obfuscation;

/// <summary>
/// One-call obfuscation orchestration shared by every host (CLI now, TUI later):
/// owns the file and map I/O so hosts stay thin. Mirrors <see cref="Ingestion.ImportRunner"/>.
/// </summary>
public readonly record struct ObfuscationResult(string AnonPath, string MapPath, int NamesMapped);

public static class ObfuscationRunner
{
    public static ObfuscationResult RunStandalone(string inPath, string outPath)
    {
        if (!File.Exists(inPath)) throw new FileNotFoundException("input plan not found", inPath);
        var (anon, map) = PlanObfuscator.Obfuscate(File.ReadAllText(inPath), new ObfuscationMap());
        File.WriteAllText(outPath, anon);
        var mapPath = MapJsonPath(outPath);
        File.WriteAllText(mapPath, map.ToJson());
        return new ObfuscationResult(outPath, mapPath, map.Entries().Count());
    }

    public static ObfuscationResult RunProject(DuckDbProject db, string plansFolder, string planId)
    {
        var src = Path.Combine(plansFolder, $"{planId}.sqlplan");
        if (!File.Exists(src)) throw new FileNotFoundException("plan not found", src);

        var map = db.LoadObfuscationMap();
        var (anon, enriched) = PlanObfuscator.Obfuscate(File.ReadAllText(src), map);
        Directory.CreateDirectory(plansFolder);
        var anonPath = Path.Combine(plansFolder, $"{planId}.anon.sqlplan");
        var mapPath = Path.Combine(plansFolder, $"{planId}.map.json");
        File.WriteAllText(anonPath, anon);
        File.WriteAllText(mapPath, enriched.ToJson());
        db.SaveObfuscationMap(enriched);
        return new ObfuscationResult(anonPath, mapPath, enriched.Entries().Count());
    }

    internal static string MapJsonPath(string outPath) =>
        outPath.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase)
            ? outPath[..^".sqlplan".Length] + ".map.json"
            : outPath + ".map.json";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ObfuscationRunnerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
rtk git add src/SqlFerret.Core/Obfuscation/ObfuscationRunner.cs tests/SqlFerret.Core.Tests/ObfuscationRunnerTests.cs
rtk git commit -m "feat(obfuscate): ObfuscationRunner orchestration for both modes"
```

---

### Task 8: CLI obfuscate-plan wiring (both modes)

**Files:**
- Modify: `src/SqlFerret.Cli/Program.cs` (add `case "obfuscate-plan":`, update the usage string)

**Interfaces:**
- Consumes: the existing `string Arg(string, string?)` and `OpenProject()` locals and `project.OpenDb()`/`project.PlansFolder`; `ObfuscationRunner.RunStandalone/RunProject` (Task 7).
- Produces: the `obfuscate-plan` command. Standalone: `--in <file> --out <file>`. Project: `--project <dir> --plan-id <bare-id>`.

This task is thin glue over the already-tested runner. Per repo convention (no CLI process-test harness exists), it is verified with a real `dotnet run` invocation in Step 3 rather than an xUnit test.

- [ ] **Step 1: Add the command**

Add this `case` to the `switch (args[0])` block in `src/SqlFerret.Cli/Program.cs`:

```csharp
    case "obfuscate-plan":
        {
            var inPath = Arg("--in");
            if (!string.IsNullOrWhiteSpace(inPath))
            {
                var outPath = Arg("--out");
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    Console.Error.WriteLine("obfuscate-plan: --out <file> is required with --in");
                    return 1;
                }
                if (!File.Exists(inPath))
                {
                    Console.Error.WriteLine($"obfuscate-plan: input not found: {inPath}");
                    return 1;
                }
                var r = SqlFerret.Core.Obfuscation.ObfuscationRunner.RunStandalone(inPath, outPath);
                Console.WriteLine($"obfuscated -> {r.AnonPath} ({r.NamesMapped} names, map: {r.MapPath})");
                return 0;
            }

            var planId = Arg("--plan-id");
            if (string.IsNullOrWhiteSpace(planId)
                || planId.Contains('/') || planId.Contains('\\') || planId.Contains("..")
                || Path.GetFileName(planId) != planId)
            {
                Console.Error.WriteLine("obfuscate-plan: provide --in <file> --out <file>, or --project <dir> --plan-id <bare-id>");
                return 1;
            }
            var project = OpenProject();
            if (project is null) return 1;

            var srcPlan = Path.Combine(project.PlansFolder, $"{planId}.sqlplan");
            if (!File.Exists(srcPlan))
            {
                Console.Error.WriteLine($"obfuscate-plan: plan not found: {srcPlan}");
                return 1;
            }

            using (var db = project.OpenDb())
            {
                var r = SqlFerret.Core.Obfuscation.ObfuscationRunner.RunProject(db, project.PlansFolder, planId);
                Console.WriteLine($"obfuscated -> {r.AnonPath} ({r.NamesMapped} names, map: {r.MapPath})");
            }
            return 0;
        }
```

Update the usage string in the `if (args.Length == 0)` block to append:
`| obfuscate-plan (--in <file> --out <file> | --project <dir> --plan-id <id>)`.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Verify both modes end-to-end with dotnet run**

```bash
# standalone
cat > /tmp/p.sqlplan <<'XML'
<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
<RelOp><Object Database="[Sales]" Schema="[dbo]" Table="[Customers]" /></RelOp></ShowPlanXML>
XML
dotnet run --project src/SqlFerret.Cli -- obfuscate-plan --in /tmp/p.sqlplan --out /tmp/p.anon.sqlplan
grep -q Table1 /tmp/p.anon.sqlplan && ! grep -q Customers /tmp/p.anon.sqlplan && test -f /tmp/p.anon.map.json && echo "STANDALONE OK"

# project
mkdir -p /tmp/wl/plans
cp /tmp/p.sqlplan /tmp/wl/plans/abc.sqlplan
dotnet run --project src/SqlFerret.Cli -- obfuscate-plan --project /tmp/wl --plan-id abc
grep -q Table1 /tmp/wl/plans/abc.anon.sqlplan && test -f /tmp/wl/plans/abc.map.json && echo "PROJECT OK"
```

Expected: both lines print `STANDALONE OK` and `PROJECT OK`.

- [ ] **Step 4: Run the full suite and format**

Run: `dotnet format && dotnet build && dotnet test`
Expected: build 0 warnings; all tests pass (1 pre-existing skipped env-gated live-SQL test).

- [ ] **Step 5: Commit**

```bash
rtk git add src/SqlFerret.Cli/Program.cs
rtk git commit -m "feat(cli): obfuscate-plan command (standalone + project modes)"
```

---

## Self-Review notes

- Spec coverage: scope C (objects + literals + StatementText) covered by Tasks 4/5; SSMS-openable via XDocument + well-formed assertion (Task 4); readable deterministic tokens + map.json (Tasks 1, 7); pure core `Obfuscate(xml, map)` (Task 4); both CLI modes via `ObfuscationRunner` (Tasks 7, 8); project-shared DuckDB `obfuscation_map` (Task 6); system whitelist (Task 4); safety invariant + fallback (Tasks 3, 5); cross-plan consistency (Tasks 5, 7). The documented v1 limitation (SQL-only identifiers left verbatim) is inherent in the design and needs no task.
- Type consistency: `PlanObfuscator.Obfuscate` returns `(string AnonXml, ObfuscationMap Map)`; `ObfuscationRunner.Run*` return `ObfuscationResult`; `Token`, `Entries`, `FromEntries`, `BuildTextLookup`, `ToJson`/`FromJson`, `Load/SaveObfuscationMap` signatures match across Tasks 1, 2, 4, 6, 7.
- Test convention: orchestration is tested through `ObfuscationRunner` (Task 7, real file + DuckDB I/O), matching `CliSmokeTests.cs` which tests Core directly. The thin CLI wiring (Task 8) is verified with a `dotnet run` invocation, the way `CLAUDE.md` documents end-to-end CLI checks. No CLI process-test harness is introduced.
