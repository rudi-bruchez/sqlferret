using System.Xml.Linq;
using SqlFerret.Core.Plans;

public class PlanFindingsTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static XDocument Plan(string grantAttrs, string body) => XDocument.Parse($"""
        <ShowPlanXML xmlns="{Ns}">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementType="SELECT" QueryPlanHash="0x1111111111111111">
              <QueryPlan DegreeOfParallelism="8">
                <MemoryGrantInfo {grantAttrs} />
                {body}
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """);

    private static IReadOnlyList<PlanFinding> Detect(XDocument d) =>
        PlanFindings.Detect(d, new PlanFindingThresholds());

    private static bool Has(IReadOnlyList<PlanFinding> f, string kind) => f.Any(x => x.Kind == kind);

    [Fact]
    public void Oversized_grant_fires_on_granted_over_used_ratio()
    {
        var f = Detect(Plan(
            """SerialDesiredMemory="4000" GrantedMemory="20000" MaxUsedMemory="1000" """,
            """<RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />"""));

        Assert.True(Has(f, "memory_grant_oversized"));
        var detail = f.First(x => x.Kind == "memory_grant_oversized").DetailJson;
        Assert.Contains("\"granted_kb\":20000", detail);
        Assert.Contains("\"max_used_kb\":1000", detail);
        Assert.Contains("\"serial_desired_kb\":4000", detail);
    }

    [Fact]
    public void Oversized_grant_fires_on_absolute_desired_even_when_ratio_is_fine()
    {
        // 40 Go désirés, écrêtés à 20 Go, 16,5 Go utilisés : ratio 1,22 sous le seuil,
        // mais la demande absolue reste un défaut. Cas réel de la trace de référence.
        var f = Detect(Plan(
            """SerialDesiredMemory="42225192" GrantedMemory="20193000" MaxUsedMemory="16533000" """,
            """<RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />"""));

        Assert.True(Has(f, "memory_grant_oversized"));
    }

    [Fact]
    public void Reasonable_grant_fires_nothing()
    {
        var f = Detect(Plan(
            """SerialDesiredMemory="2000" GrantedMemory="2000" MaxUsedMemory="1800" """,
            """<RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />"""));

        Assert.False(Has(f, "memory_grant_oversized"));
    }

    // Un grant d'1 Mo surdimensionné d'un facteur 1000 ne coûte rien à personne : le
    // ratio est exact, la conséquence nulle. Sans plancher, la règle noie son propre
    // signal — sur une trace réelle, 8 déclenchements sur 25 portaient sur des grants
    // de 1 à 3 Mo, aux côtés des deux seuls qui comptaient (1,3 Go et 2,0 Go).
    [Theory]
    [InlineData(1024)]     // 1 Mo accordé
    [InlineData(3072)]     // 3 Mo accordé
    [InlineData(16383)]    // juste sous le plancher
    public void Oversized_ratio_is_ignored_on_a_grant_below_the_floor(int grantedKb)
    {
        var f = Detect(Plan(
            $"""SerialDesiredMemory="{grantedKb}" GrantedMemory="{grantedKb}" MaxUsedMemory="1" """,
            """<RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />"""));

        Assert.False(Has(f, "memory_grant_oversized"));
    }

    [Fact]
    public void Oversized_ratio_fires_at_the_floor()
    {
        var f = Detect(Plan(
            """SerialDesiredMemory="16384" GrantedMemory="16384" MaxUsedMemory="1" """,
            """<RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />"""));

        Assert.True(Has(f, "memory_grant_oversized"));
    }

    // Le plancher ne borne que le ratio. Une demande démesurée écrêtée par le moteur
    // reste un défaut, même quand l'accordé final est modeste — c'est précisément le
    // cas que le seuil absolu existe pour attraper.
    [Fact]
    public void The_floor_does_not_suppress_the_absolute_desired_rule()
    {
        var f = Detect(Plan(
            """SerialDesiredMemory="42225192" GrantedMemory="2000" MaxUsedMemory="1900" """,
            """<RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />"""));

        Assert.True(Has(f, "memory_grant_oversized"));
    }

    [Fact]
    public void Large_scan_fires_on_table_cardinality()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="10" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan"
                   EstimateRows="100" TableCardinality="275367000">
              <IndexScan><Object Schema="[AppSchema]" Table="[WidgetRecalc]" Index="[PK_WidgetRecalc]" /></IndexScan>
            </RelOp>
            """));

        Assert.True(Has(f, "large_scan"));
        var d = f.First(x => x.Kind == "large_scan");
        Assert.Equal(10, d.NodeId);
        Assert.Contains("WidgetRecalc", d.DetailJson);
    }

    [Fact]
    public void Small_scan_fires_nothing()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="10" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan"
                   EstimateRows="100" TableCardinality="500" />
            """));

        Assert.False(Has(f, "large_scan"));
    }

    // Forme d'un plan réel (SET STATISTICS XML, SQL Server 2025, tri forcé en
    // débordement par MAX_GRANT_PERCENT = 0) : le moteur écrit SortSpillDetails sous
    // RelOp/Warnings, à côté de SpillToTempDb, jamais comme enfant direct du RelOp.
    [Fact]
    public void Spill_fires_on_sort_spill_details()
    {
        var f = Detect(Plan("""SerialDesiredMemory="69200" GrantedMemory="512" MaxUsedMemory="512" """, """
            <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="200000" Parallel="0"
                   EstimateRebinds="0" EstimateRewinds="0" EstimatedExecutionMode="Row">
              <OutputList />
              <Warnings>
                <SpillToTempDb SpillLevel="2" SpilledThreadCount="1" />
                <SortSpillDetails GrantedMemoryKb="512" UsedMemoryKb="512" WritesToTempDb="4289" ReadsFromTempDb="4289" />
              </Warnings>
              <MemoryFractions Input="1" Output="1" />
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="200000" ActualRebinds="1" ActualRewinds="0"
                                          ActualEndOfScans="1" ActualExecutions="1" ActualExecutionMode="Row" />
              </RunTimeInformation>
            </RelOp>
            """));

        var spill = Assert.Single(f, x => x.Kind == "spill_to_tempdb");
        Assert.Equal(0, spill.NodeId);
        Assert.Contains("\"kind\":\"SortSpillDetails\"", spill.DetailJson);
        Assert.Contains("\"writes_to_tempdb\":4289", spill.DetailJson);
    }

    // Même origine, jointure par hachage forcée en débordement.
    [Fact]
    public void Spill_fires_on_hash_spill_details()
    {
        var f = Detect(Plan("""SerialDesiredMemory="43024" GrantedMemory="1024" MaxUsedMemory="968" """, """
            <RelOp NodeId="0" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="200000" Parallel="0"
                   EstimateRebinds="0" EstimateRewinds="0" EstimatedExecutionMode="Row">
              <OutputList />
              <Warnings>
                <SpillToTempDb SpillLevel="3" SpilledThreadCount="1" />
                <HashSpillDetails GrantedMemoryKb="1024" UsedMemoryKb="968" WritesToTempDb="16048" ReadsFromTempDb="16048" />
              </Warnings>
              <MemoryFractions Input="1" Output="1" />
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="200000" ActualEndOfScans="1" ActualExecutions="1"
                                          ActualExecutionMode="Row" />
              </RunTimeInformation>
            </RelOp>
            """));

        var spill = Assert.Single(f, x => x.Kind == "spill_to_tempdb");
        Assert.Contains("\"kind\":\"HashSpillDetails\"", spill.DetailJson);
        Assert.Contains("\"used_kb\":968", spill.DetailJson);
    }

    [Fact]
    public void Plan_warning_carries_the_warning_element_name()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="3" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="1">
              <Warnings><PlanAffectingConvert ConvertIssue="Cardinality Estimate"
                                              Expression="CONVERT(int,[a].[b])" /></Warnings>
            </RelOp>
            """));

        Assert.True(Has(f, "plan_warning"));
        Assert.Contains("PlanAffectingConvert", f.First(x => x.Kind == "plan_warning").DetailJson);
    }

    [Fact]
    public void Missing_index_fires()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <MissingIndexes>
              <MissingIndexGroup Impact="99.5">
                <MissingIndex Database="[AppDb]" Schema="[AppSchema]" Table="[WidgetRecalc]">
                  <ColumnGroup Usage="EQUALITY"><Column Name="[DependantWidgets]" /></ColumnGroup>
                </MissingIndex>
              </MissingIndexGroup>
            </MissingIndexes>
            <RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="1" />
            """));

        Assert.True(Has(f, "missing_index"));
        Assert.Contains("99.5", f.First(x => x.Kind == "missing_index").DetailJson);
    }

    [Fact]
    public void Row_goal_defeated_fires_on_blocking_operator()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="9" PhysicalOp="Sort" LogicalOp="Sort"
                   EstimateRows="100" EstimateRowsWithoutRowGoal="275367000" />
            """));

        Assert.True(Has(f, "row_goal_defeated"));
        var d = f.First(x => x.Kind == "row_goal_defeated");
        Assert.Equal(9, d.NodeId);
        Assert.Contains("275367000", d.DetailJson);
    }

    [Fact]
    public void Row_goal_defeated_ignores_non_blocking_operators()
    {
        // Un Nested Loops n'est pas bloquant : le row goal y est légitime.
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="3" PhysicalOp="Nested Loops" LogicalOp="Inner Join"
                   EstimateRows="100" EstimateRowsWithoutRowGoal="275367000" />
            """));

        Assert.False(Has(f, "row_goal_defeated"));
    }

    [Fact]
    public void Row_goal_defeated_ignores_blocking_operator_without_row_goal()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="9" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="275367000" />
            """));

        Assert.False(Has(f, "row_goal_defeated"));
    }

    // LE test qui compte : ActualRows est rapporté PAR THREAD. Sans somme, un plan DOP 8
    // dont chaque thread traite 1/8 des lignes serait signalé comme sous-estimation d'un
    // facteur 8. Une fixture DOP 1 ne l'aurait jamais montré.
    [Fact]
    public void Cardinality_sums_actual_rows_across_threads()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="5" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="800">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="1" ActualRows="100" ActualElapsedms="10" ActualCPUms="5" />
                <RunTimeCountersPerThread Thread="2" ActualRows="100" ActualElapsedms="12" ActualCPUms="6" />
                <RunTimeCountersPerThread Thread="3" ActualRows="100" ActualElapsedms="11" ActualCPUms="5" />
                <RunTimeCountersPerThread Thread="4" ActualRows="100" ActualElapsedms="13" ActualCPUms="7" />
                <RunTimeCountersPerThread Thread="5" ActualRows="100" ActualElapsedms="10" ActualCPUms="5" />
                <RunTimeCountersPerThread Thread="6" ActualRows="100" ActualElapsedms="12" ActualCPUms="6" />
                <RunTimeCountersPerThread Thread="7" ActualRows="100" ActualElapsedms="11" ActualCPUms="5" />
                <RunTimeCountersPerThread Thread="8" ActualRows="100" ActualElapsedms="14" ActualCPUms="8" />
              </RunTimeInformation>
            </RelOp>
            """));

        // 8 x 100 = 800 = l'estimation. Rien à signaler.
        Assert.False(Has(f, "cardinality_misestimate"));
    }

    [Fact]
    public void Cardinality_misestimate_fires_on_real_gap()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="5" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="10">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="1" ActualRows="5000" ActualElapsedms="10" ActualCPUms="5" />
                <RunTimeCountersPerThread Thread="2" ActualRows="5000" ActualElapsedms="12" ActualCPUms="6" />
              </RunTimeInformation>
            </RelOp>
            """));

        Assert.True(Has(f, "cardinality_misestimate"));
        var d = f.First(x => x.Kind == "cardinality_misestimate").DetailJson;
        Assert.Contains("\"actual_rows\":10000", d);
        Assert.Contains("\"estimate_rows\":10", d);
    }

    [Fact]
    public void SumActualRows_returns_null_when_no_runtime_information()
    {
        var op = XElement.Parse($"""<RelOp xmlns="{Ns}" NodeId="1" PhysicalOp="Sort" EstimateRows="1" />""");
        Assert.Null(PlanFindings.SumActualRows(op));
    }

    [Fact]
    public void Excessive_rebinds_fires()
    {
        var f = Detect(Plan("""SerialDesiredMemory="1" GrantedMemory="1" MaxUsedMemory="1" """, """
            <RelOp NodeId="12" PhysicalOp="Table-valued function" LogicalOp="Table-valued function"
                   EstimateRows="50" EstimateRebinds="300000" />
            """));

        Assert.True(Has(f, "excessive_rebinds"));
        Assert.Contains("300000", f.First(x => x.Kind == "excessive_rebinds").DetailJson);
    }
}
