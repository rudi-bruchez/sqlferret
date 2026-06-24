using System.Linq;
using DuckDB.NET.Data;

namespace SqlFerret.Core.Storage;

public sealed partial class DuckDbProject
{
    public const int QdsExtractorVersion = 1;

    private long _nextQdsRunId = -1;

    private static string RuntimeMetricColumns() =>
        string.Join(",\n          ", QdsSchema.RuntimeMetrics.Select(m =>
            $"avg_{m.Duck} BIGINT, min_{m.Duck} BIGINT, max_{m.Duck} BIGINT, last_{m.Duck} BIGINT, stdev_{m.Duck} DOUBLE"));

    private static void CreateQdsSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
        CREATE TABLE IF NOT EXISTS qds_runs (
          run_id BIGINT PRIMARY KEY, server_name TEXT, database_name TEXT, captured_at TIMESTAMP,
          window_from TIMESTAMP, window_to TIMESTAMP,
          sql_server_version TEXT, query_store_actual_state TEXT, query_store_desired_state TEXT,
          wait_stats_available BOOLEAN, plans_requested BOOLEAN,
          queries_count BIGINT, query_text_count BIGINT, plans_count BIGINT,
          runtime_stat_rows BIGINT, wait_stat_rows BIGINT,
          plan_files_written BIGINT, plan_write_failures BIGINT, extractor_version INTEGER);

        CREATE TABLE IF NOT EXISTS qds_query_text (
          run_id BIGINT, query_text_id BIGINT, query_sql_text TEXT,
          is_part_of_encrypted_module BOOLEAN, has_restricted_text BOOLEAN,
          PRIMARY KEY (run_id, query_text_id));

        CREATE TABLE IF NOT EXISTS qds_queries (
          run_id BIGINT, query_id BIGINT, query_text_id BIGINT, object_id BIGINT, object_name TEXT,
          query_hash TEXT, query_parameterization_type TEXT, is_internal_query BOOLEAN,
          count_compiles BIGINT, last_execution_time TIMESTAMP,
          PRIMARY KEY (run_id, query_id));

        CREATE TABLE IF NOT EXISTS qds_plans (
          run_id BIGINT, plan_id BIGINT, query_id BIGINT, query_plan_hash TEXT,
          engine_version TEXT, compatibility_level INTEGER,
          is_forced_plan BOOLEAN, is_trivial_plan BOOLEAN, is_parallel_plan BOOLEAN,
          force_failure_count INTEGER, last_force_failure_reason_desc TEXT,
          count_compiles BIGINT, last_execution_time TIMESTAMP,
          sqlplan_path TEXT, plan_written BOOLEAN,
          PRIMARY KEY (run_id, plan_id));

        CREATE TABLE IF NOT EXISTS qds_runtime_stats (
          run_id BIGINT, runtime_stats_id BIGINT, plan_id BIGINT, runtime_stats_interval_id BIGINT,
          interval_start_time TIMESTAMP, interval_end_time TIMESTAMP, execution_type TEXT,
          count_executions BIGINT,
          {RuntimeMetricColumns()},
          PRIMARY KEY (run_id, runtime_stats_id));

