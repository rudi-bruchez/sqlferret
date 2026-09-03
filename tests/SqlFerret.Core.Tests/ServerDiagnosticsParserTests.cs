// tests/SqlFerret.Core.Tests/ServerDiagnosticsParserTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class ServerDiagnosticsParserTests
{
    private static readonly DateTime Ts = new(2026, 9, 3, 8, 26, 34, DateTimeKind.Utc);

    [Fact]
    public void Query_processing_reads_scalars_waits_cpu_and_pending_tasks()
    {
        const string xml = """
        <queryProcessing maxWorkers="9600" workersCreated="2574" workersIdle="1883"
                         pendingTasks="3" oldestPendingTaskWaitingTime="1500"
                         trackingNonYieldingScheduler="0x0">
          <topWaits>
            <nonPreemptive>
              <byCount>
                <wait waitType="CXPACKET" waits="1000" averageWaitTime="2" maxWaitTime="5026"/>
              </byCount>
            </nonPreemptive>
          </topWaits>
          <cpuIntensiveRequests>
            <request sessionId="53" requestId="0" command="SELECT" cpuTimeMs="1200"
                     cpuUtilization="12.5" taskAddress="0x1F"/>
          </cpuIntensiveRequests>
          <pendingTasks><entryPoint name="WidgetRecalc" count="3"/></pendingTasks>
          <blockingTasks/>
        </queryProcessing>
        """;
        var s = ServerDiagnosticsParser.TryParse("QUERY_PROCESSING", "CLEAN", xml, Ts);

        Assert.Equal(DiagnosticsOutcome.Parsed, s.Outcome);
        Assert.Equal(9600L, s.Metrics.Single(m => m.Name == "maxWorkers").ValueBig);
        // Scalaire de duree : converti, et renomme pour que l'unite voyage avec la valeur — une
        // ligne cle/valeur ne peut pas porter un suffixe de colonne.
        Assert.Equal(1_500_000L, s.Metrics.Single(m => m.Name == "oldestPendingTaskWaitingTimeUs").ValueBig);
        Assert.DoesNotContain(s.Metrics, m => m.Name == "oldestPendingTaskWaitingTime");
        // Scalaire non numerique : conserve en texte plutot que perdu.
        Assert.Equal("0x0", s.Metrics.Single(m => m.Name == "trackingNonYieldingScheduler").ValueText);

        var w = Assert.Single(s.Waits);
        Assert.Equal("CXPACKET", w.WaitType);
        Assert.False(w.Preemptive);
        Assert.Equal("byCount", w.Ranking);
        Assert.Equal(2_000L, w.AvgWaitUs);      // ms -> us
        Assert.Equal(5_026_000L, w.MaxWaitUs);

        Assert.Equal(1_200_000L, Assert.Single(s.CpuRequests).CpuTimeUs);
        Assert.Equal("0x1F", s.CpuRequests[0].TaskAddress);
        // Spec §7 : `command` est une CLASSE de commande, pas du texte d'instruction, et c'est la
        // raison pour laquelle il est stocke tel quel sans passer par le sanitizer. Epingler la
        // forme ici : si une version future de SQL Server y met du SQL, le test tombe et la
        // decision est reexaminee au lieu d'etre absorbee en silence.
        Assert.Equal("SELECT", s.CpuRequests[0].Command);
        Assert.DoesNotContain(" FROM ", s.CpuRequests[0].Command!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("WidgetRecalc", s.PendingTasks[0].EntryPoint);
        Assert.Equal(3L, s.PendingTasks[0].TaskCount);
        Assert.Empty(s.EmbeddedBlockingXml);
    }

    [Fact]
    public void Io_subsystem_converts_the_pending_request_duration()
    {
        const string xml = """
        <ioSubsystem totalLongIos="7" intervalLongIos="2" ioLatchTimeouts="0">
          <longestPendingRequests>
            <pendingRequest duration="900" filePath="X:/AppDb/AppDb.mdf" handle="0x5" offset="4096"/>
          </longestPendingRequests>
        </ioSubsystem>
        """;
        var s = ServerDiagnosticsParser.TryParse("IO_SUBSYSTEM", "CLEAN", xml, Ts);

        Assert.Equal(DiagnosticsOutcome.Parsed, s.Outcome);
        Assert.Equal(900_000L, Assert.Single(s.PendingIo).DurationUs);
        Assert.Equal(4096L, s.PendingIo[0].OffsetBytes);
    }

    [Fact]
    public void Resource_reads_memory_report_entries()
    {
        const string xml = """
        <resource isAnyPoolOutOfMemory="0" outOfMemoryExceptions="0" processOutOfMemoryPeriod="0">
          <memoryReport name="Process/System Counts" unit="Value">
            <entry description="Available Physical Memory" value="8589934592"/>
          </memoryReport>
        </resource>
        """;
        var s = ServerDiagnosticsParser.TryParse("RESOURCE", "CLEAN", xml, Ts);

        var e = Assert.Single(s.MemoryEntries);
        Assert.Equal("Process/System Counts", e.ReportName);
        Assert.Equal("Value", e.Unit);
        Assert.Equal("Available Physical Memory", e.Description);
        Assert.Equal(8589934592d, e.ValueNum);
    }

    [Fact]
    public void Blocking_tasks_are_handed_back_as_raw_documents()
    {
        const string xml = """
        <queryProcessing maxWorkers="9600">
          <blockingTasks>
            <blocked-process-report monitorLoop="11881">
              <blocked-process><process spid="61"><inputbuf>exec AppSchema.WidgetRecalc</inputbuf></process></blocked-process>
              <blocking-process><process spid="72"><inputbuf>update AppSchema.Widget</inputbuf></process></blocking-process>
            </blocked-process-report>
          </blockingTasks>
        </queryProcessing>
        """;
        var s = ServerDiagnosticsParser.TryParse("QUERY_PROCESSING", "WARNING", xml, Ts);

        Assert.Single(s.EmbeddedBlockingXml);
        Assert.Contains("blocked-process-report", s.EmbeddedBlockingXml[0]);
    }

    // `events` est un cinquieme composant documente, et une instance Always On emet une ligne par
    // groupe de disponibilite, nommee d'apres le groupe. Ni l'un ni l'autre n'est un echec.
    [Theory]
    [InlineData("events")]
    [InlineData("AG_Widget_Prod")]
    public void An_unknown_component_is_unhandled_not_failed(string component)
    {
        var s = ServerDiagnosticsParser.TryParse(component, "UNKNOWN", "<whatever/>", Ts);

        Assert.Equal(DiagnosticsOutcome.Unhandled, s.Outcome);
        Assert.Equal(component, s.Component);
        Assert.Empty(s.Metrics);
    }

    [Theory]
    [InlineData("QUERY_PROCESSING", null)]
    [InlineData("QUERY_PROCESSING", "")]
    [InlineData("QUERY_PROCESSING", "<queryProcessing")]
    [InlineData("SYSTEM", "<queryProcessing maxWorkers=\"1\"/>")]   // racine != composant
    public void A_known_component_with_unusable_xml_is_a_failure(string component, string? xml)
    {
        var s = ServerDiagnosticsParser.TryParse(component, "CLEAN", xml, Ts);

        Assert.Equal(DiagnosticsOutcome.Failed, s.Outcome);
    }
}
