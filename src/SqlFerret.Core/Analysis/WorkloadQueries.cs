// src/SqlFerret.Core/Analysis/WorkloadQueries.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Analysis;

public class WorkloadQueries(DuckDBConnection conn)
{
    private static readonly HashSet<string> SortCols =
        ["total_duration_us", "p95_duration_us", "max_duration_us", "avg_duration_us"];
    private static readonly HashSet<string> DimFields =
        ["database_name", "login_name", "client_hostname", "client_app_name"];

    public IReadOnlyList<QueryStat> TopSlow(int limit, string sortColumn, IEnumerable<FilterRule> viewFilters)
    {
        if (!SortCols.Contains(sortColumn)) sortColumn = "total_duration_us";
        return QueryStats(limit, sortColumn, viewFilters);
    }

    // TopFrequent orders by count (cnt) — cumulative_cost = count*avg which equals total_duration_us / N * N = total,
    // but the brief says "adds derived cumulative_cost = count*avg ordering via total_duration_us".
    // Ordering by cnt (count) is the frequency order; ordering by total_duration_us is cumulative cost.
    // The brief says TopFrequent uses "cnt" as orderBy but describes it as cumulative cost via total_duration_us.
    // We use "cnt" to rank by frequency (most frequent first), which is the semantically correct interpretation.
    public IReadOnlyList<QueryStat> TopFrequent(int limit, IEnumerable<FilterRule> viewFilters)
        => QueryStats(limit, "cnt", viewFilters);

    private IReadOnlyList<QueryStat> QueryStats(int limit, string orderBy, IEnumerable<FilterRule> viewFilters)
    {
        // orderBy is always from the allow-list (SortCols) or "cnt" — never from user input directly
        string where = FilterCompiler.ToWhereClause(viewFilters);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
          SELECT e.normalized_hash, n.statement_kind, n.primary_table, n.normalized_sql,
                 count(*) AS cnt,
                 avg(e.duration_us) AS avg_duration_us,
                 quantile_cont(e.duration_us, 0.95) AS p95_duration_us,
                 max(e.duration_us) AS max_duration_us,
                 sum(e.duration_us) AS total_duration_us
          FROM executions e JOIN normalized_queries n USING (normalized_hash)
          WHERE {where}
          GROUP BY e.normalized_hash, n.statement_kind, n.primary_table, n.normalized_sql
          ORDER BY {orderBy} DESC
          LIMIT {limit}
          """;
        using var r = cmd.ExecuteReader();
        var list = new List<QueryStat>();
        while (r.Read())
            list.Add(new QueryStat(
                r.GetString(0),                              // normalized_hash
                r.GetString(1),                              // statement_kind
                r.IsDBNull(2) ? null : r.GetString(2),      // primary_table (nullable)
                r.GetString(3),                              // normalized_sql
                r.GetInt64(4),                               // cnt
                r.GetDouble(5),                              // avg_duration_us
                (long)r.GetDouble(6),                        // p95_duration_us (quantile returns double)
                r.GetInt64(7),                               // max_duration_us
                r.GetInt64(8)));                             // total_duration_us
        return list;
    }

    public IReadOnlyList<Occurrence> Occurrences(string normalizedHash, int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT execution_id, captured_at, database_name, login_name, duration_us, sql_text_raw
          FROM executions WHERE normalized_hash = $h ORDER BY captured_at LIMIT $l
          """;
        Add(cmd, "$h", normalizedHash); Add(cmd, "$l", limit);
        return ReadOccurrences(cmd);
    }

