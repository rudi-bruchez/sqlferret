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

    private static void Bind(System.Data.Common.DbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
