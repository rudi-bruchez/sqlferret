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

    // Kitchen-sink fixture covering every name-bearing and literal-bearing element
    // identified as a leak vector: StatisticsInfo, StoredProc, UDF, ParameterizedText,
    // Expression (PlanAffectingConvert), plus the previously-covered elements.
    private static string KitchenSinkPlan() => $"""
    <ShowPlanXML xmlns="{Ns}">
      <StmtSimple StatementText="SELECT SSN FROM Customers WHERE SSN='123-45-6789'"
                  ParameterizedText="SELECT SSN FROM Customers WHERE SSN=@1">
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
          <StatisticsInfo Database="[Sales]" Schema="[dbo]" Table="[Customers]" Statistics="[SSN_idx]" />
          <RelOp>
            <Object Database="[Sales]" Schema="[dbo]" Table="[Customers]" Index="[PK_Customers]" />
            <Object Schema="[sys]" Table="[indexes]" />
            <Object Table="[Worktable]" />
            <Object Server="[SecretSrv]" Database="[Sales]" Schema="[dbo]" Table="[AuxTbl]" />
            <ColumnReference Database="[Sales]" Schema="[dbo]" Table="[Customers]" Column="SSN" />
            <RemoteQuery RemoteSource="[LinkedSrv]" RemoteObject="[Customers]"
                         RemoteQuery="SELECT SSN FROM Customers WHERE SSN='123-45-6789'" />
            <Fetch CursorName="[SecretCursor]" />
            <PlanGuideInfo PlanGuideName="[SecretGuide]" PlanGuideDB="[SecretGuideDb]" />
            <TemplatePlanGuideInfo TemplatePlanGuideName="[SecretTmpl]" TemplatePlanGuideDB="[SecretTmplDb]" />
            <StoredProc ProcName="[Sales].[dbo].[GetCustomer]" />
            <StoredProc ProcName="[SecretSrv2].[Sales].[dbo].[GetCustomer]" />
            <UDF Assembly="[SecretAsm]" Method="[SecretMethod]" FunctionName="[Sales].[dbo].[FmtSSN]" />
            <UDX UDXName="[SecretUdx]" />
            <Predicate>
              <ScalarOperator ScalarString="[Customers].[SSN]='123-45-6789'">
                <Const ConstValue="N'123-45-6789'" />
              </ScalarOperator>
            </Predicate>
            <PlanAffectingConvert Expression="CONVERT(int,[Sales].[dbo].[Customers].[SSN],0)" />
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
    public void KitchenSink_no_sensitive_token_leaks()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(KitchenSinkPlan(), new ObfuscationMap());
        // Every original sensitive token must be gone.
        Assert.DoesNotContain("Sales", xml);
        Assert.DoesNotContain("dbo", xml);
        Assert.DoesNotContain("Customers", xml);
        Assert.DoesNotContain("SSN", xml);
        Assert.DoesNotContain("LinkedSrv", xml);
        Assert.DoesNotContain("GetCustomer", xml);
        Assert.DoesNotContain("FmtSSN", xml);
        Assert.DoesNotContain("123-45-6789", xml);
        Assert.DoesNotContain("SecretSrv", xml);
        Assert.DoesNotContain("SecretCursor", xml);
        Assert.DoesNotContain("SecretGuide", xml);
        Assert.DoesNotContain("SecretGuideDb", xml);
        Assert.DoesNotContain("SecretTmpl", xml);
        Assert.DoesNotContain("SecretTmplDb", xml);
        Assert.DoesNotContain("SecretAsm", xml);
        Assert.DoesNotContain("SecretMethod", xml);
        Assert.DoesNotContain("SecretUdx", xml);
        Assert.DoesNotContain("SecretSrv2", xml);
        // Whitelisted system/internal objects must survive intact.
        Assert.Contains("[sys]", xml);
        Assert.Contains("indexes", xml);
        Assert.Contains("Worktable", xml);
        // Output must remain well-formed XML (SSMS-openable).
        var ex = Record.Exception((Action)(() => System.Xml.Linq.XDocument.Parse(xml)));
        Assert.Null(ex);
    }

    [Fact]
    public void Cost_and_hash_attributes_are_not_modified()
    {
        // Non-sensitive attributes (costs, cardinalities, op names, hashes) must not be touched.
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1.5"
                 QueryHash="0xABCD1234" QueryPlanHash="0xDEADBEEF" StatementType="SELECT">
            <Object Database="[Sales]" Schema="[dbo]" Table="[Customers]" />
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.Contains("PhysicalOp=\"Index Seek\"", xml);
        Assert.Contains("EstimateRows=\"1.5\"", xml);
        Assert.Contains("QueryHash=\"0xABCD1234\"", xml);
        Assert.Contains("StatementType=\"SELECT\"", xml);
        // Names are obfuscated.
        Assert.DoesNotContain("Sales", xml);
        Assert.DoesNotContain("Customers", xml);
    }

    // DDL-defined name leak tests (no operator-tree element names the defined object).
    private static string DdlPlan(string statementText) => $"""
    <ShowPlanXML xmlns="{Ns}">
      <StmtSimple StatementText="{statementText}">
      </StmtSimple>
    </ShowPlanXML>
    """;

    [Fact]
    public void Ddl_CreateProc_name_does_not_leak()
    {
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE PROC [dbo].[SecretProc](@p int) AS SELECT 1"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretProc", xml);
    }

    [Fact]
    public void Ddl_CreateView_name_does_not_leak()
    {
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE VIEW [dbo].[SecretView] AS SELECT 1"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretView", xml);
    }

    [Fact]
    public void Ddl_CreateFunction_name_does_not_leak()
    {
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE FUNCTION [dbo].[SecretFn]() RETURNS int AS BEGIN RETURN 1 END"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretFn", xml);
    }

    [Fact]
    public void Ddl_AlterProcedure_name_does_not_leak()
    {
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("ALTER PROCEDURE [dbo].[SecretProc2] AS SELECT 1"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretProc2", xml);
    }

    [Fact]
    public void Ddl_idempotency_preserved()
    {
        var input = DdlPlan("CREATE PROC [dbo].[SecretProc](@p int) AS SELECT 1");
        var (once, _) = PlanObfuscator.Obfuscate(input, new ObfuscationMap());
        var (twice, _) = PlanObfuscator.Obfuscate(once, new ObfuscationMap());
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Ddl_output_is_well_formed_xml()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE PROC [dbo].[SecretProc](@p int) AS SELECT 1"),
            new ObfuscationMap());
        var ex = Record.Exception((Action)(() => System.Xml.Linq.XDocument.Parse(xml)));
        Assert.Null(ex);
    }
}
