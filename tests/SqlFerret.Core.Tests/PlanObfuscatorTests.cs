// tests/SqlFerret.Core.Tests/PlanObfuscatorTests.cs
using SqlFerret.Core.Obfuscation;

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
        var ex = Record.Exception((Action)(() => System.Xml.Linq.XDocument.Parse(xml)));
        Assert.Null(ex);
    }
}
