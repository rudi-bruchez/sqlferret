// src/SqlFerret.Core/Analysis/ProjectComparison.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>
/// Compare deux projets, en lecture seule : une connexion en memoire, les deux fichiers attaches
/// READ_ONLY sous les alias constants base et target. Spec :
/// docs/superpowers/specs/2026-10-06-project-compare-design.md.
/// </summary>
public sealed class ProjectComparison(string baseDbPath, string targetDbPath)
{
    public const int SchemaVersion = 1;

    /// <summary>Colonnes lues par les sections. Un projet anterieur a une migration n'en a pas
    /// certaines, et l'attache READ_ONLY ne la fait pas tourner (§5).</summary>
    private static readonly (string Table, string Column)[] RequiredColumns =
    [
        ("executions", "run_id"), ("executions", "captured_at"), ("executions", "normalized_hash"),
        ("executions", "duration_us"), ("executions", "cpu_time_us"), ("executions", "logical_reads"),
        ("executions", "database_name"), ("executions", "query_hash"),
        ("normalized_queries", "normalized_hash"), ("normalized_queries", "normalized_sql"),
        ("normalized_queries", "statement_kind"), ("normalized_queries", "primary_table"),
        ("ingestion_runs", "run_id"), ("ingestion_runs", "normalizer_version"),
        ("ingestion_runs", "redaction_policy"), ("ingestion_runs", "sql_text_policy"),
        ("plan_profiles", "plan_profile_id"), ("plan_profiles", "plan_hash"),
        ("plan_profiles", "plan_hash_source"), ("plan_profiles", "query_hash"),
        ("plan_profiles", "duration_us"),
        ("plan_findings", "plan_profile_id"), ("plan_findings", "kind"),
        ("blocking_reports", "report_id"), ("blocking_reports", "run_id"),
        ("blocking_processes", "report_id"), ("blocking_processes", "inputbuf_fingerprint"),
    ];

    private static readonly (string Alias, int Side)[] Sides = [("base", 0), ("target", 1)];
    private string PathOf(int side) => side == 0 ? baseDbPath : targetDbPath;

    /// <summary>Le chemin est la seule entree interpolee : exception de CLAUDE.md, SQL safety.
    /// ATTACH refuse un parametre lie sur DuckDB.NET 1.5.3 ; une chaine DuckDB ne traite pas
    /// l'antislash comme un echappement, doubler l'apostrophe suffit.</summary>
    internal static string AttachSql(string path, string alias) =>
        $"ATTACH '{path.Replace("'", "''")}' AS {alias} (READ_ONLY)";

    /// <summary>
    /// Generation d'empreinte d'une version du normaliseur. La version couvre aussi la
    /// classification, donc deux versions peuvent produire les memes empreintes. Le commentaire
    /// de QueryNormalizer.Version dit que v4 laisse NormalizedSql, donc le fingerprint, inchange
    /// par rapport a v3. Une version qui change la reecriture des tokens ouvre une generation.
    /// </summary>
    internal static int FingerprintGeneration(int version) => version is 3 or 4 ? 3 : version;

