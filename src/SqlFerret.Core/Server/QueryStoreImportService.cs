using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Server;

public record QueryStoreImportOptions(string? Database, bool WritePlans, QueryStoreWindow Window);

public record QdsImportResult(long RunId, long QueriesCount, long QueryTextCount, long PlansCount,
    long RuntimeStatRows, long WaitStatRows, long PlanFilesWritten, long PlanWriteFailures);

/// <summary>
/// Reads a database's Query Store (all of it, or a time window) into the project's qds_* tables and
/// writes each plan as a .sqlplan file. Read-only against sys.query_store_*, under READ UNCOMMITTED.
/// </summary>
public class QueryStoreImportService(string connectionString, DuckDbProject db, string plansFolder)
{
    public const int Version = 1;

    public QdsImportResult Import(QueryStoreImportOptions opts, IProgress<string>? progress = null)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        Exec(conn, "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;");
        if (!string.IsNullOrWhiteSpace(opts.Database))
            Exec(conn, $"USE [{opts.Database!.Replace("]", "]]")}];");

        var (actual, desired) = ReadQueryStoreState(conn);
        if (actual is null || actual is "OFF" or "ERROR")
            throw new InvalidOperationException(
                $"Query Store is not enabled on the target database (actual_state={actual ?? "absent"})");

        var version = ReadScalarString(conn, "SELECT CONVERT(sysname, SERVERPROPERTY('ProductVersion'))");
        var serverName = ReadScalarString(conn, "SELECT CONVERT(sysname, SERVERPROPERTY('ServerName'))");
        var databaseName = ReadScalarString(conn, "SELECT DB_NAME()");
        bool waitAvailable = ObjectExists(conn, "sys.query_store_wait_stats");

        var window = opts.Window;
        long runId = db.BeginQdsRun(new QdsRunInfo(serverName, databaseName, window.From, window.To,
            version, actual, desired, waitAvailable, opts.WritePlans));

        // 1. query text
        var qtCount = StreamInsert(conn, QueryTextSql(window), window, progress, "query text",
            rows => db.InsertQdsQueryText(runId, rows), ReadQueryText);

        // 2. queries
        var qCount = StreamInsert(conn, QueriesSql(window), window, progress, "queries",
            rows => db.InsertQdsQueries(runId, rows), ReadQuery);

        // 3. plans (+ optional .sqlplan write). Counters are mutated inside the reader lambda
        // (locals are captured by reference; a `ref` parameter could not be).
        long plansWritten = 0, planFailures = 0;
        var planRows = ReadAll(conn, PlansSql(window), window, r =>
        {
            long planId = Convert.ToInt64(r.GetValue(0));
            string? path = null; bool ok = false;
            var planXml = S(r, 12);
            if (opts.WritePlans && planXml is not null) // skip plans with no captured XML (NULL query_plan)
            {
                try { path = WritePlan(planId, planXml); ok = true; plansWritten++; }
                catch { planFailures++; }
            }
            return new QdsPlanRow(planId, Convert.ToInt64(r.GetValue(1)), S(r, 2), S(r, 3),
                r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4)),
                Convert.ToBoolean(r.GetValue(5)), Convert.ToBoolean(r.GetValue(6)), Convert.ToBoolean(r.GetValue(7)),
                Convert.ToInt32(r.GetValue(8)), S(r, 9), Convert.ToInt64(r.GetValue(10)), Dt(r, 11), path, ok);
        });
        db.InsertQdsPlans(runId, planRows);
        progress?.Report($"plans {planRows.Count}");

        // 4. runtime stats
        var rtCount = StreamInsert(conn, RuntimeStatsSql(window, version), window, progress, "runtime stats",
            rows => db.InsertQdsRuntimeStats(runId, rows), ReadRuntimeStat);

        // 5. wait stats (when available)
        long waitCount = 0;
        if (waitAvailable)
            waitCount = StreamInsert(conn, WaitStatsSql(window), window, progress, "wait stats",
                rows => db.InsertQdsWaitStats(runId, rows), ReadWaitStat);

        var counters = new QdsRunCounters(qCount, qtCount, planRows.Count, rtCount, waitCount, plansWritten, planFailures);
        db.FinishQdsRun(runId, counters);
        return new QdsImportResult(runId, qCount, qtCount, planRows.Count, rtCount, waitCount, plansWritten, planFailures);
    }

    // ---- plan file writing ----------------------------------------------------------------------

    private string? WritePlan(long planId, string xml)
    {
        var dir = Path.Combine(plansFolder, "qds");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{planId}.sqlplan"), xml);
        return $"qds/{planId}.sqlplan";
    }

    // ---- source SQL (window-filtered; see note about exact column names) ------------------------

    private static string Where(QueryStoreWindow w, string intervalAlias) =>
        w.IsBounded
            ? $" WHERE {intervalAlias}.start_time < @to AND {intervalAlias}.end_time > @from "
            : " ";

    // query text used by queries active in window (or all)
    private static string QueryTextSql(QueryStoreWindow w) => w.IsBounded
        ? """
          SELECT DISTINCT qt.query_text_id, qt.query_sql_text, qt.is_part_of_encrypted_module, qt.has_restricted_text
          FROM sys.query_store_query_text qt
          JOIN sys.query_store_query q ON q.query_text_id = qt.query_text_id
          JOIN sys.query_store_plan p ON p.query_id = q.query_id
          JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          WHERE i.start_time < @to AND i.end_time > @from
          """
        : "SELECT query_text_id, query_sql_text, is_part_of_encrypted_module, has_restricted_text FROM sys.query_store_query_text";

    private static string QueriesSql(QueryStoreWindow w) => w.IsBounded
        ? """
          SELECT DISTINCT q.query_id, q.query_text_id, q.object_id,
            CASE WHEN q.object_id IS NULL THEN NULL ELSE QUOTENAME(OBJECT_SCHEMA_NAME(q.object_id)) + '.' + QUOTENAME(OBJECT_NAME(q.object_id)) END AS object_name,
            CONVERT(varchar(34), q.query_hash, 1) AS query_hash, q.query_parameterization_type_desc, q.is_internal_query,
            q.count_compiles, q.last_execution_time
          FROM sys.query_store_query q
          JOIN sys.query_store_plan p ON p.query_id = q.query_id
          JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          WHERE i.start_time < @to AND i.end_time > @from
          """
        : """
          SELECT q.query_id, q.query_text_id, q.object_id,
            CASE WHEN q.object_id IS NULL THEN NULL ELSE QUOTENAME(OBJECT_SCHEMA_NAME(q.object_id)) + '.' + QUOTENAME(OBJECT_NAME(q.object_id)) END AS object_name,
            CONVERT(varchar(34), q.query_hash, 1) AS query_hash, q.query_parameterization_type_desc, q.is_internal_query,
            q.count_compiles, q.last_execution_time
          FROM sys.query_store_query q
          """;

    private static string PlansSql(QueryStoreWindow w) => w.IsBounded
        ? """
          SELECT DISTINCT p.plan_id, p.query_id, CONVERT(varchar(34), p.query_plan_hash, 1) AS query_plan_hash,
            p.engine_version, p.compatibility_level, p.is_forced_plan, p.is_trivial_plan, p.is_parallel_plan,
            p.force_failure_count, p.last_force_failure_reason_desc, p.count_compiles, p.last_execution_time, p.query_plan
          FROM sys.query_store_plan p
          JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          WHERE i.start_time < @to AND i.end_time > @from
          """
        : """
          SELECT p.plan_id, p.query_id, CONVERT(varchar(34), p.query_plan_hash, 1) AS query_plan_hash,
            p.engine_version, p.compatibility_level, p.is_forced_plan, p.is_trivial_plan, p.is_parallel_plan,
            p.force_failure_count, p.last_force_failure_reason_desc, p.count_compiles, p.last_execution_time, p.query_plan
          FROM sys.query_store_plan p
          """;

    private static string RuntimeStatsSql(QueryStoreWindow w, string? version)
    {
        bool v2017 = SupportsV2017(version);
        var cols = string.Join(", ", QdsSchema.RuntimeMetrics.SelectMany(m =>
        {
            if (!v2017 && QdsSchema.V2017Plus.Contains(m.Qs))
                return new[] { $"CAST(NULL AS BIGINT) AS avg_{m.Qs}", $"CAST(NULL AS BIGINT) AS min_{m.Qs}",
                               $"CAST(NULL AS BIGINT) AS max_{m.Qs}", $"CAST(NULL AS BIGINT) AS last_{m.Qs}",
                               $"CAST(NULL AS FLOAT) AS stdev_{m.Qs}" };
            return new[] { $"rs.avg_{m.Qs}", $"rs.min_{m.Qs}", $"rs.max_{m.Qs}", $"rs.last_{m.Qs}", $"rs.stdev_{m.Qs}" };
        }));
        return $"""
          SELECT rs.runtime_stats_id, rs.plan_id, rs.runtime_stats_interval_id,
            i.start_time, i.end_time, rs.execution_type_desc, rs.count_executions, {cols}
          FROM sys.query_store_runtime_stats rs
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
          {Where(w, "i")}
          """;
    }

    private static string WaitStatsSql(QueryStoreWindow w) => $"""
          SELECT ws.wait_stats_id, ws.plan_id, ws.runtime_stats_interval_id, ws.wait_category_desc,
            ws.execution_type_desc, ws.total_query_wait_time_ms,
            ws.avg_query_wait_time_ms, ws.min_query_wait_time_ms, ws.max_query_wait_time_ms,
            ws.last_query_wait_time_ms, ws.stdev_query_wait_time_ms
          FROM sys.query_store_wait_stats ws
          JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = ws.runtime_stats_interval_id
          {Where(w, "i")}
          """;

    // ---- readers (SqlDataReader → DTO) ----------------------------------------------------------

    private static long? L(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static double? D(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private static DateTime? Dt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : QdsConvert.AsDateTime(r.GetValue(i));

    private static QdsQueryTextRow ReadQueryText(SqlDataReader r) =>
        new(Convert.ToInt64(r.GetValue(0)), S(r, 1), Convert.ToBoolean(r.GetValue(2)), Convert.ToBoolean(r.GetValue(3)));

    private static QdsQueryRow ReadQuery(SqlDataReader r) =>
        new(Convert.ToInt64(r.GetValue(0)), L(r, 1), L(r, 2), S(r, 3), S(r, 4), S(r, 5),
            Convert.ToBoolean(r.GetValue(6)), Convert.ToInt64(r.GetValue(7)), Dt(r, 8));

    private static QdsRuntimeStatRow ReadRuntimeStat(SqlDataReader r)
    {
        const int baseCols = 7; // runtime_stats_id, plan_id, interval_id, start, end, exec_type, count
        var metrics = new List<MetricAggregate>(QdsSchema.RuntimeMetrics.Length);
        for (int m = 0; m < QdsSchema.RuntimeMetrics.Length; m++)
        {
            int b = baseCols + m * 5;
            metrics.Add(new MetricAggregate(L(r, b), L(r, b + 1), L(r, b + 2), L(r, b + 3), D(r, b + 4)));
        }
        return new QdsRuntimeStatRow(Convert.ToInt64(r.GetValue(0)), Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
            QdsConvert.AsDateTime(r.GetValue(3)), QdsConvert.AsDateTime(r.GetValue(4)), S(r, 5), Convert.ToInt64(r.GetValue(6)), metrics);
    }

    private static QdsWaitStatRow ReadWaitStat(SqlDataReader r)
    {
        // QS wait times are float milliseconds → microseconds at the ingestion boundary. Read as
        // double so sub-millisecond waits are not truncated to zero before the ×1000.
        static long? Us(double? ms) => ms is null ? null : QdsConvert.MsToUs(ms.Value);
        static double? UsD(double? ms) => ms is null ? null : ms * 1000.0;
        long totalUs = QdsConvert.MsToUs(D(r, 5) ?? 0);
        var wait = new MetricAggregate(Us(D(r, 6)), Us(D(r, 7)), Us(D(r, 8)), Us(D(r, 9)), UsD(D(r, 10)));
        return new QdsWaitStatRow(Convert.ToInt64(r.GetValue(0)), Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
            S(r, 3), S(r, 4), wait, totalUs);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private long StreamInsert<T>(SqlConnection conn, string sql, QueryStoreWindow window,
        IProgress<string>? progress, string label, Action<IReadOnlyList<T>> insert, Func<SqlDataReader, T> read)
    {
        var rows = ReadAll(conn, sql, window, read);
        insert(rows);
        progress?.Report($"{label} {rows.Count}");
        return rows.Count;
    }

    private List<T> ReadAll<T>(SqlConnection conn, string sql, QueryStoreWindow window, Func<SqlDataReader, T> read)
    {
        var list = new List<T>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0; // Query Store reads can be large; no client timeout
        if (window.IsBounded) // windowed SQL references @from/@to; bind as datetimeoffset (the QS
        {                     // interval column type). A one-sided window opens the other end.
            cmd.Parameters.Add(new SqlParameter("@from", System.Data.SqlDbType.DateTimeOffset)
            { Value = QdsConvert.WindowBound(window.From, DateTimeOffset.MinValue) });
            cmd.Parameters.Add(new SqlParameter("@to", System.Data.SqlDbType.DateTimeOffset)
            { Value = QdsConvert.WindowBound(window.To, DateTimeOffset.MaxValue) });
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(read(reader));
        return list;
    }

    private (string? actual, string? desired) ReadQueryStoreState(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actual_state_desc, desired_state_desc FROM sys.database_query_store_options";
        try
        {
            using var r = cmd.ExecuteReader();
            return r.Read() ? (S(r, 0), S(r, 1)) : (null, null);
        }
        catch (SqlException) { return (null, null); } // view absent on very old servers
    }

    private static bool ObjectExists(SqlConnection conn, string viewName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@n) IS NULL THEN 0 ELSE 1 END";
        cmd.Parameters.AddWithValue("@n", viewName);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static string? ReadScalarString(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    private static void Exec(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static bool SupportsV2017(string? productVersion)
    {
        // ProductVersion like "16.0.1000.6"; major >= 14 ⇒ SQL Server 2017+.
        if (string.IsNullOrEmpty(productVersion)) return true; // assume modern when unknown
        var dot = productVersion.IndexOf('.');
        var majorStr = dot > 0 ? productVersion[..dot] : productVersion;
        return int.TryParse(majorStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) && major >= 14;
    }
}
