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

        private static string? ProcName(ExecuteStatement e)
        {
            if ((e.ExecuteSpecification?.ExecutableEntity as ExecutableProcedureReference)
                    ?.ProcedureReference?.ProcedureReference?.Name is { } id)
                return Name(id);
            return null;
        }

        private static string Name(SchemaObjectName o) =>
            string.Join(".", o.Identifiers.Select(i => i.Value));
    }
}
