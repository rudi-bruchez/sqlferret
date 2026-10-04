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

    private static PlanFinding Warning(IReadOnlyList<PlanFinding> f, string name) =>
        Assert.Single(f, x => x.Kind == "plan_warning" && x.DetailJson.Contains($"\"warning\":\"{name}\""));

    // Les formes qui suivent viennent de plans réels (SET STATISTICS XML, SQL Server 2025),
    // identifiants remplacés. Le moteur écrit PlanAffectingConvert sous QueryPlan/Warnings,
    // pas sous un opérateur : mesuré sur 158 plans réels, sans exception.
    [Fact]
    public void Plan_warning_reads_statement_level_plan_affecting_convert()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <Warnings><PlanAffectingConvert ConvertIssue="Cardinality Estimate"
                                            Expression="CONVERT_IMPLICIT(int,[w].[GadgetCode],0)" /></Warnings>
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="30.4844" />
            """));

        var w = Warning(f, "PlanAffectingConvert");
        Assert.Null(w.NodeId);
        Assert.Contains("ConvertIssue=Cardinality Estimate", w.DetailJson);
    }

    [Fact]
    public void Plan_warning_reads_statement_level_memory_grant_warning()
    {
        var f = Detect(Plan("""SerialDesiredMemory="40" GrantedMemory="526376" MaxUsedMemory="32" """, """
            <Warnings><MemoryGrantWarning GrantWarningKind="Excessive Grant" RequestedMemory="526376"
                                          GrantedMemory="526376" MaxUsedMemory="32" /></Warnings>
            <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="TopN Sort" EstimateRows="63" />
            """));

        var w = Warning(f, "MemoryGrantWarning");
        Assert.Null(w.NodeId);
        Assert.Contains("GrantWarningKind=Excessive Grant", w.DetailJson);
    }

    // NoJoinPredicate est un attribut de <Warnings>, sans enfant.
    [Fact]
    public void Plan_warning_reads_no_join_predicate_attribute()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <RelOp NodeId="0" PhysicalOp="Top" LogicalOp="Top" EstimateRows="10">
              <Top RowCount="0" IsPercent="0" WithTies="0">
                <RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10"
                       EstimateRowsWithoutRowGoal="1e+06">
                  <OutputList />
                  <Warnings NoJoinPredicate="1"></Warnings>
                </RelOp>
              </Top>
            </RelOp>
            """));

        Assert.Equal(1, Warning(f, "NoJoinPredicate").NodeId);
    }

    // UnmatchedIndexes="1" sur QueryPlan/Warnings ; l'index filtré que le paramétrage a
    // rendu inutilisable est nommé dans l'élément frère QueryPlan/UnmatchedIndexes.
    [Fact]
    public void Plan_warning_names_the_unmatched_filtered_index()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <UnmatchedIndexes><Parameterization>
              <Object Database="[AppDb]" Schema="[AppSchema]" Table="[WidgetRecalc]" Index="[IX_WidgetRecalc_Filtered]" />
            </Parameterization></UnmatchedIndexes>
            <Warnings UnmatchedIndexes="1"></Warnings>
            <RelOp NodeId="0" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="225.226" />
            """));

        var w = Warning(f, "UnmatchedIndexes");
        Assert.Null(w.NodeId);
        Assert.Contains("AppDb.AppSchema.WidgetRecalc.IX_WidgetRecalc_Filtered", w.DetailJson);
    }

    // ColumnsWithNoStatistics n'a aucun attribut : la colonne est dans un ColumnReference.
    [Fact]
    public void Plan_warning_names_the_column_without_statistics()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <RelOp NodeId="3" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="31.6228">
              <OutputList />
              <Warnings><ColumnsWithNoStatistics>
                <ColumnReference Database="[AppDb]" Schema="[AppSchema]" Table="[WidgetRecalc]" Alias="[w]" Column="GadgetCode" />
              </ColumnsWithNoStatistics></Warnings>
            </RelOp>
            """));

        var w = Warning(f, "ColumnsWithNoStatistics");
        Assert.Equal(3, w.NodeId);
        Assert.Contains("AppDb.AppSchema.WidgetRecalc.GadgetCode", w.DetailJson);
    }

    [Fact]
    public void Plan_warning_ignores_a_false_warning_attribute()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10">
              <Warnings NoJoinPredicate="0"></Warnings>
            </RelOp>
            """));

        Assert.False(Has(f, "plan_warning"));
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

    // Forme d'un plan réel (SQL Server 2025, boucle imbriquée forcée sur 1000 lignes
    // externes) : le Seek interne est estimé à 1 ligne PAR EXÉCUTION, 999 rebinds estimés,
    // et rend 1000 lignes cumulées sur 1000 exécutions. L'estimation est exacte ; comparer
    // 1 à 1000 en ferait une sous-estimation d'un facteur 1000.
    [Fact]
    public void Cardinality_scales_the_estimate_by_estimated_executions()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="963"
                   EstimateRebinds="0" EstimateRewinds="0" EstimatedExecutionMode="Row">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="1000" ActualEndOfScans="1" ActualExecutions="1" ActualExecutionMode="Row" />
              </RunTimeInformation>
              <NestedLoops Optimized="0" WithUnorderedPrefetch="1">
                <RelOp NodeId="2" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="1000"
                       EstimateRebinds="0" EstimateRewinds="0" EstimatedExecutionMode="Row">
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="0" ActualRows="1000" ActualEndOfScans="1" ActualExecutions="1" ActualExecutionMode="Row" />
                  </RunTimeInformation>
                </RelOp>
                <RelOp NodeId="3" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1"
                       EstimateRebinds="999" EstimateRewinds="0" EstimatedExecutionMode="Row">
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="0" ActualRows="1000" ActualEndOfScans="0" ActualExecutions="1000" ActualExecutionMode="Row" />
                  </RunTimeInformation>
                </RelOp>
              </NestedLoops>
            </RelOp>
            """));

        Assert.False(Has(f, "cardinality_misestimate"));
    }

    // Même forme, mais chaque exécution interne rend 50 lignes au lieu d'une : 50 000 contre
    // 1000 estimées au total. L'écart réel reste visible une fois les exécutions comptées.
    [Fact]
    public void Cardinality_still_fires_on_a_real_gap_on_the_inner_side()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <RelOp NodeId="3" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1"
                   EstimateRebinds="999" EstimateRewinds="0" EstimatedExecutionMode="Row">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualEndOfScans="0" ActualExecutions="1000" ActualExecutionMode="Row" />
              </RunTimeInformation>
            </RelOp>
            """));

        var d = Assert.Single(f, x => x.Kind == "cardinality_misestimate").DetailJson;
        Assert.Contains("\"estimate_executions\":1000", d);
        Assert.Contains("\"ratio\":50}", d);
    }

    // Les rewinds comptent autant que les rebinds : un spool rejoué 100 fois rend 100 fois
    // ses lignes.
    [Fact]
    public void Cardinality_counts_rewinds_as_executions()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, """
            <RelOp NodeId="7" PhysicalOp="Table Spool" LogicalOp="Lazy Spool" EstimateRows="10"
                   EstimateRebinds="0" EstimateRewinds="99" EstimatedExecutionMode="Row">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="1000" ActualRebinds="1" ActualRewinds="99" ActualExecutions="100" />
              </RunTimeInformation>
            </RelOp>
            """));

        Assert.False(Has(f, "cardinality_misestimate"));
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

    // Attributs d'instruction et de QueryPlan maîtrisés par le test : les règles de
    // compilation lisent StmtSimple et QueryPlan, que Plan() fige.
    private static XDocument Stmt(string stmtAttrs, string queryPlanAttrs, string body = "") => XDocument.Parse($"""
        <ShowPlanXML xmlns="{Ns}">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementType="SELECT" {stmtAttrs}>
              <QueryPlan {queryPlanAttrs}>
                {body}
                <RelOp NodeId="0" PhysicalOp="Compute Scalar" LogicalOp="Compute Scalar" EstimateRows="1" />
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """);

    // Le schéma showplan énumère trois valeurs pour StatementOptmEarlyAbortReason :
    // TimeOut, MemoryLimitExceeded et GoodEnoughPlanFound. La troisième est l'issue
    // normale d'une optimisation, pas un abandon (28 plans sur 101 la portent).
    [Theory]
    [InlineData("TimeOut")]
    [InlineData("MemoryLimitExceeded")]
    public void Optimizer_early_abort_fires_on_timeout_and_memory_limit(string reason)
    {
        var f = Detect(Stmt($"""StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="{reason}" StatementSubTreeCost="42.5" """,
                            "DegreeOfParallelism=\"1\""));

        var a = Assert.Single(f, x => x.Kind == "optimizer_early_abort");
        Assert.Null(a.NodeId);
        Assert.Contains($"\"reason\":\"{reason}\"", a.DetailJson);
        Assert.Contains("\"statement_subtree_cost\":42.5", a.DetailJson);
    }

    [Fact]
    public void Optimizer_early_abort_ignores_good_enough_plan_found()
    {
        var f = Detect(Stmt("""StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="GoodEnoughPlanFound" """,
                            "DegreeOfParallelism=\"1\""));

        Assert.False(Has(f, "optimizer_early_abort"));
    }

    // Une UDF scalaire non inlinable interdit le parallélisme à une requête dont le coût
    // aurait justifié un plan parallèle : c'est le code qu'il faut changer.
    [Fact]
    public void Non_parallel_plan_fires_on_a_code_reason_above_the_cost_threshold()
    {
        var f = Detect(Stmt("""StatementSubTreeCost="27.3" """,
                            """DegreeOfParallelism="0" NonParallelPlanReason="TSQLUserDefinedFunctionsNotParallelizable" """));

        var n = Assert.Single(f, x => x.Kind == "non_parallel_plan");
        Assert.Null(n.NodeId);
        Assert.Contains("\"reason\":\"TSQLUserDefinedFunctionsNotParallelizable\"", n.DetailJson);
        Assert.Contains("\"statement_subtree_cost\":27.3", n.DetailJson);
    }

    // Sous le seuil de coût, le moteur n'aurait pas envisagé de plan parallèle de toute
    // façon : la raison est exacte et sans conséquence.
    [Fact]
    public void Non_parallel_plan_is_silent_below_the_cost_threshold()
    {
        var f = Detect(Stmt("""StatementSubTreeCost="0.185" """,
                            """DegreeOfParallelism="0" NonParallelPlanReason="TableVariableTransactionsDoNotSupportParallelNestedTransaction" """));

        Assert.False(Has(f, "non_parallel_plan"));
    }

    // MAXDOP 1 dit une configuration, pas un choix de code.
    [Fact]
    public void Non_parallel_plan_ignores_a_configuration_reason()
    {
        var f = Detect(Stmt("""StatementSubTreeCost="500" """,
                            """DegreeOfParallelism="0" NonParallelPlanReason="MaxDOPSetToOne" """));

        Assert.False(Has(f, "non_parallel_plan"));
    }

    // TraceFlags apparaît jusqu'à deux fois sous QueryPlan : la liste de compilation
    // (IsCompileTime="1") et celle de l'exécution. Seule la première a façonné le plan.
    [Fact]
    public void Trace_flag_reports_compile_time_flags_only()
    {
        var f = Detect(Stmt("""StatementSubTreeCost="1" """, "DegreeOfParallelism=\"1\"", """
            <TraceFlags IsCompileTime="1">
              <TraceFlag Value="9130" Scope="Session" />
            </TraceFlags>
            <TraceFlags IsCompileTime="0">
              <TraceFlag Value="9130" Scope="Session" />
              <TraceFlag Value="7412" Scope="Global" />
            </TraceFlags>
            """));

        var t = Assert.Single(f, x => x.Kind == "trace_flag");
        Assert.Null(t.NodeId);
        Assert.Contains("\"value\":9130", t.DetailJson);
        Assert.Contains("\"scope\":\"Session\"", t.DetailJson);
    }

    private static string Threads(params long[] rowsFromThread1) => string.Join("\n",
        rowsFromThread1.Select((r, i) =>
            $"""<RunTimeCountersPerThread Thread="{i + 1}" ActualRows="{r}" ActualEndOfScans="1" ActualExecutions="1" />"""));

    // DOP 4, un thread porte presque tout : 40 000 lignes sur 43 000, moyenne 10 750,
    // maximum à 3,7 fois la moyenne.
    [Fact]
    public void Parallel_thread_skew_fires_when_one_thread_carries_the_rows()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, $"""
            <RelOp NodeId="4" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="43000" Parallel="1">
              <RunTimeInformation>
                {Threads(40000, 1000, 1000, 1000)}
              </RunTimeInformation>
            </RelOp>
            """));

        var s = Assert.Single(f, x => x.Kind == "parallel_thread_skew");
        Assert.Equal(4, s.NodeId);
        Assert.Contains("\"threads\":4", s.DetailJson);
        Assert.Contains("\"max_thread_rows\":40000", s.DetailJson);
        Assert.Contains("\"ratio\":3.72", s.DetailJson);
    }

    // Le thread 0 est le coordinateur. Sous un opérateur de la zone parallèle, il porte un
    // compteur à zéro qui ferait baisser la moyenne : 18 000 contre 9 750 de moyenne reste
    // sous le rapport 2, mais 18 000 contre 7 800 en comptant le thread 0 le dépasserait.
    [Fact]
    public void Parallel_thread_skew_excludes_the_coordinator_thread()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, $"""
            <RelOp NodeId="4" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="39000" Parallel="1">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="0" ActualEndOfScans="0" ActualExecutions="0" />
                {Threads(18000, 7000, 7000, 7000)}
              </RunTimeInformation>
            </RelOp>
            """));

        Assert.False(Has(f, "parallel_thread_skew"));
    }

    // Sur un Gather Streams, le thread 0 reçoit toutes les lignes des producteurs. Le
    // compter doublerait le total et ferait passer le plancher à un opérateur qui ne
    // traite que 6 000 lignes en parallèle.
    [Fact]
    public void Parallel_thread_skew_floor_ignores_the_coordinator_rows()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, $"""
            <RelOp NodeId="1" PhysicalOp="Parallelism" LogicalOp="Gather Streams" EstimateRows="6000" Parallel="1">
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="6000" ActualEndOfScans="1" ActualExecutions="1" />
                {Threads(6000, 0, 0, 0)}
              </RunTimeInformation>
            </RelOp>
            """));

        Assert.False(Has(f, "parallel_thread_skew"));
    }

    // Tout sur un thread, mais 5 000 lignes : sans conséquence.
    [Fact]
    public void Parallel_thread_skew_is_silent_below_the_row_floor()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, $"""
            <RelOp NodeId="4" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="5000" Parallel="1">
              <RunTimeInformation>
                {Threads(5000, 0, 0, 0)}
              </RunTimeInformation>
            </RelOp>
            """));

        Assert.False(Has(f, "parallel_thread_skew"));
    }

    // La règle ne lit que les opérateurs Parallel="1". Forme synthétique : sur les plans
    // mesurés, un opérateur série ne porte jamais plus d'un thread non nul, et la règle
    // s'y tairait de toute façon. Le test fixe le contrat, pas un cas observé.
    [Fact]
    public void Parallel_thread_skew_ignores_a_serial_operator()
    {
        var f = Detect(Plan("""SerialDesiredMemory="0" GrantedMemory="0" MaxUsedMemory="0" """, $"""
            <RelOp NodeId="4" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="43000" Parallel="0">
              <RunTimeInformation>
                {Threads(40000, 1000, 1000, 1000)}
              </RunTimeInformation>
            </RelOp>
            """));

        Assert.False(Has(f, "parallel_thread_skew"));
    }
}
