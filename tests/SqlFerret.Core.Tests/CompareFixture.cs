// tests/SqlFerret.Core.Tests/CompareFixture.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

/// <summary>
/// Un projet de test : un dossier temporaire et son sqlferret.duckdb, rempli par les chemins
/// d'insertion reels. Le texte passe par SqlTextSanitizer.Apply comme dans IngestionService :
/// la revision 1 du plan inserait le texte brut, ce qui cachait que la regle de texte detruisait
/// les textes reels.
/// </summary>
public sealed class CompareFixture : IDisposable
{
    public string Dir { get; }
    public string DbPath => Path.Combine(Dir, "sqlferret.duckdb");

    /// <param name="suffix">Ajoute au nom du dossier, pour un chemin qui contient une apostrophe.</param>
    public CompareFixture(string suffix = "")
    {
        Dir = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}{suffix}");
        Directory.CreateDirectory(Dir);
    }

    public record Exec(string Hash, string Sql, long? DurationUs, DateTime At,
        string Db = "AppDb", string? QueryHash = null, long? CpuUs = null, long? Reads = null);

    /// <summary>Un import : un run, ses executions. Rend le run_id.</summary>
    public long Import(IEnumerable<Exec> execs, SqlTextSanitization policy = SqlTextSanitization.Raw,
        string redaction = "off")
    {
        using var p = DuckDbProject.Open(DbPath);
        var run = p.BeginRun("logs/", 1, 100, redaction, policy);
        var rows = execs.Select(e =>
        {
            var nq = QueryNormalizer.Normalize(e.Sql) with { NormalizedHash = e.Hash };
            var (text, stored, _) = SqlTextSanitizer.Apply(e.Sql, nq, policy);
            return new PreparedRow(
                new ExecutionEvent
                {
                    EventName = "rpc_completed",
                    EventClass = EventClass.RpcCall,
                    ObjectName = "AppSchema.WidgetRecalc",
                    SqlTextRaw = text,
                    DatabaseName = e.Db,
                    SessionId = 52,
                    DurationUs = e.DurationUs,
                    CpuTimeUs = e.CpuUs,
                    LogicalReads = e.Reads,
                    QueryHash = e.QueryHash,
                    CapturedAt = e.At,
                    XeFileName = "s_0.xel",
                },
                stored, []);
        }).ToList();
        p.InsertBatch(run, rows);
        return run;
    }

    /// <summary>
    /// Un run qui ne contient qu'un blocked-process report, dont l'input buffer porte l'empreinte
    /// hash. Ecrit les memes lignes que InsertBlockingBatch pour ce que compare lit :
    /// blocking_reports, blocking_processes, et la signature, premier ecrit gagnant.
    /// </summary>
    public long BlockingOnly(string hash, string sql, SqlTextSanitization policy)
    {
        using var p = DuckDbProject.Open(DbPath);
        var run = p.BeginRun("logs/", 1, 1, "off", policy);
        var nq = QueryNormalizer.Normalize(sql) with { NormalizedHash = hash };
        var stored = SqlTextSanitizer.Apply(sql, nq, policy).Normalized;
        var ts = new DateTime(2026, 1, 1, 8, 0, 0);
        void X(string text, params (string Name, object Value)[] ps)
        {
            using var c = p.Connection.CreateCommand();
            c.CommandText = text;
            foreach (var (n, v) in ps)
            {
                var prm = c.CreateParameter(); prm.ParameterName = n; prm.Value = v; c.Parameters.Add(prm);
            }
            c.ExecuteNonQuery();
        }
        X("INSERT INTO blocking_reports (report_id, run_id, captured_at) VALUES ($id, $run, $ts)",
          ("id", run * 1000), ("run", run), ("ts", ts));
        X("INSERT INTO blocking_processes (report_id, role, inputbuf_fingerprint) VALUES ($id, 'blocked', $h)",
          ("id", run * 1000), ("h", hash));
        X("""
          INSERT INTO normalized_queries (normalized_hash, normalized_sql, statement_kind, normalizer_version, first_seen_at, last_seen_at)
          VALUES ($h, $sql, 'SELECT', 4, $ts, $ts) ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at
          """, ("h", hash), ("sql", stored.NormalizedSql), ("ts", ts));
        return run;
    }

    /// <summary>Ajoute des plan profiles a un run existant.</summary>
    public void Plans(long runId, params PlanProfile[] profiles)
    {
        using var p = DuckDbProject.Open(DbPath);
        p.InsertPlanProfileBatch(runId, profiles.Select(x => new PreparedPlanProfile(x, PlanWriteOutcome.WroteFirst)).ToList());
    }

    public static PlanProfile Plan(string queryHash, string planHash, long durationUs,
        string source = "queryplanhash", params PlanFinding[] findings) => new()
        {
            PlanHash = planHash,
            PlanHashSource = source,
            FileStem = "p_" + planHash,
            StatementCount = source == "multi" ? 2 : 1,
            QueryHash = queryHash,
            StatementType = "SELECT",
            StatementText = "SELECT * FROM AppSchema.WidgetRecalc WHERE WidgetId = 4242",
            StatementTextLength = 58,
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DurationUs = durationUs,
            Findings = findings,
        };

    /// <summary>Ecriture directe, pour simuler un etat ancien (version, colonne manquante).</summary>
    public void Sql(string sql)
    {
        using var p = DuckDbProject.Open(DbPath);
        using var c = p.Connection.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
}
