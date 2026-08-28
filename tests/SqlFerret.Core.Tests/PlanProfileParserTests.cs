using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Plans;

public class PlanProfileParserTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // Fixture bâtie sur un plan RÉEL : un plan estimé ne porte aucun RunTimeInformation
    // et n'exercerait aucune des règles comparant estimé et réel.
    internal const string RealPlan = $"""
        <ShowPlanXML xmlns="{Ns}" Version="1.564" Build="15.0.4480.2">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT TOP(@p) x FROM T ORDER BY y" StatementId="1"
                        StatementType="SELECT" StatementSubTreeCost="8868.44" StatementEstRows="50000"
                        QueryHash="0x56006EFD9093F347" QueryPlanHash="0x14A792EA4D5BF89A">
              <QueryPlan DegreeOfParallelism="8" CachedPlanSize="232" CompileTime="116">
                <MemoryGrantInfo SerialRequiredMemory="512" SerialDesiredMemory="42225192"
                                 GrantedMemory="20193000" MaxUsedMemory="16533000" />
                <RelOp NodeId="9" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="100"
                       EstimateRowsWithoutRowGoal="275367000" EstimatedTotalSubtreeCost="8834.29">
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="1" ActualRows="60" ActualElapsedms="900" ActualCPUms="400" />
                    <RunTimeCountersPerThread Thread="2" ActualRows="40" ActualElapsedms="800" ActualCPUms="350" />
                  </RunTimeInformation>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    internal static FakeEvent PlanEvent(string xml, long? durationUs = 225_183_000, long? cpuUs = 41_200_000)
    {
        var fields = new Dictionary<string, object?> { ["showplan_xml"] = xml };
        if (durationUs is not null) fields["duration"] = durationUs;
        if (cpuUs is not null) fields["cpu_time"] = cpuUs;
        return new FakeEvent("query_post_execution_plan_profile",
            new DateTime(2026, 8, 4, 13, 5, 56, DateTimeKind.Utc), fields,
            new Dictionary<string, object?>());
    }

    [Fact]
    public void TryParse_reads_the_header()
    {
        var p = PlanProfileParser.TryParse(RealPlan, PlanEvent(RealPlan), new PlanFindingThresholds());

        Assert.NotNull(p);
        Assert.Equal("14A792EA4D5BF89A", p!.PlanHash);
        Assert.Equal("queryplanhash", p.PlanHashSource);
        Assert.Equal(1, p.StatementCount);
        Assert.Equal("56006EFD9093F347", p.QueryHash);
        Assert.Equal("SELECT", p.StatementType);
        Assert.Equal(8, p.Dop);
        Assert.Equal(42_225_192L, p.SerialDesiredMemoryKb);
        Assert.Equal(20_193_000L, p.GrantedMemoryKb);
        Assert.Equal(16_533_000L, p.MaxUsedMemoryKb);
        Assert.Equal(8868.44, p.SubtreeCost!.Value, 2);
        Assert.Equal(50000, p.EstimatedRows!.Value, 2);
    }

    [Fact]
    public void TryParse_carries_event_timestamp_and_metrics()
    {
        var p = PlanProfileParser.TryParse(RealPlan, PlanEvent(RealPlan), new PlanFindingThresholds());

        Assert.Equal(new DateTime(2026, 8, 4, 13, 5, 56, DateTimeKind.Utc), p!.CapturedAt);
        Assert.Equal(225_183_000L, p.DurationUs);
        Assert.Equal(41_200_000L, p.CpuTimeUs);
    }

    [Fact]
    public void TryParse_tolerates_missing_duration_and_cpu()
    {
        var p = PlanProfileParser.TryParse(RealPlan, PlanEvent(RealPlan, null, null),
            new PlanFindingThresholds());

        Assert.NotNull(p);
        Assert.Null(p!.DurationUs);
        Assert.Null(p.CpuTimeUs);
    }

    [Fact]
    public void TryParse_records_statement_text_and_its_length()
    {
        var p = PlanProfileParser.TryParse(RealPlan, PlanEvent(RealPlan), new PlanFindingThresholds());

        Assert.Equal("SELECT TOP(@p) x FROM T ORDER BY y", p!.StatementText);
        Assert.Equal(34, p.StatementTextLength);
    }

    [Fact]
    public void TryParse_returns_null_on_malformed_xml()
    {
        const string broken = "<ShowPlanXML><unclosed>";
        Assert.Null(PlanProfileParser.TryParse(broken, PlanEvent(broken), new PlanFindingThresholds()));
    }
}
