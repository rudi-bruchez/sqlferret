using SqlFerret.Core.Model;
using SqlFerret.Core.Replay;
using Xunit;

public class ReplayBuilderTests
{
    [Fact]
    public void Raw_batch_copies_verbatim()
    {
        var ev = new ExecutionEvent { EventName="sql_batch_completed", EventClass=EventClass.SqlBatch,
            SqlTextRaw="SELECT * FROM t WHERE id = 5", XeFileName="a" };
        var r = ReplayBuilder.Build(ev);
        Assert.Equal(ReplayKind.RawBatch, r.Kind);
        Assert.Equal("SELECT * FROM t WHERE id = 5", r.Sql);
        Assert.Equal(1.0, r.Confidence);
    }

    [Fact]
    public void Rpc_builds_exec()
    {
        var ev = new ExecutionEvent { EventName="rpc_completed", EventClass=EventClass.RpcCall,
            ObjectName="dbo.GetOrder", SqlTextRaw="", XeFileName="a",
            Parameters=new[] {
                new RawParameter(0,"@OrderId",ParameterSourceKind.RpcParameter,"int","123",0.9),
                new RawParameter(1,"@Culture",ParameterSourceKind.RpcParameter,"nvarchar","N'fr-FR'",0.9) } };
        var r = ReplayBuilder.Build(ev);
        Assert.Equal(ReplayKind.ExecProc, r.Kind);
        Assert.Equal("EXEC dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR';", r.Sql);
    }
}
