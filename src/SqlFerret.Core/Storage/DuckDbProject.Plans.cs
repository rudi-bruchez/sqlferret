// src/SqlFerret.Core/Storage/DuckDbProject.Plans.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Plans;

namespace SqlFerret.Core.Storage;

public sealed partial class DuckDbProject
{
    private long _nextPlanProfileId = -1;

    private static void CreatePlanSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS plan_profiles (
          plan_profile_id BIGINT PRIMARY KEY, run_id BIGINT, captured_at TIMESTAMP,
          plan_hash TEXT, plan_hash_source TEXT, file_stem TEXT, statement_count INTEGER,
          query_hash TEXT, statement_type TEXT,
          statement_text TEXT, statement_text_length INTEGER,
          duration_us BIGINT, cpu_time_us BIGINT, estimated_rows DOUBLE, subtree_cost DOUBLE,
          dop INTEGER, serial_desired_memory_kb BIGINT, granted_memory_kb BIGINT,
          max_used_memory_kb BIGINT,
          write_outcome TEXT,
          sqlplan_path TEXT, digest_path TEXT, is_first BOOLEAN, is_worst BOOLEAN);

        CREATE TABLE IF NOT EXISTS plan_findings (
          plan_profile_id BIGINT, kind TEXT, node_id INTEGER, detail_json TEXT);

