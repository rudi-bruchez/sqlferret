using SqlFerret.Core.Ingestion;

public class BlockingClassifyTests
{
    [Theory]
    [InlineData("blocked_process_report", BlockingEventKind.Blocked)]
    [InlineData("xml_deadlock_report", BlockingEventKind.Deadlock)]
    [InlineData("rpc_completed", BlockingEventKind.None)]
    public void ClassifyBlocking_recognizes_report_events(string name, BlockingEventKind kind)
        => Assert.Equal(kind, EventMapper.ClassifyBlocking(name));

    [Fact]
    public void ExtractBlockingXml_reads_blocked_process_field()
    {
        var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
            new Dictionary<string, object?> { ["blocked_process"] = "<blocked-process-report/>" },
            new Dictionary<string, object?>());
        Assert.Equal("<blocked-process-report/>", EventMapper.ExtractBlockingXml(ev));
    }
}
