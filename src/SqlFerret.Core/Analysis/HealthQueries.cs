// src/SqlFerret.Core/Analysis/HealthQueries.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>
/// Agregation des donnees de diagnostics, en SQL DuckDB.
/// <para>Le bloc de couverture vient en premier dans le digest, avant toute conclusion : une
/// capture system_health est un anneau, et sans lui un classement se lit comme s'il portait sur
/// toute la periode alors qu'il ne porte que sur ce que l'anneau a garde.</para>
/// </summary>
public class HealthQueries(DuckDBConnection conn)
{
    public HealthCoverage Coverage()
    {
        long cycles = 0;
        DateTime? first = null, last = null;
        double span = 0, median = 0, largest = 0;

        using (var c = conn.CreateCommand())
        {
            c.CommandText = """
              WITH g AS (
                SELECT cycle_at,
                       date_diff('millisecond', lag(cycle_at) OVER (ORDER BY cycle_at), cycle_at)
                         / 60000.0 AS gap
                FROM health_cycles
              )
              SELECT count(*), min(cycle_at), max(cycle_at),
                     coalesce(date_diff('millisecond', min(cycle_at), max(cycle_at)) / 60000.0, 0),
                     coalesce(quantile_cont(gap, 0.5), 0), coalesce(max(gap), 0)
              FROM g
              """;
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                cycles = r.GetInt64(0);
                first = r.IsDBNull(1) ? null : r.GetDateTime(1);
                last = r.IsDBNull(2) ? null : r.GetDateTime(2);
                span = r.GetDouble(3);
                median = r.GetDouble(4);
                largest = r.GetDouble(5);
            }
        }

        var series = new List<HealthSeries>();
        using (var c = conn.CreateCommand())
        {
            // Cadence calculee DANS chaque serie : entrelacer deux sessions ferait apparaitre une
            // cadence deux fois plus rapide que celle qu'aucune des deux n'a.
            c.CommandText = """
              WITH g AS (
                SELECT series_key, cycle_at,
                       date_diff('millisecond',
                         lag(cycle_at) OVER (PARTITION BY series_key ORDER BY cycle_at),
                         cycle_at) / 60000.0 AS gap
                FROM health_cycles
              )
              SELECT series_key, count(*), min(cycle_at), max(cycle_at),
                     coalesce(quantile_cont(gap, 0.5), 0)
              FROM g GROUP BY series_key ORDER BY series_key
              """;
            using var r = c.ExecuteReader();
            while (r.Read())
                series.Add(new HealthSeries(r.GetString(0), r.GetInt64(1),
                    r.GetDateTime(2), r.GetDateTime(3), r.GetDouble(4)));
        }

        double share = 0;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = """
              SELECT coalesce(sum(events_read), 0)::DOUBLE,
                     (coalesce(sum(events_server_diagnostics), 0)
                    + coalesce(sum(events_server_diagnostics_unhandled), 0)
                    + coalesce(sum(server_diagnostics_parse_failures), 0))::DOUBLE
              FROM ingestion_runs
              """;
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                double read = r.GetDouble(0), diag = r.GetDouble(1);
                share = read > 0 ? (read - diag) / read : 0;
            }
        }

        return new HealthCoverage(cycles, first, last, span, median, largest, share, series);
    }

    /// <summary>
    /// Les compteurs de topWaits sont cumulatifs depuis le demarrage de l'instance — mesure : sur
    /// 147 cycles d'une seule serie, 146 transitions croissantes et zero decroissante, valeurs
    /// autour de 8,9 milliards. Les sommer classerait le temps de fonctionnement, pas l'activite.
    /// <para>byCount uniquement : melanger les deux classements ferait apparaitre le meme type
    /// d'attente deux fois dans le top N, avec des chiffres qui ne se comparent pas.</para>
    /// </summary>
    public IReadOnlyList<WaitDelta> WaitDeltas(string seriesKey, int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH w AS (
            SELECT hw.wait_type, hw.preemptive, hw.waits,
                   hw.avg_wait_us, hw.max_wait_us, y.cycle_at
            FROM health_waits hw
            JOIN health_samples s ON s.sample_id = hw.sample_id
            JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
            WHERE y.series_key = $sk AND hw.ranking = 'byCount'
          ),
          d AS (
            SELECT wait_type, preemptive, cycle_at, avg_wait_us, max_wait_us,
                   waits - lag(waits) OVER (
                     PARTITION BY wait_type, preemptive ORDER BY cycle_at) AS step
            FROM w
          ),
          b AS (
            SELECT wait_type, preemptive,
                   -- Somme des seuls pas croissants. Un pas negatif ne peut signifier qu'un
                   -- redemarrage d'instance : on l'ecarte et on le compte, au lieu de le
                   -- compenser, ce qui ferait passer un redemarrage pour une periode calme.
                   coalesce(sum(step) FILTER (WHERE step >= 0), 0) AS delta,
                   count(*) FILTER (WHERE step < 0)                AS restarts,
                   min(cycle_at) AS first_at, max(cycle_at) AS last_at,
                   arg_max(avg_wait_us, cycle_at) AS avg_us,
                   arg_max(max_wait_us, cycle_at) AS max_us
            FROM d GROUP BY wait_type, preemptive
          )
          SELECT wait_type, preemptive, delta,
                 date_diff('millisecond', first_at, last_at) / 60000.0 AS span_min,
                 avg_us, max_us, restarts
          FROM b
          ORDER BY delta DESC
          LIMIT $lim
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$lim", limit);

        var list = new List<WaitDelta>();
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            long delta = r.GetInt64(2);
            double span = r.GetDouble(3);
            list.Add(new WaitDelta(r.GetString(0), r.GetBoolean(1), "byCount", delta, span,
                span > 0 ? delta / span : 0, r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        }
        return list;
    }

    /// <summary>Jauges instantanees : min / mediane / p95 / max, jamais une somme.</summary>
    public IReadOnlyList<HealthScalar> ScalarGauges(IReadOnlyList<string> names)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT name, min(v), quantile_cont(v, 0.5), quantile_cont(v, 0.95), max(v), count(*)
          FROM (SELECT name, coalesce(value_big::DOUBLE, value_num) AS v FROM health_metrics)
          WHERE v IS NOT NULL AND list_contains($names, name)
          GROUP BY name ORDER BY name
          """;
        Bind(c, "$names", names.ToList());

        var list = new List<HealthScalar>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new HealthScalar(r.GetString(0),
                r.IsDBNull(1) ? null : r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.GetInt64(5)));
        return list;
    }

    /// <summary>Compteurs cumulatifs : dernier moins premier, dans une seule serie.</summary>
    public IReadOnlyList<(string Name, long Delta)> ScalarDeltas(
        string seriesKey, IReadOnlyList<string> names)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT m.name,
                 greatest(arg_max(m.value_big, y.cycle_at) - arg_min(m.value_big, y.cycle_at), 0)
          FROM health_metrics m
          JOIN health_samples s ON s.sample_id = m.sample_id
          JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
          WHERE y.series_key = $sk AND m.value_big IS NOT NULL AND list_contains($names, m.name)
          GROUP BY m.name ORDER BY 2 DESC
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$names", names.ToList());

        var list = new List<(string, long)>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    private static void Bind(System.Data.Common.DbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
