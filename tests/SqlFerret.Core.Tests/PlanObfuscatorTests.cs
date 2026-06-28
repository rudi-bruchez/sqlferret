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
        Assert.Contains("@Param1", xml);  // token carries the '@' sigil (prefix "@Param")
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

    // Regression: SQL Server TRUNCATES StatementText for large procs (cut off mid-body).
    // ScriptDom cannot parse the incomplete statement, so the AST collector silently skips it.
    // The regex fallback must extract the defined name from the leading CREATE clause anyway.
    [Fact]
    public void Ddl_truncated_StatementText_schema_qualified_name_does_not_leak()
    {
        // No closing END — ScriptDom will fail to parse this; only the regex fallback saves it.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE Proc dbo.SecretTruncProc ( @p char(9) , @q char ) As Begin DECLARE @x int IF @@TRANCOUNT = 0"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretTruncProc", xml);
    }

    [Fact]
    public void Ddl_truncated_StatementText_bare_name_does_not_leak()
    {
        // No schema, no brackets, truncated body — regex fallback must still catch the bare name.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE PROC SecretBare ( @a char(11) ) AS BEGIN IF @@TRANCOUNT = 0"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretBare", xml);
    }

    // FIX 1: comment-before-real-clause leak.
    // A banner comment may itself contain a CREATE PROC clause (e.g. a template header).
    // Without comment stripping the regex maps the name inside the comment (FakeName) and
    // misses the real defined name (RealSecret) which then leaks.
    [Fact]
    public void Ddl_comment_before_real_clause_RealSecret_does_not_leak()
    {
        // The comment mentions FakeName in a CREATE PROC clause; the real defined name follows after.
        // ScriptDom cannot parse the truncated body; the regex fallback must strip comments first.
        const string stmtText =
            "/* CREATE PROC dbo.FakeName ... banner ... */ CREATE PROC dbo.RealSecret ( @p char(9) ) AS BEGIN IF @@TRANCOUNT = 0";
        var xml = PlanObfuscator.Obfuscate(DdlPlan(stmtText), new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("RealSecret", xml);
    }

    // FIX 2: TABLE, SYNONYM, SEQUENCE, TYPE not covered by original regex.
    [Fact]
    public void Ddl_CreateTable_truncated_name_does_not_leak()
    {
        // Truncated CREATE TABLE: ScriptDom cannot parse; regex fallback must catch TABLE.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE TABLE dbo.SecretTable ("),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretTable", xml);
    }

    [Fact]
    public void Ddl_CreateSynonym_name_does_not_leak()
    {
        // SYNONYM: DdlNameVisitor has no handler; regex fallback is the only safety net.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE SYNONYM dbo.SecretSyn FOR dbo.RealTable"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretSyn", xml);
    }

    // FIX 3: temp-table (#) and double-quoted identifier name forms.
    [Fact]
    public void Ddl_truncated_TempProc_name_does_not_leak()
    {
        // #-prefixed temp object in a truncated (unparseable) DDL statement.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE PROC #SecretTemp ( @p int ) AS BEGIN IF @@TRANCOUNT = 0"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("SecretTemp", xml);
    }

    [Fact]
    public void Ddl_truncated_DoubleQuotedProc_name_does_not_leak()
    {
        // Double-quoted identifier: &quot; in the XML attribute decodes to " in the value.
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <StmtSimple StatementText="CREATE PROC dbo.&quot;SecretQuoted&quot; ( @p int ) AS BEGIN IF @@TRANCOUNT = 0">
          </StmtSimple>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.DoesNotContain("SecretQuoted", xml);
    }

    // ─── Temp-table obfuscation tests ────────────────────────────────────────

    // Fixture: one mangled form + one clean form of the same temp table, plus a column ref and SQL text.
    private static string TempTablePlan() => $"""
    <ShowPlanXML xmlns="{Ns}">
      <StmtSimple StatementText="SELECT Sensitive FROM #Secret">
        <RelOp>
          <Object Database="[tempdb]" Schema="[dbo]" Table="[#Secret_______________________________0000ABCD]" />
          <Object Database="[tempdb]" Schema="[dbo]" Table="[#Secret]" />
          <ColumnReference Database="[tempdb]" Schema="[dbo]" Table="[#Secret]" Column="Sensitive" />
        </RelOp>
      </StmtSimple>
    </ShowPlanXML>
    """;

    [Fact]
    public void TempTable_mangled_and_clean_collapse_to_one_token()
    {
        var (xml, _) = PlanObfuscator.Obfuscate(TempTablePlan(), new ObfuscationMap());
        // Sensitive names must not leak.
        Assert.DoesNotContain("Secret", xml);
        Assert.DoesNotContain("Sensitive", xml);
        // One shared token covers mangled + clean operator-tree refs.
        Assert.Contains("#Temp1", xml);
        Assert.Contains("Table=\"[#Temp1]\"", xml);
        // The same token must appear in the rewritten StatementText.
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var stmtText = doc.Descendants()
            .First(e => e.Attribute("StatementText") != null)
            .Attribute("StatementText")!.Value;
        Assert.Contains("#Temp1", stmtText);
    }

    [Fact]
    public void TempTable_global_temp_name_is_obfuscated()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Object Table="[##GlobalSecret]" />
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.DoesNotContain("GlobalSecret", xml);
        Assert.Contains("#Temp1", xml);
    }

    [Fact]
    public void TempTable_worktable_is_preserved()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Object Database="[tempdb]" Table="[Worktable]" />
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.Contains("Worktable", xml);
    }

    [Fact]
    public void TempTable_single_underscore_name_not_over_stripped()
    {
        // #keep_me_raw_name has underscores but not 4+ consecutive trailing ones —
        // NormalizeTempName must not strip any part of it. The whole name maps to a token.
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Object Table="[#keep_me_raw_name]" />
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.DoesNotContain("keep_me_raw_name", xml);
        Assert.Contains("#Temp1", xml);
    }

    [Fact]
    public void TempTable_obfuscation_is_idempotent()
    {
        var (once, _) = PlanObfuscator.Obfuscate(TempTablePlan(), new ObfuscationMap());
        var (twice, _) = PlanObfuscator.Obfuscate(once, new ObfuscationMap());
        Assert.Equal(once, twice);
    }

    // Regression: ScalarString predicate fragments are NOT valid SQL statements,
    // so StatementTextRewriter always hits Fallback for them. The old \b boundary
    // failed before '#' (non-word char), letting #Secret leak verbatim.
    [Fact]
    public void TempTable_scalarstring_predicate_does_not_leak_in_fallback()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Object Database="[tempdb]" Schema="[dbo]" Table="[#Secret]" />
            <ColumnReference Database="[tempdb]" Schema="[dbo]" Table="[#Secret]" Column="ColX" />
            <Predicate>
              <ScalarOperator ScalarString="[tempdb].[dbo].[#Secret].[ColX]='v'">
              </ScalarOperator>
            </Predicate>
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        // Primary bug: #Secret must be gone from the ScalarString fallback rewrite.
        Assert.DoesNotContain("#Secret", xml);
        // Column name from the map must also be gone.
        Assert.DoesNotContain("ColX", xml);
        Assert.Contains("#Temp1", xml);
    }

    // ─── DDL-only temp-table leak regression tests ───────────────────────────

    // Bug: CREATE TABLE #x (...) with NO operator-tree Object element leaked #x verbatim.
    // MapDdlMultiPartName stripped the '#' and stored the name as NameKind.Table with key "x".
    // The rewriter keyed '#x' (Strip does not remove '#'), so the lookup missed and '#x' was emitted.
    [Fact]
    public void Ddl_CreateTable_LocalTemp_DDL_only_does_not_leak()
    {
        // No operator-tree Object element names this table — DDL path only.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE TABLE #SecretProbe (CustomerSsn int)"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("#SecretProbe", xml);
        Assert.DoesNotContain("SecretProbe", xml);
    }

    [Fact]
    public void Ddl_CreateTable_LocalTemp_shares_token_with_operator_tree()
    {
        // #Shared appears in both the StatementText DDL and the operator-tree Object.
        // Both paths must produce the same single #Temp1 token (not two separate tokens).
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <StmtSimple StatementText="CREATE TABLE #Shared (col int)">
            <RelOp>
              <Object Database="[tempdb]" Schema="[dbo]" Table="[#Shared]" />
            </RelOp>
          </StmtSimple>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.DoesNotContain("#Shared", xml);
        Assert.Contains("#Temp1", xml);
        Assert.DoesNotContain("#Temp2", xml); // one name → one token, not two
    }

    [Fact]
    public void Ddl_CreateTable_GlobalTemp_does_not_leak()
    {
        // Global temp table (##) in DDL-only StatementText must also be obfuscated.
        var xml = PlanObfuscator.Obfuscate(
            DdlPlan("CREATE TABLE ##GShared (id int)"),
            new ObfuscationMap()).AnonXml;
        Assert.DoesNotContain("GShared", xml);
    }

    // ─── @parameter / @local-variable obfuscation tests ─────────────────────

    // @NUMCLT appears in both the operator tree (Column attr) and a parseable StatementText.
    // Both must map to the same @Param1 token.
    [Fact]
    public void Parameter_in_tree_and_parseable_StatementText_are_consistently_mapped()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <StmtSimple StatementText="SELECT 1 WHERE x=@NUMCLT">
            <RelOp>
              <ColumnReference Column="@NUMCLT" />
            </RelOp>
          </StmtSimple>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.DoesNotContain("NUMCLT", xml);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var colToken = doc.Descendants()
            .First(e => e.Attribute("Column") != null)
            .Attribute("Column")!.Value;
        var stmtText = doc.Descendants()
            .First(e => e.Attribute("StatementText") != null)
            .Attribute("StatementText")!.Value;
        Assert.StartsWith("@Param", colToken);
        Assert.Contains(colToken, stmtText);  // same token in both
    }

    // @ErrCode only in parseable StatementText (DECLARE + SET), not in operator tree.
    [Fact]
    public void Local_variable_in_parseable_StatementText_is_scrubbed()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <StmtSimple StatementText="DECLARE @ErrCode int SET @ErrCode=0">
          </StmtSimple>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        Assert.DoesNotContain("ErrCode", xml);
    }

    // @@TRANCOUNT and @@ERROR are T-SQL system globals and must survive obfuscation intact.
    [Fact]
    public void System_globals_are_preserved_in_StatementText()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <StmtSimple StatementText="IF @@TRANCOUNT &gt; 0 PRINT @@ERROR">
          </StmtSimple>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var stmtText = doc.Descendants()
            .First(e => e.Attribute("StatementText") != null)
            .Attribute("StatementText")!.Value;
        Assert.Contains("@@TRANCOUNT", stmtText);
        Assert.Contains("@@ERROR", stmtText);
    }

    // @NUMLME in a ScalarString predicate fragment (not parseable as a statement) —
    // the fallback path must scrub it.
    [Fact]
    public void Parameter_in_unparseable_ScalarString_is_scrubbed()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Predicate>
              <ScalarOperator ScalarString="[a]=@NUMLME @@@">
              </ScalarOperator>
            </Predicate>
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var scalarStr = doc.Descendants()
            .First(e => e.Attribute("ScalarString") != null)
            .Attribute("ScalarString")!.Value;
        Assert.DoesNotContain("NUMLME", scalarStr);
    }

    // @@ROWCOUNT in an unparseable ScalarString — fallback must leave it verbatim.
    [Fact]
    public void System_global_in_unparseable_ScalarString_is_preserved()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Predicate>
              <ScalarOperator ScalarString="@@ROWCOUNT @@@">
              </ScalarOperator>
            </Predicate>
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var scalarStr = doc.Descendants()
            .First(e => e.Attribute("ScalarString") != null)
            .Attribute("ScalarString")!.Value;
        Assert.Contains("@@ROWCOUNT", scalarStr);
    }

    // @@ROWCOUNT in a ColumnReference Column= attribute must be preserved verbatim.
    // Before Fix 1, the code routed any '@'-prefixed Column value to NameKind.Parameter,
    // including @@-prefixed system globals — an invariant violation.
    // The sibling @RealParam column ref IS a user parameter and must still be mapped.
    [Fact]
    public void System_global_in_ColumnReference_Column_attribute_is_preserved()
    {
        var plan = $"""
        <ShowPlanXML xmlns="{Ns}">
          <RelOp>
            <Object Database="[d]" Schema="[s]" Table="[t]" />
            <ColumnReference Database="[d]" Schema="[s]" Table="[t]" Column="@@ROWCOUNT" />
            <ColumnReference Database="[d]" Schema="[s]" Table="[t]" Column="@RealParam" />
          </RelOp>
        </ShowPlanXML>
        """;
        var (xml, _) = PlanObfuscator.Obfuscate(plan, new ObfuscationMap());
        // @@ROWCOUNT must survive intact — it is a system global, not a user parameter.
        Assert.Contains("@@ROWCOUNT", xml);
        // @RealParam must be mapped to a @Param token — it is a user parameter.
        Assert.DoesNotContain("RealParam", xml);
        Assert.Contains("@Param1", xml);
    }
}
