// src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Obfuscation;

public static class PlanObfuscator
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };
    // tempdb is intentionally NOT whitelisted here — user objects inside tempdb (temp tables) must be
    // mapped. The engine-internal Worktable/Workfile are preserved via InternalTables (by table name).
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InternalTables = new(StringComparer.OrdinalIgnoreCase) { "Worktable", "Workfile" };

    // Matches bracketed segments in multi-part names like [Sales].[dbo].[GetCustomer].
    private static readonly Regex BracketSegment = new(@"\[([^\]]*)\]", RegexOptions.Compiled);

    // Comment-stripping patterns (same as StatementTextRewriter.Fallback) used before the DDL regex.
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"--[^\n]*", RegexOptions.Compiled);

    // Strips the SQL Server tempdb uniqueness suffix from a stripped temp-table name.
    // SQL Server mangles local temp names: #Foo becomes #Foo_______________________________0000ABCD
    // (4+ trailing underscores + optional hex). Both forms must share the same map key.
    private static readonly Regex TempNameMangle = new(@"_{4,}[0-9A-Fa-f]*$", RegexOptions.Compiled);

    // Returns the canonical de-mangled form of a bracket/quote-stripped temp table name.
    // Only names starting with '#' are processed; others are returned unchanged.
    private static string NormalizeTempName(string stripped) =>
        stripped.StartsWith('#') ? TempNameMangle.Replace(stripped, "") : stripped;

    // Matches one name-part in any of three quoting forms:
    //   bracketed  [Name]           — extract inner via part[1..^1]
    //   double-quoted "Name"        — extract inner via part[1..^1]  (FIX 4: Strip also trims ")
    //   bare / temp   ##Name #Name  — strip leading '#' chars to get the base name
    private static readonly Regex NamePartSegment = new(
        """(?:\[[^\]]+\]|"[^"]+"|#{0,2}[A-Za-z_][A-Za-z0-9_@#$]*)""",
        RegexOptions.Compiled);

    // Matches the leading DDL define clause in a (possibly truncated) StatementText.
    // Captures the multi-part object name immediately after CREATE/ALTER <type>.
    // Extended (FIX 2) to cover TABLE, SYNONYM, SEQUENCE, TYPE in addition to PROC/FUNCTION/VIEW/TRIGGER.
    // Extended (FIX 3) to capture bracketed, double-quoted, and temp-prefixed name parts.
    // Run AFTER comment stripping so banner comments don't shadow the real clause (FIX 1).
    private static readonly Regex DdlLeadClause = new(
        """(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER|TABLE|SYNONYM|SEQUENCE|TYPE)\s+((?:\[[^\]]+\]|"[^"]+"|#{0,2}[A-Za-z_][A-Za-z0-9_@#$]*)(?:\s*\.\s*(?:\[[^\]]+\]|"[^"]+"|#{0,2}[A-Za-z_][A-Za-z0-9_@#$]*))*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string AnonXml, ObfuscationMap Map) Obfuscate(string showplanXml, ObfuscationMap map)
    {
        var doc = XDocument.Parse(showplanXml, LoadOptions.PreserveWhitespace);

        // Pass 1: collect + rename name attributes on EVERY element.
        // RenameNode is safe on all elements: elements without name attributes are untouched,
        // and whitelisted elements (system schema/db/internal table) are skipped as a whole.
        // This auto-covers StatisticsInfo, StoredProc, UDF, and any future schema-version element.
        foreach (var el in doc.Descendants())
            RenameNode(el, map);

        // Between Pass 1 and Pass 2: harvest DDL-defined object names from StatementText
        // so the rewriter (Pass 2) can substitute them like any other mapped name.
        CollectDdlObjectNames(doc, map);

        // Pass 2: rewrite embedded T-SQL and scrub parameter / literal values (map is now complete).
        foreach (var attr in doc.Descendants().Attributes())
        {
            switch (attr.Name.LocalName)
            {
                case "StatementText":
                case "ScalarString":
                case "RemoteQuery":         // linked-server remote SQL text
                case "ParameterizedText":   // parameterized statement text
                case "Expression":          // PlanAffectingConvert / computed expressions
                    attr.Value = StatementTextRewriter.Rewrite(attr.Value, map);
                    break;
                case "ConstValue":
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

        // Standard single-component name attributes.
        Set(el, "Database", NameKind.Database, map);
        Set(el, "Schema", NameKind.Schema, map);
        SetTable(el, map); // temp tables use NameKind.TempTable; regular tables use NameKind.Table
        Set(el, "Index", NameKind.Index, map);
        Set(el, "Statistics", NameKind.Statistics, map);
        Set(el, "Alias", NameKind.Alias, map);
        Set(el, "Server", NameKind.Database, map);        // 4-part name server component
        Set(el, "RemoteObject", NameKind.Table, map);     // linked-server object name (fix: was Database)
        Set(el, "RemoteSource", NameKind.Database, map);  // linked-server source
        Set(el, "CursorName", NameKind.Table, map);
        Set(el, "PlanGuideName", NameKind.Table, map);
        Set(el, "PlanGuideDB", NameKind.Database, map);
        Set(el, "TemplatePlanGuideName", NameKind.Table, map);
        Set(el, "TemplatePlanGuideDB", NameKind.Database, map);
        Set(el, "Assembly", NameKind.Table, map);
        Set(el, "Method", NameKind.Table, map);
        Set(el, "UDXName", NameKind.Table, map);

        // Column= attribute on ColumnReference: parameter refs start with '@'.
        var col = el.Attribute("Column");
        if (col is not null && !string.IsNullOrEmpty(col.Value))
        {
            var kind = col.Value.TrimStart('[').StartsWith('@') ? NameKind.Parameter : NameKind.Column;
            Rename(col, kind, map);
        }

        // Name= attribute: only for <Column> elements (MissingIndexes/ColumnGroup).
        // Ordinary <ColumnReference> uses Column=, not Name=, so no overlap.
        // Do NOT map Name on other elements — it appears on non-sensitive nodes.
        if (el.Name.LocalName == "Column")
            Set(el, "Name", NameKind.Column, map);

        // Multi-part name attributes: values like [Sales].[dbo].[GetCustomer].
        // Segments are shared with Object references so the same [Sales] -> Db1 everywhere.
        SetMultiPart(el, "ProcName", map);
        SetMultiPart(el, "FunctionName", map);
    }

    // Maps a multi-part bracketed name attribute (e.g. ProcName="[db].[schema].[proc]").
    // Assigns kinds by position: 1-part -> Table; 2-part -> Schema,Table; 3+-part -> Database,Schema,Table.
    // Tokens are shared with Object references via the same map.Token(kind, segment) call.
    private static void SetMultiPart(XElement el, string attrName, ObfuscationMap map)
    {
        var a = el.Attribute(attrName);
        if (a is null || string.IsNullOrEmpty(a.Value)) return;

        List<string> segments;
        var matches = BracketSegment.Matches(a.Value);
        if (matches.Count > 0)
            segments = [.. matches.Select(m => m.Groups[1].Value)];
        else
            segments = [.. a.Value.Split('.', StringSplitOptions.RemoveEmptyEntries)];

        NameKind[] kinds = segments.Count switch
        {
            >= 3 => [NameKind.Database, NameKind.Schema, NameKind.Table],
            2 => [NameKind.Schema, NameKind.Table],
            _ => [NameKind.Table],
        };

        // For >= 3 segments, use only the last 3 (covers 4-part server.db.schema.obj names).
        var usedSegments = segments.TakeLast(kinds.Length).ToList();
        var parts = usedSegments.Zip(kinds, (seg, kind) => "[" + map.Token(kind, seg) + "]");
        a.Value = string.Join(".", parts);
    }

    private static string? Val(XElement el, string name) => el.Attribute(name)?.Value;

    private static void Set(XElement el, string attrName, NameKind kind, ObfuscationMap map)
    {
        var a = el.Attribute(attrName);
        if (a is not null && !string.IsNullOrEmpty(a.Value)) Rename(a, kind, map);
    }

    // Maps the Table= attribute, routing temp-table names (#/##) to NameKind.TempTable so they
    // share the same token kind (and prefix) across the operator tree and SQL text. The mangled
    // form (#Foo_____…hex) and the clean form (#Foo) collapse to the same map key via
    // NormalizeTempName. Regular table names are handled by the normal NameKind.Table path.
    private static void SetTable(XElement el, ObfuscationMap map)
    {
        var a = el.Attribute("Table");
        if (a is null || string.IsNullOrEmpty(a.Value)) return;
        var stripped = ObfuscationMap.Strip(a.Value);
        if (stripped.StartsWith('#'))
        {
            var hadBrackets = a.Value.StartsWith('[');
            var token = map.Token(NameKind.TempTable, NormalizeTempName(stripped));
            a.Value = hadBrackets ? "[" + token + "]" : token;
        }
        else
        {
            Rename(a, NameKind.Table, map);
        }
    }

    private static void Rename(XAttribute a, NameKind kind, ObfuscationMap map)
    {
        var hadBrackets = a.Value.StartsWith('[');
        // Preserve the '@' prefix so parameter tokens remain detectable as parameters
        // on subsequent obfuscation passes (idempotency invariant).
        var hadAt = a.Value.StartsWith('@');
        var token = map.Token(kind, a.Value);
        a.Value = hadBrackets ? "[" + token + "]" : hadAt ? "@" + token : token;
    }

    // Iterates every StatementText attribute in the plan XML and parses each value with ScriptDom
    // to extract the name(s) of any DDL-defined object (CREATE/ALTER PROC/FUNCTION/VIEW/TRIGGER).
    // Those names are added to the map before Pass 2 so the StatementTextRewriter can substitute them.
    // Belt-and-suspenders: the regex fallback always runs in addition to the AST path. SQL Server
    // truncates StatementText for large procs, leaving an incomplete statement that ScriptDom cannot
    // parse — the regex catches the defined name from the leading CREATE/ALTER clause regardless.
    // Mapping is idempotent so running both paths on a well-formed statement is safe.
    private static void CollectDdlObjectNames(XDocument doc, ObfuscationMap map)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var visitor = new DdlNameVisitor(map);
        foreach (var attr in doc.Descendants().Attributes("StatementText"))
        {
            if (string.IsNullOrWhiteSpace(attr.Value)) continue;
            try
            {
                using var reader = new StringReader(attr.Value);
                var fragment = parser.Parse(reader, out IList<ParseError> errors);
                if (errors.Count == 0 && fragment is not null)
                    fragment.Accept(visitor);
            }
            catch { }
            // Regex fallback: strip comments first (FIX 1) then map EVERY clause match (FIX 1/belt-and-suspenders).
            // Stripping comments prevents a banner like /* CREATE PROC dbo.FakeName */ from shadowing
            // the real defined name in the clause that follows.
            // Iterating Matches (not just Match) handles multiple statements and is belt-and-suspenders.
            var stripped = StripComments(attr.Value);
            foreach (Match m in DdlLeadClause.Matches(stripped))
                MapDdlMultiPartName(m.Groups[1].Value, map);
        }
    }

    // Strips SQL block comments (/* ... */) and line comments (-- ...) from a SQL fragment.
    // Used before the DDL regex so banner comments don't shadow the real CREATE/ALTER clause.
    private static string StripComments(string sql)
    {
        var s = BlockComment.Replace(sql, " ");
        return LineComment.Replace(s, " ");
    }

    // Extracts the inner (unquoted) name from a single name-part match.
    //   [Name]   -> Name  (strip outer brackets)
    //   "Name"   -> Name  (strip outer double-quotes; FIX 3/4)
    //   #Name    -> Name  (strip leading temp-table marker; FIX 3)
    //   ##Name   -> Name
    //   Name     -> Name  (bare — no change)
    private static string ExtractInnerName(string part) =>
        part.StartsWith('[') ? part[1..^1] :
        part.StartsWith('"') ? part[1..^1] :
        part.TrimStart('#');

    // Parses a raw multi-part DDL object name (e.g. "dbo.SecretProc", "[dbo].[SecretProc]",
    // "dbo.\"SecretQuoted\"", "#SecretTemp") and pre-populates the obfuscation map with the
    // appropriate token for each name part.
    // Uses NamePartSegment to handle bracketed, double-quoted, and bare/temp-prefixed forms.
    // Mirrors the by-position kind logic in SetMultiPart; no attribute is written here.
    private static void MapDdlMultiPartName(string rawName, ObfuscationMap map)
    {
        var parts = NamePartSegment.Matches(rawName);
        if (parts.Count == 0) return;

        var segments = parts.Select(m => ExtractInnerName(m.Value)).ToList();
        if (segments.Count == 0) return;
        // Skip system schemas for consistency with the AST path.
        if (segments.Count >= 2 && SystemSchemas.Contains(segments[segments.Count - 2])) return;

        NameKind[] kinds = segments.Count switch
        {
            >= 3 => [NameKind.Database, NameKind.Schema, NameKind.Table],
            2 => [NameKind.Schema, NameKind.Table],
            _ => [NameKind.Table],
        };
        // For >= 3 segments take only the last 3 (covers 4-part server.db.schema.obj names).
        var used = segments.TakeLast(kinds.Length).ToList();
        foreach (var (seg, kind) in used.Zip(kinds))
            map.Token(kind, seg);
    }

    private sealed class DdlNameVisitor(ObfuscationMap map) : TSqlFragmentVisitor
    {
        public override void Visit(CreateProcedureStatement node) =>
            CollectName(node.ProcedureReference?.Name);
        public override void Visit(AlterProcedureStatement node) =>
            CollectName(node.ProcedureReference?.Name);
        public override void Visit(CreateFunctionStatement node) =>
            CollectName(node.Name);
        public override void Visit(AlterFunctionStatement node) =>
            CollectName(node.Name);
        public override void Visit(CreateViewStatement node) =>
            CollectName(node.SchemaObjectName);
        public override void Visit(AlterViewStatement node) =>
            CollectName(node.SchemaObjectName);
        public override void Visit(CreateTriggerStatement node)
        {
            CollectName(node.Name);
            CollectName(node.TriggerObject?.Name);
        }
        public override void Visit(AlterTriggerStatement node)
        {
            CollectName(node.Name);
            CollectName(node.TriggerObject?.Name);
        }

        private void CollectName(SchemaObjectName? obj)
        {
            if (obj is null) return;
            var ids = obj.Identifiers;
            if (ids.Count == 0) return;
            // Skip system schemas (defensive whitelist consistency).
            if (ids.Count >= 2 && SystemSchemas.Contains(ids[ids.Count - 2].Value)) return;
            // Map identifier parts by position from the right, sharing tokens with operator-tree refs.
            map.Token(NameKind.Table, ids[ids.Count - 1].Value);
            if (ids.Count >= 2) map.Token(NameKind.Schema, ids[ids.Count - 2].Value);
            if (ids.Count >= 3) map.Token(NameKind.Database, ids[ids.Count - 3].Value);
            // Further-left (server) parts are intentionally ignored.
        }
    }
}
