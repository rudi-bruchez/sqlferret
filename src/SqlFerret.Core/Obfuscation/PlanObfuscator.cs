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

        // Pass 1: collect + rename names on Object, ColumnReference, MissingIndex,
        //         Column (MissingIndexes/ColumnGroup), and RemoteQuery nodes.
        foreach (var el in doc.Descendants())
        {
            switch (el.Name.LocalName)
            {
                case "Object":
                case "ColumnReference":
                case "MissingIndex":
                    RenameNode(el, map);
                    break;
                case "Column":
                    // <Column Name="[SSN]"> in MissingIndexes/ColumnGroup.
                    // Ordinary <ColumnReference> uses Column=, not Name=, so no overlap.
                    Set(el, "Name", NameKind.Column, map);
                    break;
                case "RemoteQuery":
                    // Linked-server operator: rename source identifier; SQL text rewritten in Pass 2.
                    Set(el, "RemoteObject", NameKind.Database, map);
                    break;
            }
        }

        // Pass 2: rewrite embedded T-SQL and scrub parameter / literal values (map is now complete).
        foreach (var attr in doc.Descendants().Attributes())
        {
            switch (attr.Name.LocalName)
            {
                case "StatementText":
                case "ScalarString":
                case "RemoteQueryText":  // linked-server remote SQL text
                    attr.Value = StatementTextRewriter.Rewrite(attr.Value, map);
                    break;
                case "ConstValue":
                    // Inline constants (e.g. N'123-45-6789') must never survive verbatim.
                    attr.Value = "?";
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
        // Preserve the '@' prefix so parameter tokens remain detectable as parameters
        // on subsequent obfuscation passes (idempotency invariant).
        var hadAt = a.Value.StartsWith('@');
        var token = map.Token(kind, a.Value);
        a.Value = hadBrackets ? "[" + token + "]" : hadAt ? "@" + token : token;
    }
}
