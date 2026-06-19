// src/SqlFerret.Core/Storage/DuckDbProject.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;

namespace SqlFerret.Core.Storage;

public sealed class DuckDbProject : IDisposable
{
    public DuckDBConnection Connection { get; }

    private long _nextExecutionId = -1;
    private long _nextRunId = -1;

    private DuckDbProject(DuckDBConnection conn) => Connection = conn;

    public static DuckDbProject Open(string path)
    {
        var conn = new DuckDBConnection($"Data Source={path}");
        conn.Open();
        CreateSchema(conn);
        return new DuckDbProject(conn);
    }

    private static void CreateSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS ingestion_runs (
          run_id BIGINT PRIMARY KEY, source_path TEXT, files_count INTEGER, bytes_total BIGINT,
          started_at TIMESTAMP, finished_at TIMESTAMP, events_read BIGINT, events_mapped BIGINT,
          events_unmapped BIGINT, events_cleaned BIGINT, tokenize_failures BIGINT,
          normalizer_version INTEGER, redaction_policy TEXT);

        CREATE TABLE IF NOT EXISTS executions (
          execution_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP, event_name TEXT,
          event_class TEXT, object_name TEXT, is_system BOOLEAN, database_name TEXT, login_name TEXT,
          client_hostname TEXT, client_app_name TEXT, session_id INTEGER, duration_us BIGINT,
          cpu_time_us BIGINT, logical_reads BIGINT, physical_reads BIGINT, writes BIGINT, row_count BIGINT,
          query_hash TEXT, query_plan_hash TEXT, sql_text_raw TEXT, normalized_hash TEXT,
          xe_file_name TEXT, file_offset BIGINT);

        CREATE TABLE IF NOT EXISTS normalized_queries (
          normalized_hash TEXT PRIMARY KEY, normalized_sql TEXT, statement_kind TEXT,
          primary_table TEXT, normalizer_version INTEGER, first_seen_at TIMESTAMP, last_seen_at TIMESTAMP);

