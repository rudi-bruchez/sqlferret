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
}
