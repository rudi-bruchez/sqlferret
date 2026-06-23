// tests/SqlFerret.Core.Tests/BlockingReportParserTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;

public class BlockingReportParserTests
{
    // synthetic, no PII — shape mirrors a real blocked_process_report
    private const string ReportXml = """
    <blocked-process-report monitorLoop="42">
      <blocked-process>
        <process id="p1" waitresource="KEY: 5:72057594041204736 (x)" waittime="5972"
                 spid="201" status="suspended" trancount="2" lockMode="S"
                 isolationlevel="read committed (2)" clientapp="WedaApp" hostname="WS1" loginname="svc">
          <inputbuf>exec dbo.ASP_Select_FSE @CabinetID=897,@Nir='2921225462283'</inputbuf>
        </process>
      </blocked-process>
      <blocking-process>
        <process id="p2" spid="118" status="sleeping" trancount="1" clientapp="WedaApp" hostname="WS2" loginname="svc">
          <inputbuf>UPDATE dbo.T_FeuilleSoinsElectronique_Fse SET Fse_TM=0 WHERE Fse_ID=42</inputbuf>
        </process>
      </blocking-process>
    </blocked-process-report>
    """;

    [Fact]
    public void Parse_extracts_both_processes_and_units()
    {
        var r = BlockingReportParser.Parse(ReportXml, new DateTime(2026, 2, 24));
        Assert.NotNull(r);
        Assert.Equal(42, r!.MonitorLoop);
        Assert.Equal(201, r.Blocked.Spid);
        Assert.Equal(WaitResourceType.Key, r.Blocked.WaitResourceType);
        Assert.Equal(5_972_000L, r.Blocked.WaitTimeUs);            // ms -> us
        Assert.Equal("S", r.Blocked.LockMode);
        Assert.Equal(2, r.Blocked.TranCount);
        Assert.Contains("ASP_Select_FSE", r.Blocked.InputBufRaw);
        Assert.Null(r.Blocked.InputBufFingerprint);                // set later, in IngestionService
        Assert.Equal(118, r.Blocking.Spid);
        Assert.Contains("UPDATE", r.Blocking.InputBufRaw);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("<blocked-process-report></blocked-process-report>")]   // no processes
    public void Parse_returns_null_on_malformed_or_empty(string xml)
        => Assert.Null(BlockingReportParser.Parse(xml, DateTime.UnixEpoch));
}