        CREATE TABLE IF NOT EXISTS qds_wait_stats (
          run_id BIGINT, wait_stats_id BIGINT, plan_id BIGINT, runtime_stats_interval_id BIGINT,
          wait_category TEXT, execution_type TEXT,
          total_query_wait_time_us BIGINT,
          avg_query_wait_time_us BIGINT, min_query_wait_time_us BIGINT,
          max_query_wait_time_us BIGINT, last_query_wait_time_us BIGINT, stdev_query_wait_time_us DOUBLE,
          PRIMARY KEY (run_id, wait_stats_id));
        """;
        cmd.ExecuteNonQuery();
    }

    public long BeginQdsRun(QdsRunInfo info)
    {
        if (_nextQdsRunId < 0) _nextQdsRunId = Scalar("SELECT COALESCE(MAX(run_id),0) FROM qds_runs") + 1;
        long runId = _nextQdsRunId++;
        using var c = Connection.CreateCommand();
        c.CommandText = """
          INSERT INTO qds_runs(run_id, server_name, database_name, captured_at, window_from, window_to,
            sql_server_version, query_store_actual_state, query_store_desired_state,
            wait_stats_available, plans_requested,
            queries_count, query_text_count, plans_count, runtime_stat_rows, wait_stat_rows,
            plan_files_written, plan_write_failures, extractor_version)
          VALUES ($id,$srv,$db, now(), $wf,$wt, $ver,$as,$ds, $wsa,$pr, 0,0,0,0,0, 0,0, $ev)
          """;
        Add(c, "$id", runId); Add(c, "$srv", (object?)info.ServerName); Add(c, "$db", (object?)info.DatabaseName);
        Add(c, "$wf", (object?)info.WindowFrom); Add(c, "$wt", (object?)info.WindowTo);
        Add(c, "$ver", (object?)info.SqlServerVersion); Add(c, "$as", (object?)info.ActualState); Add(c, "$ds", (object?)info.DesiredState);
        Add(c, "$wsa", info.WaitStatsAvailable); Add(c, "$pr", info.PlansRequested); Add(c, "$ev", QdsExtractorVersion);
        c.ExecuteNonQuery();
        return runId;
    }

    public void FinishQdsRun(long runId, QdsRunCounters c)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
          UPDATE qds_runs SET queries_count=$q, query_text_count=$qt, plans_count=$p,
            runtime_stat_rows=$rt, wait_stat_rows=$w, plan_files_written=$pw, plan_write_failures=$pf
          WHERE run_id=$id
          """;
        Add(cmd, "$q", c.QueriesCount); Add(cmd, "$qt", c.QueryTextCount); Add(cmd, "$p", c.PlansCount);
        Add(cmd, "$rt", c.RuntimeStatRows); Add(cmd, "$w", c.WaitStatRows);
        Add(cmd, "$pw", c.PlanFilesWritten); Add(cmd, "$pf", c.PlanWriteFailures); Add(cmd, "$id", runId);
        cmd.ExecuteNonQuery();
    }

    public void InsertQdsQueryText(long runId, IReadOnlyList<QdsQueryTextRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_query_text VALUES ($run,$id,$txt,$enc,$rst)";
            Add(c, "$run", runId); Add(c, "$id", r.QueryTextId); Add(c, "$txt", (object?)r.QuerySqlText);
            Add(c, "$enc", r.IsPartOfEncryptedModule); Add(c, "$rst", r.HasRestrictedText);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertQdsQueries(long runId, IReadOnlyList<QdsQueryRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_queries VALUES ($run,$id,$tid,$oid,$obj,$qh,$pt,$int,$cc,$let)";
            Add(c, "$run", runId); Add(c, "$id", r.QueryId); Add(c, "$tid", (object?)r.QueryTextId);
            Add(c, "$oid", (object?)r.ObjectId); Add(c, "$obj", (object?)r.ObjectName); Add(c, "$qh", (object?)r.QueryHash);
            Add(c, "$pt", (object?)r.QueryParameterizationType); Add(c, "$int", r.IsInternalQuery);
            Add(c, "$cc", r.CountCompiles); Add(c, "$let", (object?)r.LastExecutionTime);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertQdsPlans(long runId, IReadOnlyList<QdsPlanRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_plans VALUES ($run,$id,$qid,$qph,$ev,$cl,$f,$t,$par,$ffc,$frr,$cc,$let,$path,$pw)";
            Add(c, "$run", runId); Add(c, "$id", r.PlanId); Add(c, "$qid", r.QueryId); Add(c, "$qph", (object?)r.QueryPlanHash);
            Add(c, "$ev", (object?)r.EngineVersion); Add(c, "$cl", (object?)r.CompatibilityLevel);
            Add(c, "$f", r.IsForcedPlan); Add(c, "$t", r.IsTrivialPlan); Add(c, "$par", r.IsParallelPlan);
            Add(c, "$ffc", r.ForceFailureCount); Add(c, "$frr", (object?)r.LastForceFailureReasonDesc);
            Add(c, "$cc", r.CountCompiles); Add(c, "$let", (object?)r.LastExecutionTime);
            Add(c, "$path", (object?)r.SqlplanPath); Add(c, "$pw", r.PlanWritten);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static readonly string RuntimeInsertSql = BuildRuntimeInsertSql();

    private static string BuildRuntimeInsertSql()
    {
        var baseCols = "run_id, runtime_stats_id, plan_id, runtime_stats_interval_id, " +
                       "interval_start_time, interval_end_time, execution_type, count_executions";
        var basePh = "$run,$id,$plan,$iid,$istart,$iend,$etype,$cnt";
        var metricCols = string.Join(", ", QdsSchema.RuntimeMetrics.SelectMany(m =>
            new[] { $"avg_{m.Duck}", $"min_{m.Duck}", $"max_{m.Duck}", $"last_{m.Duck}", $"stdev_{m.Duck}" }));
        var metricPh = string.Join(", ", QdsSchema.RuntimeMetrics.Select((_, i) =>
            $"$a{i},$mn{i},$mx{i},$ls{i},$sd{i}").ToArray());
        return $"INSERT INTO qds_runtime_stats ({baseCols}, {metricCols}) VALUES ({basePh}, {metricPh})";
    }

    public void InsertQdsRuntimeStats(long runId, IReadOnlyList<QdsRuntimeStatRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = RuntimeInsertSql;
            Add(c, "$run", runId); Add(c, "$id", r.RuntimeStatsId); Add(c, "$plan", r.PlanId); Add(c, "$iid", r.IntervalId);
            Add(c, "$istart", r.IntervalStart); Add(c, "$iend", r.IntervalEnd); Add(c, "$etype", (object?)r.ExecutionType);
            Add(c, "$cnt", r.CountExecutions);
            for (int i = 0; i < QdsSchema.RuntimeMetrics.Length; i++)
            {
                var m = r.Metrics[i];
                Add(c, $"$a{i}", (object?)m.Avg); Add(c, $"$mn{i}", (object?)m.Min); Add(c, $"$mx{i}", (object?)m.Max);
                Add(c, $"$ls{i}", (object?)m.Last); Add(c, $"$sd{i}", (object?)m.Stdev);
            }
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertQdsWaitStats(long runId, IReadOnlyList<QdsWaitStatRow> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var r in rows)
        {
            using var c = Connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO qds_wait_stats VALUES ($run,$id,$plan,$iid,$cat,$etype,$tot,$avg,$min,$max,$last,$sd)";
            Add(c, "$run", runId); Add(c, "$id", r.WaitStatsId); Add(c, "$plan", r.PlanId); Add(c, "$iid", r.IntervalId);
            Add(c, "$cat", (object?)r.WaitCategory); Add(c, "$etype", (object?)r.ExecutionType);
            Add(c, "$tot", r.TotalWaitTimeUs);
            Add(c, "$avg", (object?)r.WaitTimeUs.Avg); Add(c, "$min", (object?)r.WaitTimeUs.Min);
            Add(c, "$max", (object?)r.WaitTimeUs.Max); Add(c, "$last", (object?)r.WaitTimeUs.Last);
            Add(c, "$sd", (object?)r.WaitTimeUs.Stdev);
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
