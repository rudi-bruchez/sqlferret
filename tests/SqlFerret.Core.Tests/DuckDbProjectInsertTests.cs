// tests/SqlFerret.Core.Tests/DuckDbProjectInsertTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;
using Xunit;

public class DuckDbProjectInsertTests
{
    [Fact]
    public void Insert_batch_writes_executions_params_and_signature()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var p = DuckDbProject.Open(path);
            var run = p.BeginRun("logs/", 1, 100, "masked");

            var ev = new ExecutionEvent { EventName="rpc_completed", EventClass=EventClass.RpcCall,
                ObjectName="dbo.P", SqlTextRaw="exec dbo.P @a = 1", DatabaseName="Sales",
                SessionId=5, DurationUs=4000, CapturedAt=new DateTime(2026,1,1), XeFileName="s_0.xel" };
            var nq = new NormalizedQuery("exec dbo.p @a = ?", "hash1", "EXEC", "dbo.P", false);
            var row = new PreparedRow(ev, nq, [new PreparedParameter(0, "@a", "rpc_parameter", "int", "1", false, false, 0.9)]);

            p.InsertBatch(run, new[] { row });
            p.FinishRun(run, read:1, mapped:1, unmapped:0, cleaned:0, tokenizeFailures:0);

            using var c = p.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM executions"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM execution_parameters"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM normalized_queries"; Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT finished_at IS NOT NULL FROM ingestion_runs"; Assert.True(Convert.ToBoolean(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
