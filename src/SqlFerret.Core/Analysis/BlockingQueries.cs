// src/SqlFerret.Core/Analysis/BlockingQueries.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Analysis;

/// <summary>
/// Toutes les lectures se restreignent aux rapports declenches par seuil.
/// <para><c>blocking_reports.source</c> distingue un <c>blocked_process_report</c>, declenche quand
/// un blocage depasse le seuil, d'un instantane extrait d'un cycle sp_server_diagnostics pris a
/// cadence fixe. Les compter ensemble ferait qu'un blocage durable serait compte une fois par le
/// premier mecanisme et autant de fois qu'il y a eu de cycles par le second : tout classement par
/// <c>count(*)</c> favoriserait silencieusement la source qui echantillonne le plus.</para>
/// <para>Le predicat s'ecrit toujours <c>coalesce(source,'event')</c> : NULL designe une ligne
/// anterieure a la migration, et un <c>source = 'event'</c> nu les perdrait toutes.</para>
/// </summary>
public class BlockingQueries(DuckDBConnection conn)
{
    /// <summary>Jointure a poser sur toute lecture de <c>blocking_processes</c>, qui ne porte pas
    /// la colonne <c>source</c>.</summary>
    private const string EventOnly =
        "JOIN blocking_reports r ON r.report_id = bp.report_id AND coalesce(r.source, 'event') = 'event'";

