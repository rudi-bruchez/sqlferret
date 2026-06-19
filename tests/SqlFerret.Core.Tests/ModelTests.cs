using SqlFerret.Core.Model;
using Xunit;

public class ModelTests
{
    [Fact]
    public void ExecutionEvent_defaults_parameters_to_empty()
    {
        var e = new ExecutionEvent { SqlTextRaw = "select 1", EventName = "sql_batch_completed", XeFileName = "a.xel" };
        Assert.Empty(e.Parameters);
        Assert.Equal(EventClass.Unknown, e.EventClass);
    }

    [Fact]
    public void RawParameter_holds_values()
    {
        var p = new RawParameter(0, "@id", ParameterSourceKind.RpcParameter, "int", "42", 0.9);
        Assert.Equal("@id", p.Name);
        Assert.Equal("42", p.ValueText);
    }
}
