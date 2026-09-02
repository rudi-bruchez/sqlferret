// src/SqlFerret.Core/Normalization/AstClassifier.cs
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

/// <summary>
/// Résultat de la classification d'une instruction. <paramref name="PrimaryTable"/> porte le nom
/// tel qu'écrit dans l'instruction, schéma compris quand il est présent.
/// </summary>
public readonly record struct ClassificationResult(string Kind, string? PrimaryTable, string? TargetObject);

public static class AstClassifier
{
    public static ClassificationResult Classify(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql)) return new("OTHER", null, null);
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(rawSql);
            var fragment = parser.Parse(reader, out IList<ParseError> errors);
            if (errors.Count > 0 || fragment is null) return new("OTHER", null, null);

            var visitor = new ClassifyVisitor();
            fragment.Accept(visitor);
            return new(visitor.Kind ?? "OTHER", visitor.PrimaryTable, visitor.TargetObject);
        }
        catch { return new("OTHER", null, null); }   // repli délibéré : texte non parsable
    }

    private sealed class ClassifyVisitor : TSqlFragmentVisitor
    {
        public string? Kind { get; private set; }
        public string? PrimaryTable { get; private set; }
        public string? TargetObject { get; private set; }

        // ----- DML (comportement historique, inchangé) -----
        public override void Visit(SelectStatement node) => Set("SELECT", FirstTable(node), null);
        public override void Visit(InsertStatement node) => Set("INSERT", NamedTarget(node.InsertSpecification?.Target), null);
        public override void Visit(UpdateStatement node) => Set("UPDATE", NamedTarget(node.UpdateSpecification?.Target), null);
        public override void Visit(DeleteStatement node) => Set("DELETE", NamedTarget(node.DeleteSpecification?.Target), null);
        public override void Visit(ExecuteStatement node) => Set("EXEC", ProcName(node), null);

        // ----- Index -----
        public override void Visit(CreateIndexStatement node) =>
            Set(node.Clustered == true ? "CREATE CLUSTERED INDEX" : "CREATE INDEX",
                Name(node.OnName), node.Name?.Value);

        public override void Visit(DropIndexStatement node)
        {
            switch (node.DropIndexClauses.FirstOrDefault())
            {
                case DropIndexClause c:
                    Set("DROP INDEX", Name(c.Object), c.Index?.Value);
                    break;
                // Forme heritee `DROP INDEX [schema.]table.index`, celle des scripts d'upgrade
                // anciens : ScriptDom ne la rend pas en DropIndexClause mais en
                // BackwardsCompatibleDropIndexClause, dont le ChildObjectName porte la table dans
                // ses identifiants de tete et l'index dans ChildIdentifier.
                case BackwardsCompatibleDropIndexClause b:
                    Set("DROP INDEX", Qualifier(b.Index), b.Index?.ChildIdentifier?.Value);
                    break;
                default:
                    Set("DROP INDEX", null, null);
                    break;
            }
        }

        public override void Visit(AlterIndexStatement node) =>
            Set("ALTER INDEX", Name(node.OnName), node.Name?.Value);

        // ----- Tables -----
        public override void Visit(CreateTableStatement node) =>
            Set("CREATE TABLE", Name(node.SchemaObjectName), null);

        public override void Visit(DropTableStatement node) =>
            Set("DROP TABLE", node.Objects.FirstOrDefault() is { } o ? Name(o) : null, null);

        public override void Visit(TruncateTableStatement node) =>
            Set("TRUNCATE TABLE", Name(node.TableName), null);

        public override void Visit(AlterTableAddTableElementStatement node)
        {
            var table = Name(node.SchemaObjectName);
            if (node.Definition?.ColumnDefinitions.FirstOrDefault() is { } col)
                Set("ALTER TABLE ADD COLUMN", table, col.ColumnIdentifier?.Value);
            else if (node.Definition?.TableConstraints.FirstOrDefault() is { } ct)
                Set("ALTER TABLE ADD CONSTRAINT", table, ct.ConstraintIdentifier?.Value);
            else
                Set("ALTER TABLE ADD", table, null);
        }

        public override void Visit(AlterTableAlterColumnStatement node) =>
            Set("ALTER TABLE ALTER COLUMN", Name(node.SchemaObjectName), node.ColumnIdentifier?.Value);

        public override void Visit(AlterTableDropTableElementStatement node) =>
            Set("ALTER TABLE DROP", Name(node.SchemaObjectName),
                node.AlterTableDropTableElements.FirstOrDefault()?.Name?.Value);

        public override void Visit(AlterTableRebuildStatement node) =>
            Set("ALTER TABLE REBUILD", Name(node.SchemaObjectName), null);

        public override void Visit(AlterTableSwitchStatement node) =>
            Set("ALTER TABLE SWITCH", Name(node.SchemaObjectName), Name(node.TargetTable));

        public override void Visit(AlterTableTriggerModificationStatement node) =>
            Set($"ALTER TABLE {node.TriggerEnforcement.ToString().ToUpperInvariant()} TRIGGER",
                Name(node.SchemaObjectName), node.TriggerNames.FirstOrDefault()?.Value);

        // Les formes ALTER TABLE restantes. Elles sont enumerees une par une, et non captees par
        // un filet sur AlterTableStatement : ScriptDom appelle la surcharge de la classe de base
        // AVANT la plus derivee, si bien qu'un filet ne peut pas se contenter d'un Set() — il
        // faudrait un second mecanisme de preseance, et ce mecanisme s'est revele faux des qu'un
        // lot contenait plusieurs instructions. AlterTableStatement n'a que treize sous-classes
        // concretes, toutes ci-dessus ou ci-dessous ; AllAlterTableFormsAreCovered le verrouille.
        public override void Visit(AlterTableConstraintModificationStatement node) =>
            Set($"ALTER TABLE {(node.ConstraintEnforcement == ConstraintEnforcement.NoCheck ? "NOCHECK" : "CHECK")} CONSTRAINT",
                Name(node.SchemaObjectName), node.ConstraintNames.FirstOrDefault()?.Value);

        public override void Visit(AlterTableSetStatement node) =>
            Set("ALTER TABLE SET", Name(node.SchemaObjectName), null);

        public override void Visit(AlterTableChangeTrackingModificationStatement node) =>
            Set(node.IsEnable ? "ALTER TABLE ENABLE CHANGE_TRACKING" : "ALTER TABLE DISABLE CHANGE_TRACKING",
                Name(node.SchemaObjectName), null);

        public override void Visit(AlterTableAlterIndexStatement node) =>
            Set("ALTER TABLE ALTER INDEX", Name(node.SchemaObjectName), node.IndexIdentifier?.Value);

        public override void Visit(AlterTableAlterPartitionStatement node) =>
            Set(node.IsSplit ? "ALTER TABLE SPLIT PARTITION" : "ALTER TABLE MERGE PARTITION",
                Name(node.SchemaObjectName), null);

        public override void Visit(AlterTableAddClusterByStatement node) =>
            Set("ALTER TABLE ADD CLUSTER BY", Name(node.SchemaObjectName), null);

        public override void Visit(AlterTableFileTableNamespaceStatement node) =>
            Set(node.IsEnable ? "ALTER TABLE ENABLE FILETABLE_NAMESPACE" : "ALTER TABLE DISABLE FILETABLE_NAMESPACE",
                Name(node.SchemaObjectName), null);

        // ----- Base, partitionnement, statistiques -----
        // Le nom de la base va dans TargetObject, pas dans PrimaryTable : une base n'est pas une
        // table, et l'y mettre polluerait tout GROUP BY primary_table.
        // Si le visiteur de la classe abstraite n'existe pas dans cette version de ScriptDom,
        // remplacer par les sous-classes concrètes (AlterDatabaseSetStatement, etc.).
        public override void Visit(AlterDatabaseStatement node) =>
            Set("ALTER DATABASE", null, node.DatabaseName?.Value);

        public override void Visit(CreatePartitionFunctionStatement node) =>
            Set("CREATE PARTITION FUNCTION", null, node.Name?.Value);

        public override void Visit(CreatePartitionSchemeStatement node) =>
            Set("CREATE PARTITION SCHEME", null, node.Name?.Value);

        public override void Visit(CreateStatisticsStatement node) =>
            Set("CREATE STATISTICS", Name(node.OnName), node.Name?.Value);

        public override void Visit(UpdateStatisticsStatement node) =>
            Set("UPDATE STATISTICS", Name(node.SchemaObjectName), node.SubElements.FirstOrDefault()?.Value);

        // ----- Modules -----
        // Une surcharge par **classe de base** ScriptDom, pas une par mot-cle : chaque base couvre
        // d'un coup CREATE, ALTER et CREATE OR ALTER. Sans elles, les formes `ALTER X` n'avaient
        // aucun visiteur et la premiere instruction de leur *corps* gagnait le verrou de Set() :
        // un `ALTER PROCEDURE ... AS BEGIN UPDATE T ... END` sortait en `UPDATE` sur la table `T`,
        // et un `GROUP BY primary_table` imputait le cout du remplacement de la procedure a une
        // table qu'elle ne touche pas. Ce n'etait pas un trou, c'etait une valeur fausse.
        // La distinction creation/alteration est conservee : elle est observable sur le type du
        // noeud, et elle porte du sens dans un audit d'upgrade. `CREATE OR ALTER` reste range avec
        // les creations, comme a l'origine : le texte ne dit pas laquelle des deux a eu lieu.
        public override void Visit(ProcedureStatementBody node) =>
            Set(node is AlterProcedureStatement ? "ALTER PROCEDURE" : "CREATE PROCEDURE",
                null, Name(node.ProcedureReference?.Name));

        public override void Visit(ViewStatementBody node) =>
            Set(node is AlterViewStatement ? "ALTER VIEW" : "CREATE VIEW",
                Name(node.SchemaObjectName), null);

        public override void Visit(FunctionStatementBody node) =>
            Set(node is AlterFunctionStatement ? "ALTER FUNCTION" : "CREATE FUNCTION",
                null, Name(node.Name));

        public override void Visit(TriggerStatementBody node) =>
            Set(node is AlterTriggerStatement ? "ALTER TRIGGER" : "CREATE TRIGGER",
                Name(node.TriggerObject?.Name), Name(node.Name));

        // DROP : chaque forme a sa propre sous-classe de DropObjectsStatement — y compris
        // DropTableStatement, d'ou l'absence de visiteur sur la base commune, qui ecraserait la
        // distinction. `Objects` peut en contenir plusieurs (`DROP VIEW a, b`) ; on garde le
        // premier, comme le fait deja DROP TABLE.
        public override void Visit(DropProcedureStatement node) =>
            Set("DROP PROCEDURE", null, FirstName(node));
        public override void Visit(DropFunctionStatement node) =>
            Set("DROP FUNCTION", null, FirstName(node));
        public override void Visit(DropTriggerStatement node) =>
            Set("DROP TRIGGER", null, FirstName(node));
        // La vue prend la place de la table, comme pour CREATE VIEW : c'est l'objet interroge.
        public override void Visit(DropViewStatement node) =>
            Set("DROP VIEW", FirstName(node), null);

        // ----- MERGE et curseurs -----
        // MERGE est du DML : la cible est le renseignement utile, comme pour UPDATE/DELETE.
        public override void Visit(MergeStatement node) =>
            Set("MERGE", NamedTarget(node.MergeSpecification?.Target), null);

        public override void Visit(FetchCursorStatement node) =>
            Set("FETCH", null, node.Cursor?.Name?.Value);

        private bool _set;

        /// <summary>
        /// La <b>première instruction</b> gagne, et elle gagne <b>en bloc</b>. Un verrou unique,
        /// et non un <c>??=</c> par champ : sinon un <c>CREATE PROCEDURE</c> — qui ne porte pas de
        /// table — laisserait la première instruction de son corps renseigner
        /// <c>PrimaryTable</c>, et l'on obtiendrait le type d'une instruction avec la table d'une
        /// autre.
        /// </summary>
        private void Set(string kind, string? table, string? target)
        {
            if (_set) return;
            _set = true;
            Kind = kind;
            PrimaryTable = table;
            TargetObject = target;
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

        private static string? Name(SchemaObjectName? o) =>
            o is null ? null : string.Join(".", o.Identifiers.Select(i => i.Value));

        private static string? FirstName(DropObjectsStatement s) =>
            s.Objects.FirstOrDefault() is { } o ? Name(o) : null;

        /// <summary>
        /// Tout sauf le dernier identifiant d'un <see cref="ChildObjectName"/>, c'est-a-dire
        /// l'objet qui porte l'enfant : <c>dbo.T.IX_A</c> donne <c>dbo.T</c>, <c>T.IX_A</c> donne
        /// <c>T</c>. <see cref="Name"/> ne convient pas, il joindrait aussi l'enfant.
        /// </summary>
        private static string? Qualifier(ChildObjectName? o) =>
            o is null || o.Identifiers.Count < 2
                ? null
                : string.Join(".", o.Identifiers.Take(o.Identifiers.Count - 1).Select(i => i.Value));
    }
}
