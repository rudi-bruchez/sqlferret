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

    internal const string TextWithheld = "(text withheld: first imported under raw)";

    internal static bool MustSanitize(CompareCoverage c) =>
        c.Base.SqlTextPolicies.Concat(c.Target.SqlTextPolicies).Any(p => p != "raw");

    /// <summary>
    /// Le texte imprime pour chaque empreinte, en CTE compare_texts(h, txt). Les deux ecrivains de
    /// normalized_queries (l'upsert des executions, l'insert des blocages) gardent le premier
    /// texte ; la politique est par run. Le texte d'un cote n'est donc fiable que si le premier
    /// run de ce cote qui a rencontre l'empreinte etait en literals. Rien n'est re-parse : un
    /// texte normalise porte des ?, que ScriptDom ne parse pas (spec §7, revision 3).
    /// </summary>
    internal static string TextsCte(bool sanitize)
    {
        if (!sanitize)
            return """
                compare_texts AS (
                  SELECT a.h AS h, coalesce(tq.normalized_sql, bq.normalized_sql) AS txt
                  FROM (SELECT normalized_hash AS h FROM base.normalized_queries
                        UNION SELECT normalized_hash AS h FROM target.normalized_queries) AS a
                  LEFT JOIN base.normalized_queries AS bq ON bq.normalized_hash = a.h
                  LEFT JOIN target.normalized_queries AS tq ON tq.normalized_hash = a.h)
                """;

        static string Trusted(string alias) => $"""
            SELECT o.h AS h, q.normalized_sql AS txt
            FROM (SELECT s.h AS h, min(s.run_id) AS r FROM (
                    SELECT normalized_hash AS h, run_id AS run_id FROM {alias}.executions WHERE normalized_hash IS NOT NULL
                    UNION ALL
                    SELECT p.inputbuf_fingerprint AS h, br.run_id AS run_id
                    FROM {alias}.blocking_processes AS p JOIN {alias}.blocking_reports AS br ON br.report_id = p.report_id
                    WHERE p.inputbuf_fingerprint IS NOT NULL) AS s
                  GROUP BY s.h) AS o
            JOIN {alias}.ingestion_runs AS r ON r.run_id = o.r
            JOIN {alias}.normalized_queries AS q ON q.normalized_hash = o.h
            WHERE r.sql_text_policy = 'literals'
            """;

        return $"""
            compare_texts AS (
              SELECT a.h AS h, coalesce(tt.txt, tb.txt, '{TextWithheld}') AS txt
              FROM (SELECT normalized_hash AS h FROM base.normalized_queries
                    UNION SELECT normalized_hash AS h FROM target.normalized_queries) AS a
              LEFT JOIN ({Trusted("base")}) AS tb ON tb.h = a.h
              LEFT JOIN ({Trusted("target")}) AS tt ON tt.h = a.h)
            """;
    }

    public string? TextOf(CompareOptions o, string hash)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        var sanitize = MustSanitize(Coverage(conn, o));
        using var c = conn.CreateCommand();
        c.CommandText = $"WITH {TextsCte(sanitize)} SELECT txt FROM compare_texts WHERE h = $h";
        Add(c, "$h", hash);
        return c.ExecuteScalar() as string;
    }

    private const double UsPerHour = 3_600_000_000d;

    public (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? LoadOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        var cov = Coverage(conn, o);
        return Load(conn, o, cov, MustSanitize(cov));
    }

    public (OneSideList Appeared, OneSideList Disappeared) OneSidedOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        var cov = Coverage(conn, o);
        return OneSided(conn, o, cov, MustSanitize(cov));
    }

    internal (IReadOnlyList<LoadRow> Up, IReadOnlyList<LoadRow> Down)? Load(
        DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)
    {
        var min = o.Thresholds.MinActiveSpanUs;
        if (cov.Base.ActiveSpanUs < min || cov.Target.ActiveSpanUs < min) return null;

        List<LoadRow> Read(string filter, string order)
        {
            using var c = conn.CreateCommand();
            // filter et order sont des constantes de ce fichier, jamais une entree.
            c.CommandText = $"""
                WITH {TextsCte(sanitize)},
                     b AS (SELECT normalized_hash AS h, count(*) AS n, sum(duration_us)::DOUBLE AS d FROM {Execs("base", o)} AS x GROUP BY 1),
                     t AS (SELECT normalized_hash AS h, count(*) AS n, sum(duration_us)::DOUBLE AS d FROM {Execs("target", o)} AS x GROUP BY 1)
                SELECT b.h AS h, q.statement_kind AS kind, q.primary_table AS tbl, ct.txt AS txt,
                       b.n * $bf AS bn, t.n * $tf AS tn, b.d * $bf AS bd, t.d * $tf AS td, t.d * $tf - b.d * $bf AS delta
                FROM b JOIN t ON b.h = t.h
                JOIN target.normalized_queries AS q ON q.normalized_hash = b.h
                JOIN compare_texts AS ct ON ct.h = b.h
                WHERE greatest(b.n, t.n) >= $minExec AND {filter}
                ORDER BY {order}, b.h
                LIMIT $lim
                """;
            AddDb(c, o);
            Add(c, "$bf", UsPerHour / cov.Base.ActiveSpanUs);
            Add(c, "$tf", UsPerHour / cov.Target.ActiveSpanUs);
            Add(c, "$minExec", o.Thresholds.MinExecutions);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var list = new List<LoadRow>();
            while (r.Read())
                list.Add(new LoadRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8)));
            return list;
        }

        return (Read("delta > 0", "delta DESC"), Read("delta < 0", "delta ASC"));
    }

    internal (OneSideList Appeared, OneSideList Disappeared) OneSided(
        DuckDBConnection conn, CompareOptions o, CompareCoverage cov, bool sanitize)
    {
        bool perHour = cov.Base.ActiveSpanUs >= o.Thresholds.MinActiveSpanUs
                    && cov.Target.ActiveSpanUs >= o.Thresholds.MinActiveSpanUs;

        OneSideList List(string present, string absent, long spanUs)
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"""
                WITH {TextsCte(sanitize)},
                     p AS (SELECT normalized_hash AS h, count(*) AS n, sum(duration_us)::BIGINT AS d FROM {Execs(present, o)} AS x GROUP BY 1),
                     a AS (SELECT DISTINCT normalized_hash AS h FROM {Execs(absent, o)} AS x)
                SELECT p.h AS h, q.statement_kind AS kind, q.primary_table AS tbl, ct.txt AS txt,
                       p.n AS n, p.d AS d, count(*) OVER () AS total
                FROM p JOIN {present}.normalized_queries AS q ON q.normalized_hash = p.h
                JOIN compare_texts AS ct ON ct.h = p.h
                WHERE p.h NOT IN (SELECT h FROM a)
                ORDER BY p.d DESC, p.h
                LIMIT $lim
                """;
            AddDb(c, o);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var rows = new List<OneSideRow>();
            long total = 0;
            while (r.Read())
            {
                long d = r.GetInt64(5);
                rows.Add(new OneSideRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3), r.GetInt64(4), d, perHour ? d * UsPerHour / spanUs : null));
                total = r.GetInt64(6);
            }
            return new OneSideList(rows, total);
        }

        return (List("target", "base", cov.Target.ActiveSpanUs), List("base", "target", cov.Base.ActiveSpanUs));
    }

    public (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) CostOnly(CompareOptions o)
    {
        using var conn = Open();
        CheckPreconditions(conn, o);
        return Cost(conn, o, MustSanitize(Coverage(conn, o)));
    }

    internal (IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains) Cost(
        DuckDBConnection conn, CompareOptions o, bool sanitize)
    {
        string Agg(string alias) => $"""
            SELECT normalized_hash AS h, count(*) AS n, avg(duration_us) AS a,
                   quantile_cont(duration_us, 0.95) AS p, avg(cpu_time_us) AS c, avg(logical_reads) AS r
            FROM {Execs(alias, o)} AS x GROUP BY 1
            """;

        List<CostRow> Read(string filter, string order)
        {
            using var c = conn.CreateCommand();
            // filter et order sont des constantes de ce fichier.
            c.CommandText = $"""
                WITH {TextsCte(sanitize)},
                     b AS ({Agg("base")}), t AS ({Agg("target")}),
                     j AS (SELECT b.h AS h, b.n AS bn, t.n AS tn, b.a AS ba, t.a AS ta, b.p AS bp, t.p AS tp,
                                  b.c AS bc, t.c AS tc, b.r AS br, t.r AS tr,
                                  CASE WHEN b.a = 0 THEN NULL ELSE t.a / b.a END AS ratio
                           FROM b JOIN t ON b.h = t.h
                           WHERE b.n >= $minExec AND t.n >= $minExec AND greatest(b.a, t.a) >= $minAvg)
                SELECT j.h AS h, q.statement_kind AS kind, q.primary_table AS tbl, ct.txt AS txt,
                       bn, tn, ba, ta, bp, tp, bc, tc, br, tr, ratio
                FROM j JOIN target.normalized_queries AS q ON q.normalized_hash = j.h
                JOIN compare_texts AS ct ON ct.h = j.h
                WHERE {filter}
                ORDER BY {order}, j.h
                LIMIT $lim
                """;
            AddDb(c, o);
            Add(c, "$minExec", o.Thresholds.MinExecutions);
            Add(c, "$minAvg", o.Thresholds.MinAvgDurationUs);
            Add(c, "$lim", o.Limit);
            using var r = c.ExecuteReader();
            var list = new List<CostRow>();
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            while (r.Read())
                list.Add(new CostRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3), r.GetInt64(4), r.GetInt64(5),
                    r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9),
                    D(10), D(11), D(12), D(13), D(14)));
            return list;
        }

        // Une moyenne de base nulle donne un ratio NULL, classe en tete des regressions.
        return (Read("(ratio > 1 OR ratio IS NULL)", "(ratio IS NULL) DESC, ratio DESC, ta DESC"),
                Read("ratio < 1", "ratio ASC"));
    }
}
