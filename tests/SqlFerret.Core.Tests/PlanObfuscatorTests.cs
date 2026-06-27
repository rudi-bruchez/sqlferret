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

    private static string RichPlan() => $"""
    <ShowPlanXML xmlns="{Ns}">
      <StmtSimple StatementText="SELECT SSN FROM Customers WHERE SSN = '123-45-6789'">
        <QueryPlan>
          <MissingIndexes>
            <MissingIndexGroup Impact="78.5432">
              <MissingIndex Database="[Sales]" Schema="[dbo]" Table="[Customers]">
                <ColumnGroup Usage="EQUALITY">
                  <Column Name="[SSN]" ColumnId="3" />
                </ColumnGroup>
              </MissingIndex>
            </MissingIndexGroup>
          </MissingIndexes>
          <RelOp>
            <Filter>
              <Predicate>
                <ScalarOperator ScalarString="[Customers].[SSN]='123-45-6789'">
                  <Const ConstValue="N'123-45-6789'" />
                </ScalarOperator>
              </Predicate>
              <Object Database="[Sales]" Schema="[dbo]" Table="[Customers]" />
              <ColumnReference Database="[Sales]" Schema="[dbo]" Table="[Customers]" Column="SSN" />
            </Filter>
            <RemoteQuery RemoteSource="[LinkedSrv]" RemoteObject="[Customers]" RemoteQuery="SELECT SSN FROM Customers WHERE SSN = '123-45-6789'" />
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
        Assert.DoesNotContain("LinkedSrv", xml);     // remote server name gone
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
        Assert.Contains("@Param2", b);     // proves the shared map's parameter counter advanced (not a fresh map)
        Assert.DoesNotContain("@Param1", b);
    }
}
