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
        double span = 0, median = 0, largest = 0, smallest = 0;

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
                     coalesce(quantile_cont(gap, 0.5), 0), coalesce(max(gap), 0),
                     coalesce(min(gap), 0)
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
                smallest = r.GetDouble(6);
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
              -- Seuls les runs qui ont ingere des diagnostics. Ce chiffre decrit la CAPTURE et
              -- non le projet : un projet qui contient aussi une trace de charge — les imports
              -- s'ajoutent — diluait la part jusqu'a la vider de sens.
              WHERE coalesce(events_server_diagnostics, 0)
                  + coalesce(events_server_diagnostics_unhandled, 0)
                  + coalesce(server_diagnostics_parse_failures, 0) > 0
              """;
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                double read = r.GetDouble(0), diag = r.GetDouble(1);
                share = read > 0 ? (read - diag) / read : 0;
            }
        }

        // Ecarts heterogenes : le plus souvent deux sessions enregistrant le meme serveur. On le
        // signale sans pretendre attribuer chaque cycle a l'une d'elles.
        bool irregular = cycles >= 4 && smallest > 0 && largest / smallest > 2.5;

        return new HealthCoverage(
            cycles, first, last, span, median, largest, share, irregular, series);
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

    /// <summary>
    /// Microsoft documente cinq composants : system, resource, query_processing, io_subsystem et
    /// events. Tout autre nom est celui d'un groupe de disponibilite, donc du nommage choisi par
    /// le client — unite d'affaires, application, parfois le client lui-meme. Il est stocke
    /// verbatim sous toutes les politiques, y compris <c>full</c>, et imprime dans le digest.
    /// </summary>
    public IReadOnlyList<string> NonStandardComponents()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT DISTINCT component FROM health_samples
          WHERE lower(component) NOT IN
                ('system','resource','query_processing','io_subsystem','events')
          ORDER BY component
          """;
        var list = new List<string>();
        using var r = c.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// Compteurs d'intervalle : chaque cycle porte le nombre survenu DANS son intervalle, et non
    /// un cumul depuis le demarrage. L'agregat est donc une somme directe. Les differencier — ce
    /// que faisait <see cref="ScalarDeltas"/> — calcule une acceleration : un plateau constant
    /// rend zero, et le filtre du digest le fait alors disparaitre.
    /// <para>Non verifie : Microsoft Learn documente la sortie de <c>sp_server_diagnostics</c>
    /// mais ne definit la semantique d'aucun de ces attributs. Le decoupage repose sur le nom et
    /// sur la spec §6.</para>
    /// </summary>
    public IReadOnlyList<ScalarDelta> IntervalSums(
        string seriesKey, IReadOnlyList<string> names)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT m.name, coalesce(sum(m.value_big), 0) AS total
          FROM health_metrics m
          JOIN health_samples s ON s.sample_id = m.sample_id
          JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
          WHERE y.series_key = $sk AND m.value_big IS NOT NULL AND list_contains($names, m.name)
          GROUP BY m.name ORDER BY total DESC
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$names", names.ToList());

        var list = new List<ScalarDelta>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new ScalarDelta(r.GetString(0), r.GetInt64(1)));
        return list;
    }

    /// <summary>
    /// Compteurs cumulatifs. Somme des seuls pas croissants, comme <see cref="WaitDeltas"/> : un
    /// simple dernier-moins-premier perdrait toute l'activite anterieure a un redemarrage
    /// d'instance, et un <c>greatest(..., 0)</c> la ferait disparaitre en silence.
    /// </summary>
    public IReadOnlyList<ScalarDelta> ScalarDeltas(
        string seriesKey, IReadOnlyList<string> names)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH v AS (
            SELECT m.name, y.cycle_at, m.value_big,
                   m.value_big - lag(m.value_big) OVER (PARTITION BY m.name ORDER BY y.cycle_at) AS step
            FROM health_metrics m
            JOIN health_samples s ON s.sample_id = m.sample_id
            JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
            WHERE y.series_key = $sk AND m.value_big IS NOT NULL AND list_contains($names, m.name)
          )
          SELECT name, coalesce(sum(step) FILTER (WHERE step >= 0), 0) AS delta
          FROM v GROUP BY name ORDER BY delta DESC
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$names", names.ToList());

        var list = new List<ScalarDelta>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new ScalarDelta(r.GetString(0), r.GetInt64(1)));
        return list;
    }

    /// <summary>Spec §9 item 1. Un etat autre que CLEAN est le premier endroit ou regarder.</summary>
    public IReadOnlyList<StateCount> NonCleanStates()
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          -- Pas de filtre sur `handled` : `state` est le seul champ que l'outil comprend pour
          -- TOUT composant, et ne pas savoir lire un payload n'est pas une raison de jeter l'etat.
          -- Un composant de groupe de disponibilite en WARNING doit remonter.
          SELECT component, state, count(*) AS cycles
          FROM health_samples
          WHERE state IS NOT NULL AND upper(state) <> 'CLEAN'
          GROUP BY component, state
          ORDER BY cycles DESC, component
          """;
        var list = new List<StateCount>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new StateCount(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    /// <summary>
    /// Spec §9 item 3. Une seule serie : comparer un premier et un dernier a travers deux series
    /// entrelacees comparerait deux instants sans rapport.
    /// </summary>
    public IReadOnlyList<MemoryMovement> MemoryMovers(string seriesKey, int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          WITH e AS (
            SELECT m.report_name, m.unit, m.description, m.value_num, y.cycle_at
            FROM health_memory_entries m
            JOIN health_samples s ON s.sample_id = m.sample_id
            JOIN health_cycles  y ON y.cycle_id  = s.cycle_id
            WHERE y.series_key = $sk AND m.value_num IS NOT NULL
          )
          SELECT report_name, any_value(unit), description,
                 arg_min(value_num, cycle_at) AS first_v,
                 arg_max(value_num, cycle_at) AS last_v
          FROM e GROUP BY report_name, description
          ORDER BY abs(arg_max(value_num, cycle_at) - arg_min(value_num, cycle_at)) DESC
          LIMIT $lim
          """;
        Bind(c, "$sk", seriesKey); Bind(c, "$lim", limit);
        var list = new List<MemoryMovement>();
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            double first = r.GetDouble(3), last = r.GetDouble(4);
            list.Add(new MemoryMovement(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2), first, last, last - first));
        }
        return list;
    }

    /// <summary>
    /// Spec §9 item 6. file_path est stocke verbatim et n'est retire par aucune politique de
    /// redaction : l'hote qui l'affiche doit le dire.
    /// </summary>
    public IReadOnlyList<PendingIoRow> WorstPendingIo(int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT s.captured_at, i.duration_us, i.file_path, i.handle
          FROM health_pending_io i
          JOIN health_samples s ON s.sample_id = i.sample_id
          ORDER BY i.duration_us DESC NULLS LAST
          LIMIT $lim
          """;
        Bind(c, "$lim", limit);
        var list = new List<PendingIoRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new PendingIoRow(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>
    /// Spec §9 item 7. Les instantanes de diagnostics uniquement, jamais melanges aux rapports
    /// declenches par seuil.
    /// </summary>
    public IReadOnlyList<DiagnosticsBlockingRow> DiagnosticsBlocking(int limit = 10)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
          SELECT r.captured_at, b.spid, k.spid, b.wait_time_us, b.wait_resource_type, b.inputbuf
          FROM blocking_reports r
          JOIN blocking_processes b ON b.report_id = r.report_id AND b.role = 'blocked'
          JOIN blocking_processes k ON k.report_id = r.report_id AND k.role = 'blocking'
          WHERE coalesce(r.source, 'event') = 'diagnostics'
          ORDER BY b.wait_time_us DESC NULLS LAST
          LIMIT $lim
          """;
        Bind(c, "$lim", limit);
        var list = new List<DiagnosticsBlockingRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new DiagnosticsBlockingRow(r.GetDateTime(0),
                r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
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
