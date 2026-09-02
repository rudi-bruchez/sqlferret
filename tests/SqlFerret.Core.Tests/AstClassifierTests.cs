// tests/SqlFerret.Core.Tests/AstClassifierTests.cs
using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFerret.Core.Normalization;
using Xunit;

public class AstClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 1", "SELECT", "dbo.Users")]
    [InlineData("INSERT INTO Orders (Id) VALUES (1)", "INSERT", "Orders")]
    [InlineData("UPDATE dbo.T SET x = 1 WHERE id = 2", "UPDATE", "dbo.T")]
    [InlineData("DELETE FROM Logs WHERE d < '2020-01-01'", "DELETE", "Logs")]
    [InlineData("EXEC dbo.GetOrder @id = 1", "EXEC", "dbo.GetOrder")]
    public void Classifies_kind_and_table(string raw, string kind, string? table)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(table, r.PrimaryTable);
    }

    [Fact]
    public void Garbage_is_OTHER()
    {
        var r = AstClassifier.Classify("@@@ ((");
        Assert.Equal("OTHER", r.Kind);
        Assert.Null(r.PrimaryTable);
        Assert.Null(r.TargetObject);
    }

    [Theory]
    [InlineData("CREATE NONCLUSTERED INDEX IX_A ON dbo.T (c1) INCLUDE (c2)",
                "CREATE INDEX", "dbo.T", "IX_A")]
    [InlineData("CREATE UNIQUE NONCLUSTERED INDEX IX_B ON [S].[T] ([c1] ASC)",
                "CREATE INDEX", "S.T", "IX_B")]
    [InlineData("CREATE CLUSTERED INDEX IX_C ON dbo.T (c1)",
                "CREATE CLUSTERED INDEX", "dbo.T", "IX_C")]
    [InlineData("DROP INDEX IX_A ON dbo.T", "DROP INDEX", "dbo.T", "IX_A")]
    [InlineData("ALTER INDEX IX_A ON dbo.T REBUILD", "ALTER INDEX", "dbo.T", "IX_A")]
    [InlineData("CREATE TABLE dbo.T (c1 INT)", "CREATE TABLE", "dbo.T", null)]
    [InlineData("DROP TABLE dbo.T", "DROP TABLE", "dbo.T", null)]
    [InlineData("TRUNCATE TABLE dbo.T", "TRUNCATE TABLE", "dbo.T", null)]
    [InlineData("ALTER TABLE dbo.T ADD c2 INT NULL",
                "ALTER TABLE ADD COLUMN", "dbo.T", "c2")]
    [InlineData("ALTER TABLE dbo.T ADD CONSTRAINT PK_T PRIMARY KEY NONCLUSTERED (c1)",
                "ALTER TABLE ADD CONSTRAINT", "dbo.T", "PK_T")]
    [InlineData("ALTER TABLE dbo.T ALTER COLUMN c1 BIGINT NOT NULL",
                "ALTER TABLE ALTER COLUMN", "dbo.T", "c1")]
    [InlineData("ALTER TABLE dbo.T DROP CONSTRAINT PK_T",
                "ALTER TABLE DROP", "dbo.T", "PK_T")]
    [InlineData("CREATE PARTITION FUNCTION pf_A (INT) AS RANGE LEFT FOR VALUES (1, 2)",
                "CREATE PARTITION FUNCTION", null, "pf_A")]
    [InlineData("CREATE PARTITION SCHEME ps_A AS PARTITION pf_A ALL TO ([PRIMARY])",
                "CREATE PARTITION SCHEME", null, "ps_A")]
    [InlineData("CREATE STATISTICS st_A ON dbo.T (c1)",
                "CREATE STATISTICS", "dbo.T", "st_A")]
    [InlineData("CREATE VIEW dbo.V AS SELECT 1 AS x",
                "CREATE VIEW", "dbo.V", null)]
    public void Classifies_ddl(string raw, string kind, string? table, string? obj)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(table, r.PrimaryTable);
        Assert.Equal(obj, r.TargetObject);
    }

    [Fact]
    public void Alter_database_is_classified()
    {
        var r = AstClassifier.Classify("ALTER DATABASE [AppDb] SET READ_COMMITTED_SNAPSHOT ON");
        Assert.Equal("ALTER DATABASE", r.Kind);
        Assert.Null(r.PrimaryTable);          // une base n'est pas une table
        Assert.Equal("AppDb", r.TargetObject);
    }

    // Regression : le motif LIKE '% ADD %' d'une analyse manuelle ratait cette forme,
    // parce qu'une tabulation sépare le nom de la table du mot ADD. Le parseur, lui,
    // est indifférent à la nature de l'espace.
    [Fact]
    public void Classify_AlterTableAddColumn_WithTabBeforeAdd()
    {
        var r = AstClassifier.Classify(
            "ALTER TABLE AppSchema.WidgetScaling \tADD SCALEID AS TRY_CONVERT(numeric(6,0), SCALEVALUE) PERSISTED");
        Assert.Equal("ALTER TABLE ADD COLUMN", r.Kind);
        Assert.Equal("AppSchema.WidgetScaling", r.PrimaryTable);
        Assert.Equal("SCALEID", r.TargetObject);
    }

    // ---------------------------------------------------------------------------------------
    // Regression : les formes `ALTER X` de module n'avaient aucun visiteur. La premiere
    // instruction de leur corps gagnait alors le verrou de Set(), et l'on obtenait une valeur
    // fausse — pas un trou : un remplacement de procedure sortait en UPDATE, impute a une table
    // que la procedure ne touche pas.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Alter_procedure_is_not_classified_from_its_body()
    {
        var r = AstClassifier.Classify(
            "ALTER PROCEDURE [dbo].[usp_RebuildWidgets] AS BEGIN UPDATE AppSchema.WidgetScaling SET x=1 END");
        Assert.Equal("ALTER PROCEDURE", r.Kind);
        Assert.Null(r.PrimaryTable);                       // et surtout pas AppSchema.WidgetScaling
        Assert.Equal("dbo.usp_RebuildWidgets", r.TargetObject);
    }

    [Fact]
    public void Alter_view_is_not_classified_from_its_body()
    {
        var r = AstClassifier.Classify("ALTER VIEW dbo.V AS SELECT * FROM dbo.BigTable");
        Assert.Equal("ALTER VIEW", r.Kind);
        Assert.Equal("dbo.V", r.PrimaryTable);             // la vue, pas dbo.BigTable
    }

    [Theory]
    // Les formes CREATE gardent leur classification d'origine : la surcharge de classe de base ne
    // doit rien changer pour elles.
    [InlineData("CREATE PROCEDURE dbo.P AS SELECT 1", "CREATE PROCEDURE", null, "dbo.P")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.P AS SELECT 1", "CREATE PROCEDURE", null, "dbo.P")]
    [InlineData("ALTER PROCEDURE dbo.P AS SELECT 1", "ALTER PROCEDURE", null, "dbo.P")]
    [InlineData("CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS x", "CREATE VIEW", "dbo.V", null)]
    [InlineData("CREATE FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END",
                "CREATE FUNCTION", null, "dbo.F")]
    [InlineData("ALTER FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN (SELECT 1 FROM dbo.T) END",
                "ALTER FUNCTION", null, "dbo.F")]
    [InlineData("CREATE TRIGGER dbo.TR ON dbo.T AFTER INSERT AS SELECT 1",
                "CREATE TRIGGER", "dbo.T", "dbo.TR")]
    [InlineData("ALTER TRIGGER dbo.TR ON dbo.T AFTER INSERT AS DELETE FROM dbo.Autre",
                "ALTER TRIGGER", "dbo.T", "dbo.TR")]
    public void Classifies_module_create_and_alter(string raw, string kind, string? table, string? obj)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(table, r.PrimaryTable);
        Assert.Equal(obj, r.TargetObject);
    }

    // ---------------------------------------------------------------------------------------
    // Regression : ~1 300 signatures parfaitement analysables de la trace reelle restaient OTHER
    // faute de visiteur (DROP PROCEDURE/FUNCTION/TRIGGER/VIEW, ALTER TABLE REBUILD/SWITCH/ENABLE,
    // MERGE, FETCH).
    // ---------------------------------------------------------------------------------------
    [Theory]
    [InlineData("DROP PROCEDURE dbo.P", "DROP PROCEDURE", null, "dbo.P")]
    [InlineData("DROP PROCEDURE IF EXISTS [dbo].[P]", "DROP PROCEDURE", null, "dbo.P")]
    [InlineData("DROP FUNCTION dbo.F", "DROP FUNCTION", null, "dbo.F")]
    [InlineData("DROP TRIGGER dbo.TR", "DROP TRIGGER", null, "dbo.TR")]
    [InlineData("DROP VIEW dbo.V", "DROP VIEW", "dbo.V", null)]
    [InlineData("ALTER TABLE dbo.T REBUILD", "ALTER TABLE REBUILD", "dbo.T", null)]
    [InlineData("ALTER TABLE dbo.T REBUILD PARTITION = 3 WITH (ONLINE = ON)",
                "ALTER TABLE REBUILD", "dbo.T", null)]
    [InlineData("ALTER TABLE dbo.T SWITCH PARTITION 1 TO dbo.U PARTITION 1",
                "ALTER TABLE SWITCH", "dbo.T", "dbo.U")]
    [InlineData("ALTER TABLE dbo.T ENABLE TRIGGER TR_A", "ALTER TABLE ENABLE TRIGGER", "dbo.T", "TR_A")]
    [InlineData("ALTER TABLE dbo.T DISABLE TRIGGER ALL", "ALTER TABLE DISABLE TRIGGER", "dbo.T", null)]
    [InlineData("MERGE INTO dbo.Cible AS t USING dbo.Src AS s ON t.id = s.id "
                + "WHEN MATCHED THEN UPDATE SET t.x = s.x;", "MERGE", "dbo.Cible", null)]
    [InlineData("MERGE dbo.Cible AS t USING dbo.Src AS s ON t.id = s.id "
                + "WHEN NOT MATCHED THEN INSERT (id) VALUES (s.id);", "MERGE", "dbo.Cible", null)]
    [InlineData("FETCH NEXT FROM cur INTO @a", "FETCH", null, "cur")]
    public void Classifies_previously_untyped_ddl(string raw, string kind, string? table, string? obj)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(table, r.PrimaryTable);
        Assert.Equal(obj, r.TargetObject);
    }

    // -----------------------------------------------------------------------------------------
    // Regression : un filet pose sur la classe de base AlterTableStatement etait battu par une
    // instruction ULTERIEURE du lot, parce qu'il ne verrouillait pas le resultat. On obtenait la
    // classe de defaut que B3 corrigeait — une valeur fausse, pas absente. Le filet a ete
    // remplace par l'enumeration des sous-classes concretes : un seul mecanisme de preseance,
    // celui de Set(), et la premiere instruction gagne toujours.
    // -----------------------------------------------------------------------------------------
    [Theory]
    [InlineData("ALTER TABLE dbo.T SET (LOCK_ESCALATION = AUTO);\nSELECT * FROM dbo.U;",
                "ALTER TABLE SET", "dbo.T")]
    [InlineData("ALTER TABLE dbo.T WITH CHECK CHECK CONSTRAINT ALL;\nUPDATE dbo.Z SET a = 1;",
                "ALTER TABLE CHECK CONSTRAINT", "dbo.T")]
    [InlineData("ALTER TABLE dbo.T ADD c2 INT;\nSELECT * FROM dbo.U;",
                "ALTER TABLE ADD COLUMN", "dbo.T")]
    // Et symetriquement : une premiere instruction non-ALTER TABLE garde la main.
    [InlineData("SELECT * FROM dbo.U;\nALTER TABLE dbo.T SET (LOCK_ESCALATION = AUTO);",
                "SELECT", "dbo.U")]
    public void The_first_statement_of_a_batch_wins_for_every_alter_table_form(
        string raw, string kind, string table)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(table, r.PrimaryTable);
    }

    [Theory]
    [InlineData("ALTER TABLE dbo.T SET (LOCK_ESCALATION = AUTO)", "ALTER TABLE SET", null)]
    [InlineData("ALTER TABLE dbo.T WITH CHECK CHECK CONSTRAINT FK_A",
                "ALTER TABLE CHECK CONSTRAINT", "FK_A")]
    [InlineData("ALTER TABLE dbo.T NOCHECK CONSTRAINT ALL", "ALTER TABLE NOCHECK CONSTRAINT", null)]
    [InlineData("ALTER TABLE dbo.T ENABLE CHANGE_TRACKING", "ALTER TABLE ENABLE CHANGE_TRACKING", null)]
    [InlineData("ALTER TABLE dbo.T DISABLE CHANGE_TRACKING", "ALTER TABLE DISABLE CHANGE_TRACKING", null)]
    [InlineData("ALTER TABLE dbo.T SPLIT RANGE (10)", "ALTER TABLE SPLIT PARTITION", null)]
    [InlineData("ALTER TABLE dbo.T MERGE RANGE (10)", "ALTER TABLE MERGE PARTITION", null)]
    public void Classifies_the_remaining_alter_table_forms(string raw, string kind, string? obj)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal("dbo.T", r.PrimaryTable);
        Assert.Equal(obj, r.TargetObject);
    }

    /// <summary>
    /// Ce que le filet de classe de base promettait, obtenu autrement : aucune forme
    /// <c>ALTER TABLE</c> ne peut etre oubliee en silence. Le jeu des sous-classes concretes de
    /// <c>AlterTableStatement</c> est ferme et fige par la version du paquet ScriptDom ; ce test
    /// echouera si une mise a jour en ajoute une, au lieu de la laisser retomber en OTHER.
    /// </summary>
    [Fact]
    public void All_alter_table_forms_are_covered_by_a_visitor()
    {
        var visitorMethods = typeof(AstClassifier)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Single(t => t.Name == "ClassifyVisitor")
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Visit")
            .Select(m => m.GetParameters()[0].ParameterType)
            .ToHashSet();

        var baseType = typeof(AlterTableStatement);
        var uncovered = baseType.Assembly.GetTypes()
            .Where(t => baseType.IsAssignableFrom(t) && t != baseType && !t.IsAbstract)
            .Where(t => !visitorMethods.Contains(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal([], uncovered);
    }

    // ---------------------------------------------------------------------------------------
    // Regression : `DROP INDEX table.index` (syntaxe heritee, celle des vieux scripts d'upgrade)
    // ne rendait ni table ni index. ScriptDom la remonte en BackwardsCompatibleDropIndexClause,
    // que le test `is DropIndexClause` manquait — et rien ne le signalait, puisque kind != OTHER.
    // ---------------------------------------------------------------------------------------
    [Theory]
    [InlineData("DROP INDEX dbo.T.IX_A", "dbo.T", "IX_A")]
    [InlineData("DROP INDEX T.IX_A", "T", "IX_A")]
    [InlineData("DROP INDEX [dbo].[T].[IX_A]", "dbo.T", "IX_A")]
    [InlineData("DROP INDEX IX_A ON dbo.T", "dbo.T", "IX_A")]      // forme moderne, inchangee
    public void Drop_index_legacy_syntax_yields_table_and_index(string raw, string table, string index)
    {
        var r = AstClassifier.Classify(raw);
        Assert.Equal("DROP INDEX", r.Kind);
        Assert.Equal(table, r.PrimaryTable);
        Assert.Equal(index, r.TargetObject);
    }


    // Regression : une regex à préfixe de schéma glouton extrayait "_SCALING" de
    // "[WidgetScaling]", faute de point pour délimiter le schéma.
    [Fact]
    public void Classify_UnqualifiedTableWithUnderscore()
    {
        var r = AstClassifier.Classify("ALTER TABLE [WidgetScaling] ALTER COLUMN [TAGID] INT NOT NULL");
        Assert.Equal("ALTER TABLE ALTER COLUMN", r.Kind);
        Assert.Equal("WidgetScaling", r.PrimaryTable);
        Assert.Equal("TAGID", r.TargetObject);
    }
}