        CREATE TABLE IF NOT EXISTS execution_parameters (
          execution_id BIGINT, ordinal INTEGER, name TEXT, source_kind TEXT, sql_type_guess TEXT,
          value_text TEXT, value_redacted BOOLEAN, is_truncated BOOLEAN, parse_confidence DOUBLE);
        """;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = Connection.CreateCommand(); c.CommandText = sql;
        var v = c.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    public long BeginRun(string sourcePath, int filesCount, long bytesTotal, string redactionPolicy)
    {
        if (_nextRunId < 0) _nextRunId = Scalar("SELECT COALESCE(MAX(run_id),0) FROM ingestion_runs") + 1;
        long runId = _nextRunId++;
        using var c = Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO ingestion_runs(run_id, source_path, files_count, bytes_total, started_at,
            finished_at, events_read, events_mapped, events_unmapped, events_cleaned,
            tokenize_failures, normalizer_version, redaction_policy)
          VALUES ($id,$src,$fc,$bt, now(), NULL, 0,0,0,0,0, $nv, $rp)
          """;
        Add(c, "$id", runId); Add(c, "$src", sourcePath); Add(c, "$fc", filesCount);
        Add(c, "$bt", bytesTotal); Add(c, "$nv", QueryNormalizer.Version); Add(c, "$rp", redactionPolicy);
        c.ExecuteNonQuery();
        return runId;
    }

    public long NextExecutionId()
    {
        if (_nextExecutionId < 0) _nextExecutionId = Scalar("SELECT COALESCE(MAX(execution_id),0) FROM executions");
        return ++_nextExecutionId;
    }

    public void InsertBatch(long runId, IReadOnlyList<PreparedRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            long id = NextExecutionId();
            InsertExecution(tx, id, runId, r);
            UpsertSignature(tx, r);
            foreach (var p in r.Parameters) InsertParameter(tx, id, p);
        }
        tx.Commit();
    }

    private void InsertExecution(DuckDBTransaction tx, long id, long runId, PreparedRow r)
    {
        var e = r.Event;
        using var c = Connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = """
          INSERT INTO executions VALUES ($id,$run,$ts,$en,$ec,$obj,$sys,$db,$login,$host,$app,$sid,
            $dur,$cpu,$lr,$pr,$w,$rows,$qh,$qph,$raw,$nh,$file,$off)
          """;
        Add(c,"$id",id); Add(c,"$run",runId); Add(c,"$ts",e.CapturedAt); Add(c,"$en",e.EventName);
        Add(c,"$ec",e.EventClass.ToString()); Add(c,"$obj",(object?)e.ObjectName); Add(c,"$sys",e.IsSystem);
        Add(c,"$db",(object?)e.DatabaseName); Add(c,"$login",(object?)e.LoginName);
        Add(c,"$host",(object?)e.ClientHostname); Add(c,"$app",(object?)e.ClientAppName);
        Add(c,"$sid",(object?)e.SessionId); Add(c,"$dur",(object?)e.DurationUs); Add(c,"$cpu",(object?)e.CpuTimeUs);
        Add(c,"$lr",(object?)e.LogicalReads); Add(c,"$pr",(object?)e.PhysicalReads); Add(c,"$w",(object?)e.Writes);
        Add(c,"$rows",(object?)e.RowCount); Add(c,"$qh",(object?)e.QueryHash); Add(c,"$qph",(object?)e.QueryPlanHash);
        Add(c,"$raw",e.SqlTextRaw); Add(c,"$nh",r.Normalized.NormalizedHash);
        Add(c,"$file",e.XeFileName); Add(c,"$off",e.FileOffset);
        c.ExecuteNonQuery();
    }

    private void UpsertSignature(DuckDBTransaction tx, PreparedRow r)
    {
        var n = r.Normalized;
        using var c = Connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = """
          INSERT INTO normalized_queries VALUES ($h,$sql,$kind,$tbl,$ver,$ts,$ts)
          ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at = $ts
          """;
        Add(c,"$h",n.NormalizedHash); Add(c,"$sql",n.NormalizedSql); Add(c,"$kind",n.StatementKind);
        Add(c,"$tbl",(object?)n.PrimaryTable); Add(c,"$ver",QueryNormalizer.Version); Add(c,"$ts",r.Event.CapturedAt);
        c.ExecuteNonQuery();
    }

    private void InsertParameter(DuckDBTransaction tx, long execId, PreparedParameter p)
    {
        using var c = Connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = "INSERT INTO execution_parameters VALUES ($id,$ord,$name,$sk,$tg,$val,$red,$trunc,$conf)";
        Add(c,"$id",execId); Add(c,"$ord",p.Ordinal); Add(c,"$name",(object?)p.Name); Add(c,"$sk",p.SourceKind);
        Add(c,"$tg",(object?)p.TypeGuess); Add(c,"$val",p.Value); Add(c,"$red",p.Redacted);
        Add(c,"$trunc",p.Truncated); Add(c,"$conf",p.Confidence);
        c.ExecuteNonQuery();
    }

    public void FinishRun(long runId, long read, long mapped, long unmapped, long cleaned, long tokenizeFailures)
    {
        using var c = Connection.CreateCommand();
        c.CommandText = """
          UPDATE ingestion_runs SET finished_at = now(), events_read=$r, events_mapped=$m,
            events_unmapped=$u, events_cleaned=$cl, tokenize_failures=$tf WHERE run_id=$id
          """;
        Add(c,"$r",read); Add(c,"$m",mapped); Add(c,"$u",unmapped); Add(c,"$cl",cleaned);
        Add(c,"$tf",tokenizeFailures); Add(c,"$id",runId);
        c.ExecuteNonQuery();
    }

    // DuckDB.NET 1.5.3: ParameterName must NOT include the leading '$';
    // the SQL placeholder uses $name but the parameter collection matches by name without prefix.
    private static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }

    public void Dispose() => Connection.Dispose();
}
