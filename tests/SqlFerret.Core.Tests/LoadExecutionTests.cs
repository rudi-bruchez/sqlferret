// tests/SqlFerret.Core.Tests/LoadExecutionTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Replay;
using SqlFerret.Core.Storage;

public class LoadExecutionTests
{
    [Fact]
    public void LoadExecution_rebuilds_event_for_replay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            // an rpc_completed event with object_name + a statement carrying params
            var ev = new FakeEvent("rpc_completed", new DateTime(2026, 1, 1),
                new Dictionary<string, object?>
                {
                    ["statement"] = "exec dbo.GetOrder @OrderId = 123",
                    ["object_name"] = "dbo.GetOrder",
                    ["duration"] = 10L
                },
                new Dictionary<string, object?>());
            new IngestionService(db, new IngestionOptions(RedactionMode.Full, []))
                .Ingest("logs/", [((IXeEventData)ev, "s_0.xel", 0L)]);

            long id;
            using (var cmd = db.Connection.CreateCommand())
            { cmd.CommandText = "SELECT execution_id FROM executions LIMIT 1"; id = Convert.ToInt64(cmd.ExecuteScalar()); }

            var loaded = new WorkloadQueries(db.Connection).LoadExecution(id);
            Assert.Equal(EventClass.RpcCall, loaded.EventClass);
            Assert.Equal("dbo.GetOrder", loaded.ObjectName);
            Assert.Single(loaded.Parameters);
            Assert.Equal("@OrderId", loaded.Parameters[0].Name);

            var replay = ReplayBuilder.Build(loaded);
            Assert.Equal(ReplayKind.ExecProc, replay.Kind);
            Assert.Equal("EXEC dbo.GetOrder @OrderId = 123;", replay.Sql);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
