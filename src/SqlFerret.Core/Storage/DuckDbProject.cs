// src/SqlFerret.Core/Storage/DuckDbProject.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Storage;

public sealed class DuckDbProject : IDisposable
{
    public DuckDBConnection Connection { get; }

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

    public void Dispose() => Connection.Dispose();
}