        CREATE INDEX IF NOT EXISTS ix_plan_profiles_captured_at ON plan_profiles (captured_at);
        CREATE INDEX IF NOT EXISTS ix_plan_findings_profile     ON plan_findings (plan_profile_id);
        """;
        cmd.ExecuteNonQuery();
    }

    public long NextPlanProfileId()
    {
        if (_nextPlanProfileId < 0)
            _nextPlanProfileId = Scalar("SELECT COALESCE(MAX(plan_profile_id),0) FROM plan_profiles");
        return ++_nextPlanProfileId;
    }

    /// <summary>
    /// Seule écriture composite de la fonctionnalité : une ligne plan_profiles puis ses
    /// enfants plan_findings, dans la même transaction — comme InsertBatch pour
    /// executions / execution_parameters.
    /// sqlplan_path reste NULL : il est renseigné par FinalizePlanRun.
    /// write_outcome est persisté parce que la base ne peut pas deviner ce que le writer
    /// a réellement produit sur le disque — c'est lui qui autorise FinalizePlanRun à
    /// n'attribuer un chemin qu'aux lignes dont le fichier existe.
    /// </summary>
    public void InsertPlanProfileBatch(long runId, IReadOnlyList<PreparedPlanProfile> rows)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var row in rows)
        {
            long id = NextPlanProfileId();
            var p = row.Profile;
            using (var c = Connection.CreateCommand())
            {
                c.Transaction = tx;
                // Liste de colonnes explicite, comme l'INSERT de BeginRun : un INSERT
                // positionnel se désaligne silencieusement le jour où une colonne est
                // ajoutée par ALTER TABLE ... ADD COLUMN, ce que fait déjà ce projet.
                c.CommandText = """
                  INSERT INTO plan_profiles
                    (plan_profile_id, run_id, captured_at, plan_hash, plan_hash_source, file_stem,
                     statement_count, query_hash, statement_type, statement_text, statement_text_length,
                     duration_us, cpu_time_us, estimated_rows, subtree_cost, dop,
                     serial_desired_memory_kb, granted_memory_kb, max_used_memory_kb,
                     write_outcome, sqlplan_path, digest_path, is_first, is_worst)
                  VALUES
                    ($id,$run,$ts,$ph,$phs,$fs,$sc,$qh,$st,$txt,$len,
                     $dur,$cpu,$est,$cost,$dop,$sd,$gr,$mu,$out,NULL,NULL,$first,FALSE)
                  """;
                Add(c, "$id", id); Add(c, "$run", runId); Add(c, "$ts", p.CapturedAt);
                Add(c, "$ph", p.PlanHash); Add(c, "$phs", p.PlanHashSource);
                Add(c, "$fs", p.FileStem); Add(c, "$sc", p.StatementCount);
                Add(c, "$qh", (object?)p.QueryHash); Add(c, "$st", (object?)p.StatementType);
                Add(c, "$txt", (object?)p.StatementText); Add(c, "$len", p.StatementTextLength);
                Add(c, "$dur", (object?)p.DurationUs); Add(c, "$cpu", (object?)p.CpuTimeUs);
                Add(c, "$est", (object?)p.EstimatedRows); Add(c, "$cost", (object?)p.SubtreeCost);
                Add(c, "$dop", (object?)p.Dop); Add(c, "$sd", (object?)p.SerialDesiredMemoryKb);
                Add(c, "$gr", (object?)p.GrantedMemoryKb); Add(c, "$mu", (object?)p.MaxUsedMemoryKb);
                Add(c, "$out", row.Outcome.ToString());
                Add(c, "$first", row.Outcome == PlanWriteOutcome.WroteFirst);
                c.ExecuteNonQuery();
            }
            foreach (var f in p.Findings)
            {
                using var fc = Connection.CreateCommand();
                fc.Transaction = tx;
                fc.CommandText = "INSERT INTO plan_findings VALUES ($id,$k,$n,$d)";
                Add(fc, "$id", id); Add(fc, "$k", f.Kind);
                Add(fc, "$n", (object?)f.NodeId); Add(fc, "$d", f.DetailJson);
                fc.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>
    /// Calcule is_worst et fiabilise sqlplan_path, à exécuter après la boucle d'ingestion.
    ///
    /// is_worst ne peut pas être posé à l'insertion : le pire cas n'est connu qu'à la fin,
    /// et une ligne marquée en cours de route devrait être démarquée dès qu'une exécution
    /// plus lente arrive.
    ///
    /// sqlplan_path ne peut pas non plus être écrit en flux : une ligne marquée pire cas
    /// pointerait vers file_stem.worst.sqlplan après qu'une exécution plus lente en a
    /// écrasé le contenu — le fichier appartiendrait alors à une autre exécution.
    ///
    /// Le CASE final s'appuie sur write_outcome, PAS sur is_first / is_worst. Pour un plan
    /// vu une seule fois, la même ligne est à la fois is_first et is_worst : un CASE testant
    /// is_worst d'abord lui donnerait file_stem.worst.sqlplan, un fichier que le writer n'a
    /// jamais produit puisqu'il n'écrit un .worst que sur une durée strictement supérieure.
    /// write_outcome est le seul témoin fiable de ce qui existe sur le disque.
    /// </summary>
    public void FinalizePlanRun(long runId)
    {
        using var tx = Connection.BeginTransaction();

        using (var c = Connection.CreateCommand())
        {
            c.Transaction = tx;
            // Le filtre IS NOT NULL rend le classement indépendant du réglage
            // default_null_order du moteur, et il applique la même politique que le
            // writer : une durée nulle ne remporte jamais le créneau worst.
            // Le départage plan_profile_id ASC reproduit le `>` strict du writer.
            c.CommandText = """
              UPDATE plan_profiles SET is_worst = FALSE WHERE run_id = $run;
              """;
            Add(c, "$run", runId);
            c.ExecuteNonQuery();
        }

        using (var c = Connection.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = """
              UPDATE plan_profiles SET is_worst = TRUE
              WHERE plan_profile_id IN (
                SELECT plan_profile_id FROM (
                  SELECT plan_profile_id,
                         ROW_NUMBER() OVER (PARTITION BY plan_hash
                                            ORDER BY duration_us DESC NULLS LAST,
                                                     plan_profile_id ASC) AS rn
                  FROM plan_profiles
                  WHERE run_id = $run
                    AND duration_us IS NOT NULL
                    AND write_outcome <> 'Failed')
                WHERE rn = 1)
              """;
            Add(c, "$run", runId);
            c.ExecuteNonQuery();
        }

        using (var c = Connection.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = """
              UPDATE plan_profiles SET
                sqlplan_path = CASE
                    WHEN write_outcome = 'WroteFirst'              THEN file_stem || '.sqlplan'
                    WHEN write_outcome = 'WroteWorst' AND is_worst THEN file_stem || '.worst.sqlplan'
                    ELSE NULL END,
                digest_path = CASE
                    WHEN is_worst OR is_first THEN file_stem || '.digest.json'
                    ELSE NULL END
              WHERE run_id = $run
              """;
            Add(c, "$run", runId);
            c.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Une ligne par plan distinct : agrégats du run, plus les lignes is_first et is_worst
    /// avec leurs horodatages exacts, plus les findings de la ligne retenue.
    /// L'agrégation vit ici, en SQL, jamais dans une réduction C#.
    /// </summary>
    public IReadOnlyList<PlanDigestRow> ReadPlanDigestRows(long runId)
    {
        var rows = new List<PlanDigestRow>();
        using (var c = Connection.CreateCommand())
        {
            c.CommandText = """
              WITH agg AS (
                SELECT plan_hash,
                       count(*)          AS exec_count,
                       min(duration_us)  AS dur_min, max(duration_us)  AS dur_max,
                       min(cpu_time_us)  AS cpu_min, max(cpu_time_us)  AS cpu_max,
                       min(captured_at)  AS cap_min, max(captured_at)  AS cap_max
                FROM plan_profiles WHERE run_id = $run GROUP BY plan_hash),
              firsts AS (SELECT * FROM plan_profiles WHERE run_id = $run AND is_first),
              worsts AS (SELECT * FROM plan_profiles WHERE run_id = $run AND is_worst)
              SELECT a.plan_hash,
                     coalesce(w.plan_hash_source, f.plan_hash_source),
                     coalesce(w.file_stem, f.file_stem),
                     coalesce(w.query_hash, f.query_hash),
                     coalesce(w.statement_type, f.statement_type),
                     coalesce(w.statement_text, f.statement_text),
                     coalesce(w.statement_text_length, f.statement_text_length),
                     coalesce(w.statement_count, f.statement_count),
                     a.exec_count, a.dur_min, a.dur_max, a.cpu_min, a.cpu_max, a.cap_min, a.cap_max,
                     coalesce(w.dop, f.dop),
                     coalesce(w.serial_desired_memory_kb, f.serial_desired_memory_kb),
                     coalesce(w.granted_memory_kb, f.granted_memory_kb),
                     coalesce(w.max_used_memory_kb, f.max_used_memory_kb),
                     f.sqlplan_path, f.captured_at, f.duration_us, f.cpu_time_us,
                     w.sqlplan_path, w.captured_at, w.duration_us, w.cpu_time_us,
                     coalesce(w.plan_profile_id, f.plan_profile_id) AS retained_id
              FROM agg a
              LEFT JOIN firsts f ON f.plan_hash = a.plan_hash
              LEFT JOIN worsts w ON w.plan_hash = a.plan_hash
              ORDER BY a.dur_max DESC NULLS LAST
              """;
            Add(c, "$run", runId);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                long retainedId = r.GetInt64(27);
                string planHash = r.GetString(0);
                string planHashSource = r.IsDBNull(1) ? "queryplanhash" : r.GetString(1);
                rows.Add(new PlanDigestRow
                {
                    PlanHash = planHash,
                    PlanHashSource = planHashSource,
                    // Reachable only when every row for this hash is Failed (no first/worst
                    // survives to carry file_stem): centralise the prefix rule in PlanIdentity
                    // rather than hardcoding "p_" here.
                    FileStem = r.IsDBNull(2) ? PlanIdentity.FileStem(planHash, planHashSource) : r.GetString(2),
                    QueryHash = r.IsDBNull(3) ? null : r.GetString(3),
                    StatementType = r.IsDBNull(4) ? null : r.GetString(4),
                    StatementText = r.IsDBNull(5) ? null : r.GetString(5),
                    StatementTextLength = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    StatementCount = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    ExecutionCount = r.GetInt64(8),
                    DurationMinUs = r.IsDBNull(9) ? null : r.GetInt64(9),
                    DurationMaxUs = r.IsDBNull(10) ? null : r.GetInt64(10),
                    CpuMinUs = r.IsDBNull(11) ? null : r.GetInt64(11),
                    CpuMaxUs = r.IsDBNull(12) ? null : r.GetInt64(12),
                    CapturedMinUtc = r.GetDateTime(13),
                    CapturedMaxUtc = r.GetDateTime(14),
                    Dop = r.IsDBNull(15) ? null : r.GetInt32(15),
                    SerialDesiredMemoryKb = r.IsDBNull(16) ? null : r.GetInt64(16),
                    GrantedMemoryKb = r.IsDBNull(17) ? null : r.GetInt64(17),
                    MaxUsedMemoryKb = r.IsDBNull(18) ? null : r.GetInt64(18),
                    First = r.IsDBNull(19) ? null : new PlanFileRef(r.GetString(19), r.GetDateTime(20),
                        r.IsDBNull(21) ? null : r.GetInt64(21), r.IsDBNull(22) ? null : r.GetInt64(22)),
                    Worst = r.IsDBNull(23) ? null : new PlanFileRef(r.GetString(23), r.GetDateTime(24),
                        r.IsDBNull(25) ? null : r.GetInt64(25), r.IsDBNull(26) ? null : r.GetInt64(26)),
                    Findings = ReadFindings(retainedId),
                });
            }
        }
        return rows;
    }

    // N+1 assumé : une requête par plan distinct, à la seule fin d'ingestion. Acceptable
    // aux volumes mesurés en tâche 0. Si la sonde a rapporté plus de ~2 000 plans distincts,
    // remplacer par une requête groupée unique chargée dans un Dictionary<long, List<...>>
    // avant la boucle de ReadPlanDigestRows.
    private IReadOnlyList<PlanFinding> ReadFindings(long planProfileId)
    {
        var list = new List<PlanFinding>();
        using var c = Connection.CreateCommand();
        c.CommandText = "SELECT kind, node_id, detail_json FROM plan_findings WHERE plan_profile_id = $id";
        Add(c, "$id", planProfileId);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new PlanFinding(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2)));
        return list;
    }

    /// <summary>
    /// Message à afficher en fin d'import quand le run contient à la fois des plans et des
    /// exécutions, mais qu'aucune exécution ne porte de query_hash — donc qu'aucune
    /// corrélation n'est possible. Renvoie null quand il n'y a rien à signaler.
    /// La garde « le run contient des exécutions » évite de reprocher l'absence d'une action
    /// sur des événements qui ne sont pas dans la trace.
    /// </summary>
    public string? PlanCorrelationWarning(long runId)
    {
        using var c = Connection.CreateCommand();
        c.CommandText = """
          SELECT (SELECT count(*) FROM plan_profiles WHERE run_id = $run),
                 (SELECT count(*) FROM executions    WHERE run_id = $run),
                 (SELECT count(*) FROM executions    WHERE run_id = $run AND query_hash IS NOT NULL)
          """;
        Add(c, "$run", runId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;

        long plans = r.GetInt64(0), execs = r.GetInt64(1), withHash = r.GetInt64(2);
        if (plans == 0 || execs == 0 || withHash > 0) return null;

        return $"""
            warning: {plans} plan profiles ingested, but no execution carries query_hash.
                     Plans cannot be correlated with queries.
                     Add ACTION(sqlserver.query_hash) to rpc_completed / sql_batch_completed.
                     See docs/capture-session.md
            """;
    }
}