    // "Could not set lock" est mesure sur Linux. Le libelle Windows n'est pas verifie : "used by
    // another process" est celui du systeme pour un partage refuse. S'il differe, le message
    // generique reste exact, sans pile, et sort en 1.
    internal static string DescribeAttachFailure(string dbPath, Exception ex) =>
        ex.Message.Contains("Could not set lock", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            ? $"compare: {dbPath} is open in another SQLFerret process (a TUI, an import); close it first"
            : $"compare: cannot attach {dbPath}: {ex.Message}";

    /// <summary>Attache les deux projets et verifie le §5 ; leve CompareRefusedException sinon.</summary>
    public void Check(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
    }

    /// <summary>Executions retenues d'un cote : une empreinte, une duree, la base filtree (§6).
    /// alias est constant, la base est liee.</summary>
    internal static string Execs(string alias, CompareOptions o) =>
        $"(SELECT * FROM {alias}.executions AS e WHERE e.normalized_hash IS NOT NULL AND e.duration_us IS NOT NULL{DbWhere(o)})";

    internal static string DbWhere(CompareOptions o) => o.Database is null ? "" : " AND e.database_name = $db";

    internal static void AddDb(System.Data.IDbCommand c, CompareOptions o)
    {
        if (o.Database is not null) Add(c, "$db", o.Database);
    }

    internal DuckDBConnection Open()
    {
        foreach (var (_, side) in Sides)
            if (!File.Exists(PathOf(side))) throw new CompareRefusedException($"compare: no project database at {PathOf(side)}");

        var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        foreach (var (alias, side) in Sides)
        {
            try { Exec(conn, AttachSql(PathOf(side), alias)); }
            catch (DuckDBException ex)
            {
                conn.Dispose();
                throw new CompareRefusedException(DescribeAttachFailure(PathOf(side), ex));
            }
        }
        return conn;
    }

    internal void CheckPreconditions(DuckDBConnection conn, CompareOptions options)
    {
        foreach (var (alias, side) in Sides)
        {
            var have = new HashSet<(string, string)>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT table_name, column_name FROM duckdb_columns() WHERE database_name = $db";
                Add(c, "$db", alias);
                using var r = c.ExecuteReader();
                while (r.Read()) have.Add((r.GetString(0), r.GetString(1)));
            }
            foreach (var need in RequiredColumns)
                if (!have.Contains(need))
                    throw new CompareRefusedException(
                        $"compare: {PathOf(side)} predates column {need.Table}.{need.Column}; run any other command on it once (for example top-slow) to migrate it");
        }

        foreach (var (alias, side) in Sides)
        {
            using var c = conn.CreateCommand();
            // Toutes les executions, avec ou sans duree (§5) : une capture faite seulement
            // d'executions sans duree doit atteindre la couverture, qui les compte (§6).
            c.CommandText = $"SELECT count(*) FROM {alias}.executions AS e WHERE e.normalized_hash IS NOT NULL{DbWhere(options)}";
            AddDb(c, options);
            if (Convert.ToInt64(c.ExecuteScalar()) == 0)
                throw new CompareRefusedException(
                    $"compare: {PathOf(side)} has no execution{(options.Database is null ? "" : $" in database {options.Database}")}");
        }

        // Les runs qui ont stocke des executions : ce sont eux qui ont calcule les empreintes
        // comparees. Un run sans execution (system_health, blocages seuls) n'en a stocke aucune.
        var versions = new List<(string Path, int Version)>();
        foreach (var (alias, side) in Sides)
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"""
                SELECT DISTINCT coalesce(r.normalizer_version, -1) AS v
                FROM {alias}.ingestion_runs AS r
                WHERE r.run_id IN (SELECT e.run_id FROM {alias}.executions AS e)
                """;
            using var rd = c.ExecuteReader();
            while (rd.Read()) versions.Add((PathOf(side), rd.GetInt32(0)));
        }
        if (versions.Select(v => FingerprintGeneration(v.Version)).Distinct().Count() > 1 || versions.Any(v => v.Version < 0))
            throw new CompareRefusedException(
                "compare: the projects hold fingerprints from different normalizer generations ("
                + string.Join(", ", versions.Distinct().Select(v => $"{v.Path}: v{v.Version}"))
                + "); re-import the older capture, since its stored hashes cannot be recomputed in place");
    }

    private static void Exec(DuckDBConnection conn, string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    internal static void Add(System.Data.IDbCommand c, string name, object? value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }

    public CompareCoverage CoverageOnly(CompareOptions options)
    {
        using var conn = Open();
        CheckPreconditions(conn, options);
        return Coverage(conn, options);
    }

    internal CompareCoverage Coverage(DuckDBConnection conn, CompareOptions options)
    {
        var b = Side(conn, "base", baseDbPath, options);
        var t = Side(conn, "target", targetDbPath, options);
        var notes = new List<string>();
        foreach (var (s, alias) in ((CompareSideCoverage, string)[])[(b, "base"), (t, "target")])
        {
            if (s.ActiveSpanUs < options.Thresholds.MinActiveSpanUs)
                notes.Add($"{s.ProjectDir}: active span under the threshold, per-hour figures are not computed");
            foreach (var runId in SplitRuns(conn, alias, options))
                notes.Add($"{s.ProjectDir}: run {runId} has a gap of more than a quarter of its span; it probably holds several disjoint captures, and its per-hour figures understate the load");
            if (s.ExecutionsWithoutDuration > 0)
                notes.Add($"{s.ProjectDir}: {s.ExecutionsWithoutDuration} executions without a duration are left out of every section");
            if (s.EligiblePlanProfiles == 0)
                notes.Add($"{s.ProjectDir}: no single-statement plan profile, the plan section is skipped");
        }
        notes.Add("SQLFerret does not know the predicates of the capture sessions; two captures with different duration thresholds have per-hour loads that cannot be compared");
        return new CompareCoverage(b, t, notes);
    }

    /// <summary>Les runs dont le plus grand trou depasse le quart de leur propre etendue. Chaque run
    /// se mesure contre la sienne : le run au plus grand trou absolu n'est pas forcement celui-la.</summary>
    private static List<long> SplitRuns(DuckDBConnection conn, string alias, CompareOptions o)
    {
        using var c = conn.CreateCommand();
        c.CommandText = $"""
            SELECT run_id FROM (
              SELECT run_id,
                     epoch_us(captured_at) - lag(epoch_us(captured_at)) OVER (PARTITION BY run_id ORDER BY captured_at) AS gap,
                     epoch_us(max(captured_at) OVER (PARTITION BY run_id)) - epoch_us(min(captured_at) OVER (PARTITION BY run_id)) AS span
              FROM {Execs(alias, o)} AS x) AS g
            GROUP BY run_id HAVING max(span) > 0 AND max(gap) * 4 > max(span) ORDER BY run_id
            """;
        AddDb(c, o);
        using var r = c.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private static CompareSideCoverage Side(DuckDBConnection conn, string alias, string dbPath, CompareOptions o)
    {
        var x = Execs(alias, o);
        var runs = new List<CompareRunSpan>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT run_id, min(captured_at) AS first_at, max(captured_at) AS last_at,
                       epoch_us(max(captured_at)) - epoch_us(min(captured_at)) AS span_us, count(*) AS n
                FROM {x} AS x GROUP BY run_id ORDER BY run_id
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            while (r.Read())
                runs.Add(new CompareRunSpan(r.GetInt64(0), r.GetDateTime(1), r.GetDateTime(2), r.GetInt64(3), r.GetInt64(4)));
        }

        long? gapUs = null, gapRun = null;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT run_id, gap FROM (
                  SELECT run_id, epoch_us(captured_at) - lag(epoch_us(captured_at))
                         OVER (PARTITION BY run_id ORDER BY captured_at) AS gap
                  FROM {x} AS x) AS g
                WHERE gap IS NOT NULL ORDER BY gap DESC, run_id LIMIT 1
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            if (r.Read()) { gapRun = r.GetInt64(0); gapUs = r.GetInt64(1); }
        }

        long execs, distinct; long? minDur; double qhShare;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT count(*) AS n, count(DISTINCT normalized_hash) AS d, min(duration_us) AS m,
                       avg(CASE WHEN query_hash IS NULL THEN 0.0 ELSE 1.0 END) AS s
                FROM {x} AS x
                """;
            AddDb(c, o);
            using var r = c.ExecuteReader();
            r.Read();
            execs = r.GetInt64(0); distinct = r.GetInt64(1);
            minDur = r.IsDBNull(2) ? null : r.GetInt64(2);
            qhShare = r.IsDBNull(3) ? 0 : r.GetDouble(3);
        }

        long noDuration;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT count(*) FROM {alias}.executions AS e WHERE e.normalized_hash IS NOT NULL AND e.duration_us IS NULL{DbWhere(o)}";
            AddDb(c, o);
            noDuration = Convert.ToInt64(c.ExecuteScalar());
        }

        var dbs = Strings(conn, $"SELECT DISTINCT database_name FROM {x} AS x WHERE database_name IS NOT NULL ORDER BY 1", o);
        var versions = Strings(conn, $"SELECT DISTINCT CAST(normalizer_version AS VARCHAR) AS v FROM {alias}.ingestion_runs WHERE run_id IN (SELECT run_id FROM {x} AS x) ORDER BY 1", o)
            .Select(int.Parse).ToList();
        var redaction = Strings(conn, $"SELECT DISTINCT coalesce(redaction_policy, 'unknown') AS p FROM {alias}.ingestion_runs WHERE run_id IN (SELECT run_id FROM {x} AS x) ORDER BY 1", o);
        var textPolicies = Strings(conn, $"SELECT DISTINCT coalesce(sql_text_policy, 'raw') AS p FROM {alias}.ingestion_runs ORDER BY 1", o);

        long eligible, excluded;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                SELECT count(*) FILTER (WHERE plan_hash_source = 'queryplanhash' AND query_hash IS NOT NULL) AS el,
                       count(*) FILTER (WHERE NOT (plan_hash_source = 'queryplanhash' AND query_hash IS NOT NULL)) AS ex
                FROM {alias}.plan_profiles
                """;
            using var r = c.ExecuteReader();
            r.Read();
            eligible = r.GetInt64(0); excluded = r.GetInt64(1);
        }

        return new CompareSideCoverage(
            Path.GetDirectoryName(dbPath) ?? dbPath, runs, runs.Sum(r => r.SpanUs), gapUs, gapRun,
            execs, noDuration, distinct, dbs.Take(o.Limit).ToList(), Math.Max(0, dbs.Count - o.Limit),
            versions, redaction, textPolicies, minDur, qhShare, eligible, excluded);
    }

    private static List<string> Strings(DuckDBConnection conn, string sql, CompareOptions o)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        if (sql.Contains("$db")) AddDb(c, o);
        using var r = c.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
