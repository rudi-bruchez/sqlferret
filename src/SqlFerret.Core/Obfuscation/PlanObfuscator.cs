// src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Obfuscation;

public static class PlanObfuscator
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "tempdb" };
    private static readonly HashSet<string> InternalTables = new(StringComparer.OrdinalIgnoreCase) { "Worktable", "Workfile" };

    // Matches bracketed segments in multi-part names like [Sales].[dbo].[GetCustomer].
    private static readonly Regex BracketSegment = new(@"\[([^\]]*)\]", RegexOptions.Compiled);

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
        Set(el, "Table", NameKind.Table, map);
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
                if (errors.Count > 0 || fragment is null) continue;
                fragment.Accept(visitor);
            }
            catch { }
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