    public BlockingOverview Overview()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT (SELECT count(*) FROM blocking_reports r WHERE coalesce(r.source,'event')='event'),
                 (SELECT count(*) FROM deadlock_reports),
                 (SELECT min(captured_at) FROM blocking_reports r WHERE coalesce(r.source,'event')='event'),
                 (SELECT max(captured_at) FROM blocking_reports r WHERE coalesce(r.source,'event')='event')
          """;
        using var r = c.ExecuteReader(); r.Read();
        return new BlockingOverview(r.GetInt64(0), r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3));
    }

    public IReadOnlyList<LocalityStat> Locality()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT bp.wait_resource_type, count(*) AS cnt,
                 100.0 * count(*) / NULLIF(sum(count(*)) OVER (), 0) AS pct
          FROM blocking_processes bp
          JOIN blocking_reports r ON r.report_id = bp.report_id
          WHERE bp.role='blocked' AND coalesce(r.source,'event')='event'
          GROUP BY bp.wait_resource_type ORDER BY cnt DESC
          """;
        using var r = c.ExecuteReader();
        var list = new List<LocalityStat>();
        while (r.Read()) list.Add(new LocalityStat(r.GetString(0), r.GetInt64(1), r.GetDouble(2)));
        return list;
    }

    public IReadOnlyList<ContentionStat> TopObjects(int limit)
        => CountBy($"SELECT CAST(bp.object_id AS TEXT), count(*) FROM blocking_processes bp {EventOnly} WHERE bp.role='blocked' AND bp.object_id IS NOT NULL GROUP BY bp.object_id ORDER BY 2 DESC LIMIT {limit}");

    public IReadOnlyList<ContentionStat> LockModes()
        => CountBy($"SELECT COALESCE(bp.lock_mode,'(none)'), count(*) FROM blocking_processes bp {EventOnly} WHERE bp.role='blocked' GROUP BY 1 ORDER BY 2 DESC");

    public IReadOnlyList<ContentionStat> IsolationLevels()
        => CountBy($"SELECT COALESCE(bp.isolation_level,'(none)'), count(*) FROM blocking_processes bp {EventOnly} WHERE bp.role='blocked' GROUP BY 1 ORDER BY 2 DESC");

    public IReadOnlyList<BlockingStat> TopBlockers(int limit) => Top(ProcessRole.Blocking, limit);
    public IReadOnlyList<BlockingStat> TopBlocked(int limit) => Top(ProcessRole.Blocked, limit);

    private enum ProcessRole { Blocked, Blocking }

    private IReadOnlyList<BlockingStat> Top(ProcessRole role, int limit)
    {
        // Map the enum to a compile-time constant — no arbitrary string can reach the SQL.
        string roleStr = role switch
        {
            ProcessRole.Blocked => "blocked",
            ProcessRole.Blocking => "blocking",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        using var c = conn.CreateCommand();
        c.CommandText = $"""
          SELECT bp.inputbuf_fingerprint,
                 COALESCE(nq.normalized_sql, bp.inputbuf, '(none)') AS sql,
                 count(*) AS cnt
          FROM blocking_processes bp
          {EventOnly}
          LEFT JOIN normalized_queries nq ON nq.normalized_hash = bp.inputbuf_fingerprint
          WHERE bp.role = '{roleStr}' AND bp.inputbuf_fingerprint IS NOT NULL
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
          SELECT COALESCE(quantile_cont(bp.wait_time_us, 0.5),0),
                 COALESCE(quantile_cont(bp.wait_time_us, 0.95),0),
                 COALESCE(max(bp.wait_time_us),0)
          FROM blocking_processes bp
          JOIN blocking_reports r ON r.report_id = bp.report_id
          WHERE bp.role='blocked' AND bp.wait_time_us IS NOT NULL
            AND coalesce(r.source,'event')='event'
          """;
        using var r = c.ExecuteReader(); r.Read();
        return new WaitTimeDist((long)r.GetDouble(0), (long)r.GetDouble(1), r.GetInt64(2));
    }

    public IReadOnlyList<ChainStat> Chains()
    {
        // La CTE se cle sur (source, monitor_loop).
        //
        // Surtout PAS sur report_id : SQL Server emet un rapport par processus BLOQUE, et
        // InsertBlockingBatch ecrit exactement un `blocked` et un `blocking` par rapport. Se cler
        // sur report_id donne donc une arete par groupe, toujours, et plafonne la profondeur a 2 —
        // ce qui supprime la reconstruction de chaine, qui est la raison d'etre de cette requete.
        // Une chaine 83 -> 72 -> 61 arrive comme deux rapports partageant leur monitor_loop, et
        // c'est precisement a cela que monitor_loop sert.
        //
        // `source` entre dans la cle parce que les deux mecanismes numerotent leurs boucles dans
        // le meme espace d'entiers sans aucun rapport entre eux ; le WHERE ci-dessous restreint en
        // plus aux rapports declenches par seuil.
        //
        // Reste un risque assume, anterieur a ce chantier : deux incidents sans rapport, eloignes
        // dans le temps, peuvent partager une valeur de monitor_loop et se voir relies. C'est le
        // seul groupement que la source nous donne.
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH RECURSIVE edges AS (
            SELECT coalesce(r.source, 'event') AS src, r.monitor_loop AS loop,
                   b.spid AS blocked_spid, k.spid AS blocking_spid
            FROM blocking_reports r
            JOIN blocking_processes b ON b.report_id=r.report_id AND b.role='blocked'
            JOIN blocking_processes k ON k.report_id=r.report_id AND k.role='blocking'
            WHERE coalesce(r.source, 'event') = 'event'
          ),
          heads AS (
            SELECT DISTINCT src, loop, blocking_spid AS spid FROM edges e
            WHERE NOT EXISTS (SELECT 1 FROM edges x
                              WHERE x.src=e.src AND x.loop IS NOT DISTINCT FROM e.loop
                                AND x.blocked_spid=e.blocking_spid)
          ),
          walk AS (
            SELECT src, loop, spid AS head, spid AS cur, 1 AS depth FROM heads
            UNION ALL
            SELECT w.src, w.loop, w.head, e.blocked_spid, w.depth+1
            FROM walk w JOIN edges e
              ON e.src=w.src AND e.loop IS NOT DISTINCT FROM w.loop AND e.blocking_spid=w.cur
            WHERE w.depth < 64
          )
          SELECT loop, max(depth) AS depth, head,
                 (SELECT count(*) FROM edges e
                   WHERE e.src=walk.src AND e.loop IS NOT DISTINCT FROM walk.loop) AS edges
          FROM walk GROUP BY src, loop, head ORDER BY depth DESC
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
          WHERE bp.inputbuf_fingerprint = $fp AND coalesce(r.source, 'event') = 'event'
          ORDER BY r.captured_at LIMIT $l
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