    public IReadOnlyList<Occurrence> SessionFlow(int sessionId, DateTime from, DateTime to)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT execution_id, captured_at, database_name, login_name, duration_us, sql_text_raw
          FROM executions WHERE session_id = $s AND captured_at BETWEEN $f AND $t ORDER BY captured_at
          """;
        Add(cmd, "$s", sessionId); Add(cmd, "$f", from); Add(cmd, "$t", to);
        return ReadOccurrences(cmd);
    }

    public IReadOnlyList<ParamImpact> ParameterImpact(string normalizedHash, string paramName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT p.value_text, count(*) AS cnt, avg(e.duration_us) AS avg_dur,
                 quantile_cont(e.duration_us, 0.95) AS p95_dur, max(e.duration_us) AS max_dur
          FROM executions e
          JOIN execution_parameters p ON p.execution_id = e.execution_id
          WHERE e.normalized_hash = $h AND p.name = $n
          GROUP BY p.value_text ORDER BY avg_dur DESC
          """;
        Add(cmd, "$h", normalizedHash); Add(cmd, "$n", paramName);
        using var r = cmd.ExecuteReader();
        var list = new List<ParamImpact>();
        while (r.Read())
            list.Add(new ParamImpact(r.GetString(0), r.GetInt64(1), r.GetDouble(2), (long)r.GetDouble(3), r.GetInt64(4)));
        return list;
    }

    public IReadOnlyList<DimensionStat> Dimension(string field)
    {
        if (!DimFields.Contains(field)) throw new ArgumentException($"field not allowed: {field}");
        // field is allow-listed — safe to interpolate
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
          SELECT COALESCE({field}, '(none)') AS v, count(*) AS cnt, sum(duration_us) AS total
          FROM executions GROUP BY v ORDER BY total DESC
          """;
        using var r = cmd.ExecuteReader();
        var list = new List<DimensionStat>();
        while (r.Read()) list.Add(new DimensionStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public QualityStat Quality(long runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT events_read, events_mapped, events_unmapped, events_cleaned, tokenize_failures
          FROM ingestion_runs WHERE run_id = $r
          """;
        Add(cmd, "$r", runId);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new QualityStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    private static IReadOnlyList<Occurrence> ReadOccurrences(System.Data.IDbCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Occurrence>();
        while (r.Read())
            list.Add(new Occurrence(
                r.GetInt64(0),
                r.GetDateTime(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.GetString(5)));
        return list;
    }

    public ExecutionEvent LoadExecution(long executionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT event_name, event_class, object_name, database_name, login_name,
                 client_hostname, client_app_name, session_id, captured_at, duration_us,
                 sql_text_raw, xe_file_name, file_offset
          FROM executions WHERE execution_id = $id
          """;
        Add(cmd, "$id", executionId);

        string eventName, eventClassText, sqlRaw, xeFile;
        string? objectName, db, login, host, app;
        int? sessionId; long? durationUs; long fileOffset; DateTime capturedAt;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException($"execution {executionId} not found");
            eventName = r.GetString(0);
            eventClassText = r.GetString(1);
            objectName = r.IsDBNull(2) ? null : r.GetString(2);
            db = r.IsDBNull(3) ? null : r.GetString(3);
            login = r.IsDBNull(4) ? null : r.GetString(4);
            host = r.IsDBNull(5) ? null : r.GetString(5);
            app = r.IsDBNull(6) ? null : r.GetString(6);
            sessionId = r.IsDBNull(7) ? null : r.GetInt32(7);
            capturedAt = r.GetDateTime(8);
            durationUs = r.IsDBNull(9) ? null : r.GetInt64(9);
            sqlRaw = r.GetString(10);
            xeFile = r.GetString(11);
            fileOffset = r.GetInt64(12);
        }

        var parameters = new List<RawParameter>();
        using (var pcmd = conn.CreateCommand())
        {
            pcmd.CommandText = """
              SELECT ordinal, name, source_kind, sql_type_guess, value_text, parse_confidence
              FROM execution_parameters WHERE execution_id = $id ORDER BY ordinal
              """;
            Add(pcmd, "$id", executionId);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
                parameters.Add(new RawParameter(
                    pr.GetInt32(0),
                    pr.IsDBNull(1) ? null : pr.GetString(1),
                    Enum.Parse<ParameterSourceKind>(pr.GetString(2), ignoreCase: true),
                    pr.IsDBNull(3) ? null : pr.GetString(3),
                    pr.GetString(4),
                    pr.GetDouble(5)));
        }

        return new ExecutionEvent
        {
            CapturedAt = capturedAt,
            EventName = eventName,
            EventClass = Enum.Parse<EventClass>(eventClassText, ignoreCase: true),
            ObjectName = objectName,
            DatabaseName = db,
            LoginName = login,
            ClientHostname = host,
            ClientAppName = app,
            SessionId = sessionId,
            DurationUs = durationUs,
            SqlTextRaw = sqlRaw,
            Parameters = parameters,
            XeFileName = xeFile,
            FileOffset = fileOffset,
        };
    }

    // DuckDB.NET 1.5.3: ParameterName must NOT include the leading '$';
    // the SQL placeholder uses $name but the parameter collection matches by name without prefix.
    // This matches the convention established in DuckDbProject.cs.
    private static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
