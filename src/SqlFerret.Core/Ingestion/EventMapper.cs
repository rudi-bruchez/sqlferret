using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;

namespace SqlFerret.Core.Ingestion;

public enum BlockingEventKind { None, Blocked, Deadlock }

public static class EventMapper
{
    public static ExecutionEvent Map(IXeEventData ev, string fileName, long fileOffset)
    {
        var cls = Classify(ev.Name);
        var sql = ExtractSql(ev, cls);
        if (sql is null) cls = EventClass.Unknown;

        IReadOnlyList<RawParameter> parameters = sql is null ? [] : ParameterExtractor.Extract(cls, sql);

        return new ExecutionEvent
        {
            CapturedAt = ev.Timestamp,
            EventName = ev.Name,
            EventClass = cls,
            ObjectName = Str(ev.Fields, "object_name"),
            IsSystem = Bool(ev.Fields, "is_system"),
            DatabaseName = Str(ev.Actions, "database_name"),
            LoginName = Str(ev.Actions, "server_principal_name") ?? Str(ev.Actions, "username"),
            ClientHostname = Str(ev.Actions, "client_hostname"),
            ClientAppName = Str(ev.Actions, "client_app_name"),
            SessionId = Int(ev.Actions, "session_id"),
            DurationUs = Long(ev.Fields, "duration"),
            CpuTimeUs = Long(ev.Fields, "cpu_time"),
            LogicalReads = Long(ev.Fields, "logical_reads"),
            PhysicalReads = Long(ev.Fields, "physical_reads"),
            Writes = Long(ev.Fields, "writes"),
            RowCount = Long(ev.Fields, "row_count"),
            QueryHash = Str(ev.Actions, "query_hash"),
            QueryPlanHash = Str(ev.Actions, "query_plan_hash"),
            SqlTextRaw = sql ?? "",
            Parameters = parameters,
            XeFileName = fileName,
            FileOffset = fileOffset,
        };
    }

    private static EventClass Classify(string name) =>
        name.Contains("rpc", StringComparison.OrdinalIgnoreCase) ? EventClass.RpcCall :
        name.Contains("sql_batch", StringComparison.OrdinalIgnoreCase) ? EventClass.SqlBatch :
        name.Contains("statement", StringComparison.OrdinalIgnoreCase) ? EventClass.Statement :
        EventClass.Unknown;

    private static string? ExtractSql(IXeEventData ev, EventClass cls) => cls switch
    {
        EventClass.SqlBatch => Str(ev.Fields, "batch_text"),
        EventClass.RpcCall or EventClass.Statement => Str(ev.Fields, "statement") ?? Str(ev.Fields, "sql_text"),
        _ => null
    };

    public static BlockingEventKind ClassifyBlocking(string name) =>
        name.Equals("blocked_process_report", StringComparison.OrdinalIgnoreCase) ? BlockingEventKind.Blocked :
        name.Equals("xml_deadlock_report", StringComparison.OrdinalIgnoreCase) ? BlockingEventKind.Deadlock :
        BlockingEventKind.None;

    public static string? ExtractBlockingXml(IXeEventData ev) => Str(ev.Fields, "blocked_process");
    public static string? ExtractDeadlockXml(IXeEventData ev) => Str(ev.Fields, "xml_report");

    private static string? Str(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null ? v.ToString() : null;
    private static bool Bool(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null && Convert.ToBoolean(v);
    private static int? Int(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null ? Convert.ToInt32(v) : null;
    private static long? Long(IReadOnlyDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is not null ? Convert.ToInt64(v) : null;
}
