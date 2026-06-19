using SqlFerret.Core.Server;
using SqlFerret.Core.Model;

public class EstimatedPlanServiceTests
{
    [Fact]
    public void Save_writes_sqlplan_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var svc = new EstimatedPlanService(connectionString: "unused", plansFolder: dir);
        var path = svc.Save("abc123", "<ShowPlanXML/>");
        Assert.True(File.Exists(path));
        Assert.EndsWith("abc123.sqlplan", path);
        Assert.Equal("<ShowPlanXML/>", File.ReadAllText(path));
    }

    [SkippableFact]
    public async Task CaptureAsync_returns_sqlplan_path()
    {
        var connStr = Environment.GetEnvironmentVariable("SQLFERRET_TEST_CONN");
        Skip.If(string.IsNullOrEmpty(connStr), "SQLFERRET_TEST_CONN not set — skipping integration test");

        var dir = Directory.CreateTempSubdirectory().FullName;
        var svc = new EstimatedPlanService(connectionString: connStr!, plansFolder: dir);

        var ev = new ExecutionEvent
        {
            EventName = "sql_batch_completed",
            SqlTextRaw = "SELECT 1 AS N",
            XeFileName = "test.xel",
            DatabaseName = "master"
        };

        var path = await svc.CaptureAsync(ev, "plan001");
        Assert.True(File.Exists(path));
        Assert.EndsWith("plan001.sqlplan", path);
        var xml = File.ReadAllText(path);
        Assert.Contains("ShowPlanXML", xml);
    }
}
