using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using Xunit;

public class ParameterExtractorTests
{
    [Fact]
    public void Extracts_named_rpc_parameters()
    {
        var ps = ParameterExtractor.Extract(EventClass.RpcCall, "exec dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR'");
        Assert.Equal(2, ps.Count);
        Assert.Equal("@OrderId", ps[0].Name);
        Assert.Equal("123", ps[0].ValueText);
        Assert.Equal("@Culture", ps[1].Name);
        Assert.Equal("N'fr-FR'", ps[1].ValueText);
    }

    [Fact]
    public void No_params_returns_empty()
    {
        var ps = ParameterExtractor.Extract(EventClass.SqlBatch, "select * from t");
        Assert.Empty(ps);
    }
}
