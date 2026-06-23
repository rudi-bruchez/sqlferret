// src/SqlFerret.Core/Analysis/BlockingQueries.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Analysis;

public class BlockingQueries(DuckDBConnection conn)
{
    public BlockingOverview Overview()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT (SELECT count(*) FROM blocking_reports),
                 (SELECT count(*) FROM deadlock_reports),
                 (SELECT min(captured_at) FROM blocking_reports),
                 (SELECT max(captured_at) FROM blocking_reports)
          """;
        using var r = c.ExecuteReader(); r.Read();
        return new BlockingOverview(r.GetInt64(0), r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3));
    }

    public IReadOnlyList<LocalityStat> Locality()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT wait_resource_type, count(*) AS cnt,
                 100.0 * count(*) / NULLIF(sum(count(*)) OVER (), 0) AS pct
          FROM blocking_processes WHERE role='blocked'
          GROUP BY wait_resource_type ORDER BY cnt DESC
          """;
        using var r = c.ExecuteReader();
        var list = new List<LocalityStat>();
        while (r.Read()) list.Add(new LocalityStat(r.GetString(0), r.GetInt64(1), r.GetDouble(2)));
        return list;
    }

    public IReadOnlyList<ContentionStat> TopObjects(int limit)
        => CountBy($"SELECT CAST(object_id AS TEXT), count(*) FROM blocking_processes WHERE role='blocked' AND object_id IS NOT NULL GROUP BY object_id ORDER BY 2 DESC LIMIT {limit}");

    public IReadOnlyList<ContentionStat> LockModes()
        => CountBy("SELECT COALESCE(lock_mode,'(none)'), count(*) FROM blocking_processes WHERE role='blocked' GROUP BY 1 ORDER BY 2 DESC");

    public IReadOnlyList<ContentionStat> IsolationLevels()
        => CountBy("SELECT COALESCE(isolation_level,'(none)'), count(*) FROM blocking_processes WHERE role='blocked' GROUP BY 1 ORDER BY 2 DESC");

    public IReadOnlyList<BlockingStat> TopBlockers(int limit) => Top("blocking", limit);
    public IReadOnlyList<BlockingStat> TopBlocked(int limit) => Top("blocked", limit);

    private IReadOnlyList<BlockingStat> Top(string role, int limit)
    {
        using var c = conn.CreateCommand();
        // role is a hard-coded literal ('blocked'|'blocking'), never user input; limit is an int
        c.CommandText = $"""
          SELECT bp.inputbuf_fingerprint,
                 COALESCE(nq.normalized_sql, bp.inputbuf, '(none)') AS sql,
                 count(*) AS cnt
          FROM blocking_processes bp
          LEFT JOIN normalized_queries nq ON nq.normalized_hash = bp.inputbuf_fingerprint
          WHERE bp.role = '{role}' AND bp.inputbuf_fingerprint IS NOT NULL
          GROUP BY bp.inputbuf_fingerprint, sql ORDER BY cnt DESC LIMIT {limit}
          """;
        using var r = c.ExecuteReader();
        var list = new List<BlockingStat>();
        while (r.Read()) list.Add(new BlockingStat(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    public WaitTimeDist WaitTimes()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT COALESCE(quantile_cont(wait_time_us, 0.5),0),
                 COALESCE(quantile_cont(wait_time_us, 0.95),0),
                 COALESCE(max(wait_time_us),0)
          FROM blocking_processes WHERE role='blocked' AND wait_time_us IS NOT NULL
          """;
        using var r = c.ExecuteReader(); r.Read();
        return new WaitTimeDist((long)r.GetDouble(0), (long)r.GetDouble(1), r.GetInt64(2));
    }

    public IReadOnlyList<ChainStat> Chains()
    {
        // Edges: within a report, blocked.spid waits on blocking.spid. Head = a blocking spid that is
        // never itself blocked in the same monitor_loop. Depth via recursive CTE over (loop, from->to).
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH edges AS (
            SELECT r.monitor_loop AS loop, b.spid AS blocked_spid, k.spid AS blocking_spid
            FROM blocking_reports r
            JOIN blocking_processes b ON b.report_id=r.report_id AND b.role='blocked'
            JOIN blocking_processes k ON k.report_id=r.report_id AND k.role='blocking'
          ),
          heads AS (
            SELECT DISTINCT loop, blocking_spid AS spid FROM edges e
            WHERE NOT EXISTS (SELECT 1 FROM edges x WHERE x.loop=e.loop AND x.blocked_spid=e.blocking_spid)
          ),
          walk AS (
            SELECT loop, spid AS head, spid AS cur, 1 AS depth FROM heads
            UNION ALL
            SELECT w.loop, w.head, e.blocked_spid, w.depth+1
            FROM walk w JOIN edges e ON e.loop=w.loop AND e.blocking_spid=w.cur
          )
          SELECT loop, max(depth) AS depth, head, (SELECT count(*) FROM edges e WHERE e.loop=walk.loop) AS edges
          FROM walk GROUP BY loop, head ORDER BY depth DESC
          """;
        using var r = c.ExecuteReader();
        var list = new List<ChainStat>();
        while (r.Read())
            list.Add(new ChainStat(r.IsDBNull(0) ? null : r.GetInt32(0), (int)r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt64(3)));
        return list;
    }

    public IReadOnlyList<BlockingReport> SampleReports(string fingerprint, int limit)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT r.report_id, r.captured_at, r.monitor_loop, r.database_id
          FROM blocking_reports r
          JOIN blocking_processes bp ON bp.report_id=r.report_id AND bp.role='blocking'
          WHERE bp.inputbuf_fingerprint = $fp ORDER BY r.captured_at LIMIT $l
          """;
        Add(c, "$fp", fingerprint); Add(c, "$l", limit);
        var ids = new List<(long id, DateTime ts, int? loop, int? db)>();
        using (var r = c.ExecuteReader())
            while (r.Read()) ids.Add((r.GetInt64(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3)));
        var list = new List<BlockingReport>();
        foreach (var (id, ts, loop, db) in ids)
            list.Add(new BlockingReport(ts, loop, db, LoadProc(id, "blocked"), LoadProc(id, "blocking")));
        return list;
    }

    private BlockingProcess LoadProc(long reportId, string role)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT spid, ecid, status, wait_resource_raw, wait_resource_type, object_id, hobt_id,
                 wait_time_us, lock_mode, isolation_level, trancount, client_app, host_name, login_name,
                 inputbuf, inputbuf_fingerprint
          FROM blocking_processes WHERE report_id=$id AND role=$role
          """;
        Add(c, "$id", reportId); Add(c, "$role", role);
        using var r = c.ExecuteReader();
        if (!r.Read()) return new BlockingProcess(null, null, null, null, WaitResourceType.Other, null, null, null, null, null, null, null, null, null, null, null);
        return new BlockingProcess(
            r.IsDBNull(0) ? null : r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), Enum.Parse<WaitResourceType>(r.GetString(4)),
            r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7),
            r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt32(10),
            r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
            r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15));
    }

    private IReadOnlyList<ContentionStat> CountBy(string sql)
    {
        using var c = conn.CreateCommand(); c.CommandText = sql;
        using var r = c.ExecuteReader();
        var list = new List<ContentionStat>();
        while (r.Read()) list.Add(new ContentionStat(r.IsDBNull(0) ? "(none)" : r.GetString(0), r.GetInt64(1)));
        return list;
    }

    private static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter(); p.ParameterName = name.TrimStart('$'); p.Value = value ?? DBNull.Value; c.Parameters.Add(p);
    }
}
