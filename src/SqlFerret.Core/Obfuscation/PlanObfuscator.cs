// src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Obfuscation;

public static class PlanObfuscator
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };
    // tempdb is intentionally NOT whitelisted — user objects inside tempdb (temp tables) must be
    // mapped. The engine-internal Worktable/Workfile are preserved via InternalTables (by table name).
    private static readonly HashSet<string> InternalTables = new(StringComparer.OrdinalIgnoreCase) { "Worktable", "Workfile" };

    // Matches bracketed segments in multi-part names like [Sales].[dbo].[GetCustomer].
    private static readonly Regex BracketSegment = new(@"\[([^\]]*)\]", RegexOptions.Compiled);

    // Comment-stripping patterns (same as StatementTextRewriter.Fallback) used before the DDL regex.
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"--[^\n]*", RegexOptions.Compiled);

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

    // DDL clauses whose defined name is a single identifier of a NON-Table kind, so they cannot go
    // through DdlLeadClause (which assigns Database/Schema/Table by position). Regex fallbacks for the
    // truncated-StatementText case; the AST visitor covers well-formed statements (review fix #4).
    private const string OneNamePart = """(\[[^\]]+\]|"[^"]+"|[A-Za-z_][A-Za-z0-9_@#$]*)""";
    private static readonly Regex DdlIndexClause = new(
        """(?:CREATE|ALTER)\s+(?:UNIQUE\s+)?(?:(?:NON)?CLUSTERED\s+)?(?:COLUMNSTORE\s+)?INDEX\s+""" + OneNamePart,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DdlStatisticsClause = new(
        """CREATE\s+STATISTICS\s+""" + OneNamePart, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DdlSchemaClause = new(
        """CREATE\s+SCHEMA\s+""" + OneNamePart, RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        var body = doc.ToString(SaveOptions.DisableFormatting);
        // doc.ToString() drops the XML declaration. SQL Server emits .sqlplan as UTF-16 with a
        // declaration; the runner writes the returned string as UTF-8. Re-emit the declaration
        // normalized to utf-8 so the declared encoding matches the bytes on disk and the file stays
        // SSMS-openable (review fix #8). Plans without a declaration keep none.
        if (doc.Declaration is null)
            return (body, map);
        var decl = new XDeclaration(doc.Declaration.Version, "utf-8", doc.Declaration.Standalone);
        return (decl + Environment.NewLine + body, map);
    }

    private static void RenameNode(XElement el, ObfuscationMap map)
    {
        var schema = Val(el, "Schema");
        var table = Val(el, "Table");
        var whitelisted =
            (schema is not null && SystemSchemas.Contains(ObfuscationMap.Strip(schema)))
            || (table is not null && InternalTables.Contains(ObfuscationMap.Strip(table)));

        // The Database component is a user-chosen name even on whitelisted system/internal objects
        // (e.g. a query touching sys.indexes inside a user database), so it is sensitive and must be
        // mapped unconditionally — BEFORE the whitelist short-circuit (review fix #2).
        Set(el, "Database", NameKind.Database, map);
        if (whitelisted)
            return; // whitelisted: neither the system object nor its columns are mapped further

        // Standard single-component name attributes.
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
            var trimmed = col.Value.TrimStart('[');
            if (!trimmed.StartsWith("@@", StringComparison.Ordinal))
            {
                var kind = trimmed.StartsWith('@') ? NameKind.Parameter : NameKind.Column;
                Rename(col, kind, map);
            }
            // @@-globals: leave the Column attribute untouched (preserved)
        }

        // Name= attribute: only for <Column> elements (MissingIndexes/ColumnGroup).
        // Ordinary <ColumnReference> uses Column=, not Name=, so no overlap.
        // Do NOT map Name on other elements — it appears on non-sensitive nodes.
        if (el.Name.LocalName == "Column")
            Set(el, "Name", NameKind.Column, map);

        // Multi-part name attributes: values like [Sales].[dbo].[GetCustomer].
        // Segments are shared with Object references so the same [Sales] -> Db1 everywhere.
        SetMultiPart(el, "ProcName", map);
        // FunctionName on <Intrinsic> is a built-in (isnull, getdate, …), NOT a user object:
        // mapping it would corrupt the SQL text and inflate the Table counter (review fix #1).
        // <UserDefinedFunction FunctionName="…"> is still mapped.
        if (el.Name.LocalName != "Intrinsic")
            SetMultiPart(el, "FunctionName", map);
    }

    // Maps a multi-part bracketed name attribute (e.g. ProcName="[db].[schema].[proc]").
    // Assigns kinds by position: 1-part -> Table; 2-part -> Schema,Table; 3+-part -> Database,Schema,Table.
    // Tokens are shared with Object references via the same map.Token(kind, segment) call.
    private static void SetMultiPart(XElement el, string attrName, ObfuscationMap map)
    {
        var a = el.Attribute(attrName);
        if (a is null || string.IsNullOrEmpty(a.Value)) return;

        // Use NamePartSegment (not a bracket-only scan) so MIXED quoting like myschema.[Proc]
        // keeps every part: a bare segment alongside a bracketed one is no longer dropped
        // (review fix #13). Mirrors MapDdlMultiPartName.
        var matches = NamePartSegment.Matches(a.Value);
        if (matches.Count == 0) return;
        var segments = matches.Select(m => ExtractInnerName(m.Value)).ToList();

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
            var token = map.Token(NameKind.TempTable, ObfuscationMap.NormalizeTempName(stripped));
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
        var token = map.Token(kind, a.Value);
        // Parameter tokens carry the '@' sigil (prefix "@Param"), so emit verbatim everywhere — no re-prepend needed.
        a.Value = hadBrackets ? "[" + token + "]" : token;
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
            // Single-name DDL of non-Table kinds (index/statistics/schema) — review fix #4.
            foreach (Match m in DdlIndexClause.Matches(stripped))
                map.Token(NameKind.Index, ExtractInnerName(m.Groups[1].Value));
            foreach (Match m in DdlStatisticsClause.Matches(stripped))
                map.Token(NameKind.Statistics, ExtractInnerName(m.Groups[1].Value));
            foreach (Match m in DdlSchemaClause.Matches(stripped))
                map.Token(NameKind.Schema, ExtractInnerName(m.Groups[1].Value));
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
    // Mirrors the by-position kind logic in SetMultiPart and SetTable; no attribute is written here.
    private static void MapDdlMultiPartName(string rawName, ObfuscationMap map)
    {
        var parts = NamePartSegment.Matches(rawName);
        if (parts.Count == 0) return;

        // Keep the raw match alongside the bracket/quote-stripped segment.
        // The raw form is needed to detect the '#' temp prefix for bare names like #Foo,
        // because ExtractInnerName strips '#' (it returns "Foo" for "#Foo").
        var rawParts = parts.Select(m => m.Value).ToList();
        var segments = rawParts.Select(ExtractInnerName).ToList();
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
        int skip = Math.Max(0, segments.Count - kinds.Length);
        for (int i = 0; i < kinds.Length; i++)
        {
            var raw = rawParts[skip + i];
            var seg = segments[skip + i];
            var kind = kinds[i];

            // For the object-name (Table) slot, detect a temp name and route it to TempTable
            // so the map key matches the '#'-keyed lookup used by SetTable and the rewriter.
            // Two quoting forms to handle:
            //   bracketed [#Name] — ExtractInnerName returns "#Name" (keeps '#')
            //   bare      #Name   — ExtractInnerName returns "Name" (stripped '#'), raw has it
            // Mirror exactly how SetTable decides temp-vs-regular so both paths share one token.
            if (kind == NameKind.Table)
            {
                var nameWithHash = seg.StartsWith('#') ? seg
                                 : raw.StartsWith('#') ? raw
                                 : null;
                if (nameWithHash is not null)
                {
                    map.Token(NameKind.TempTable, ObfuscationMap.NormalizeTempName(nameWithHash));
                    continue;
                }
            }
            map.Token(kind, seg);
        }
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

        // Non-Table single-name DDL: the defined name maps to Index/Statistics/Schema, not Table
        // (review fix #4). The indexed/analyzed table (OnName) is still collected like any object ref.
        public override void Visit(CreateIndexStatement node)
        {
            if (node.Name is not null) map.Token(NameKind.Index, node.Name.Value);
            CollectName(node.OnName);
        }
        public override void Visit(CreateStatisticsStatement node)
        {
            if (node.Name is not null) map.Token(NameKind.Statistics, node.Name.Value);
            CollectName(node.OnName);
        }
        public override void Visit(CreateSchemaStatement node)
        {
            if (node.Name is not null) map.Token(NameKind.Schema, node.Name.Value);
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
