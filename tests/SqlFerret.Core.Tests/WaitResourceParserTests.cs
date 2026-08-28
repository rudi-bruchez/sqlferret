// tests/SqlFerret.Core.Tests/WaitResourceParserTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;

public class WaitResourceParserTests
{
    [Theory]
    [InlineData("OBJECT: 5:1977058079:0", WaitResourceType.Object, 5, 1977058079L)]
    [InlineData("KEY: 5:72057594041204736 (8194443284a0)", WaitResourceType.Key, 5, null)]
    [InlineData("PAGE: 6:1:70ableau", WaitResourceType.Page, 6, null)]
    [InlineData("RID: 5:1:8956:0", WaitResourceType.Rid, 5, null)]
    [InlineData("DATABASE: 2:38 ", WaitResourceType.Database, 2, null)]
    public void Parse_classifies_resource_and_extracts_ids(string raw, WaitResourceType type, int? db, long? objId)
    {
        var info = WaitResourceParser.Parse(raw);
        Assert.Equal(type, info.Type);
        Assert.Equal(db, info.DatabaseId);
        Assert.Equal(objId, info.ObjectId);
    }

    [Theory]
    [InlineData("PAGELATCH_EX: 2:1:128", WaitResourceType.PageLatch)]
    [InlineData("APPLICATION: 5:0:[Form]", WaitResourceType.AppLock)]
    [InlineData(null, WaitResourceType.Other)]
    [InlineData("", WaitResourceType.Other)]
    public void Parse_handles_latch_applock_and_empty(string? raw, WaitResourceType type)
        => Assert.Equal(type, WaitResourceParser.Parse(raw).Type);
}
