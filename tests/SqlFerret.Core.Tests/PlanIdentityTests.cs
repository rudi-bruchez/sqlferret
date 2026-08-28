using System.Xml.Linq;
using SqlFerret.Core.Plans;

public class PlanIdentityTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Plan(string statements) => $"""
        <ShowPlanXML xmlns="{Ns}" Version="1.564" Build="15.0.4480.2">
          <BatchSequence><Batch><Statements>{statements}</Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    private static string Stmt(string queryHash, string planHash, string runtime = "") => $"""
        <StmtSimple StatementText="SELECT 1" StatementId="1" StatementType="SELECT"
                    StatementSubTreeCost="12.5" StatementEstRows="100"
                    QueryHash="{queryHash}" QueryPlanHash="{planHash}">
          <QueryPlan DegreeOfParallelism="8">
            <MemoryGrantInfo SerialRequiredMemory="512" SerialDesiredMemory="42225192"
                             GrantedMemory="20193000" MaxUsedMemory="16533000" />
            <RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="100"
                   EstimatedTotalSubtreeCost="12.5">{runtime}</RelOp>
          </QueryPlan>
        </StmtSimple>
        """;

    [Fact]
    public void Single_statement_uses_QueryPlanHash_without_0x()
    {
        var (hash, source, count) = PlanIdentity.Compute(
            XDocument.Parse(Plan(Stmt("0x56006EFD9093F347", "0x14A792EA4D5BF89A"))));

        Assert.Equal("14A792EA4D5BF89A", hash);
        Assert.Equal("queryplanhash", source);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Multi_statement_uses_composite_hash_distinct_from_first_alone()
    {
        var single = PlanIdentity.Compute(
            XDocument.Parse(Plan(Stmt("0xAA", "0x1111111111111111"))));
        var multi = PlanIdentity.Compute(XDocument.Parse(Plan(
            Stmt("0xAA", "0x1111111111111111") + Stmt("0xBB", "0x2222222222222222"))));

        Assert.Equal("queryplanhash", single.Source);
        Assert.Equal("multi", multi.Source);
        Assert.Equal(2, multi.StatementCount);
        Assert.NotEqual(single.Hash, multi.Hash);
    }

    // Le hash reste NU dans les trois cas : c'est une clé de jointure, pas un nom de
    // fichier. Le préfixe vit dans FileStem, calculé une seule fois (tâche 6).
    [Fact]
    public void Hash_never_carries_a_file_prefix()
    {
        foreach (var doc in (XDocument[])[
            XDocument.Parse(Plan(Stmt("0xAA", "0x1111111111111111"))),
            XDocument.Parse(Plan(Stmt("0xAA", "0x1111111111111111") + Stmt("0xBB", "0x2222222222222222"))),
            XDocument.Parse(Plan("""<StmtSimple StatementType="SELECT"><QueryPlan /></StmtSimple>"""))])
        {
            var (hash, _, _) = PlanIdentity.Compute(doc);
            Assert.Matches("^[0-9A-F]+$", hash);
        }
    }

    [Fact]
    public void FileStem_prefixes_by_source()
    {
        Assert.Equal("p_ABC", PlanIdentity.FileStem("ABC", "queryplanhash"));
        Assert.Equal("m_ABC", PlanIdentity.FileStem("ABC", "multi"));
        Assert.Equal("c_ABC", PlanIdentity.FileStem("ABC", "content"));
    }

    [Fact]
    public void Multi_statement_hash_is_order_sensitive_and_stable()
    {
        var ab = PlanIdentity.Compute(XDocument.Parse(Plan(
            Stmt("0xAA", "0x1111111111111111") + Stmt("0xBB", "0x2222222222222222"))));
        var ab2 = PlanIdentity.Compute(XDocument.Parse(Plan(
            Stmt("0xAA", "0x1111111111111111") + Stmt("0xBB", "0x2222222222222222"))));
        var ba = PlanIdentity.Compute(XDocument.Parse(Plan(
            Stmt("0xBB", "0x2222222222222222") + Stmt("0xAA", "0x1111111111111111"))));

        Assert.Equal(ab.Hash, ab2.Hash);
        Assert.NotEqual(ab.Hash, ba.Hash);
    }

    [Fact]
    public void No_QueryPlanHash_falls_back_to_content_hash()
    {
        const string noHash = """
            <StmtSimple StatementText="SELECT 1" StatementId="1" StatementType="SELECT">
              <QueryPlan><RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" /></QueryPlan>
            </StmtSimple>
            """;
        var (hash, source, _) = PlanIdentity.Compute(XDocument.Parse(Plan(noHash)));

        Assert.Equal("content", source);
        Assert.Matches("^[0-9A-F]{16}$", hash);
    }

    // LE test qui compte : deux exécutions du même plan diffèrent par leurs compteurs
    // d'exécution. Sans normalisation, le hash de contenu changerait à chaque exécution
    // et le dédoublonnage cesserait de fonctionner exactement là où il est le seul recours.
    [Fact]
    public void Content_hash_ignores_runtime_counters()
    {
        const string noHashTemplate = """
            <StmtSimple StatementText="SELECT 1" StatementId="1" StatementType="SELECT">
              <QueryPlan>
                <MemoryGrantInfo SerialRequiredMemory="512" SerialDesiredMemory="1024"
                                 GrantedMemory="{0}" MaxUsedMemory="{1}" />
                <RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="100">
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="1" ActualRows="{2}" ActualElapsedms="{3}" />
                  </RunTimeInformation>
                </RelOp>
                <QueryTimeStats CpuTime="{4}" ElapsedTime="{5}" />
              </QueryPlan>
            </StmtSimple>
            """;

        var run1 = PlanIdentity.Compute(XDocument.Parse(Plan(
            string.Format(noHashTemplate, 1000, 900, 50, 12, 8, 20))));
        var run2 = PlanIdentity.Compute(XDocument.Parse(Plan(
            string.Format(noHashTemplate, 4000, 3900, 999999, 45000, 900, 47000))));

        Assert.Equal("content", run1.Source);
        Assert.Equal(run1.Hash, run2.Hash);
    }
}
