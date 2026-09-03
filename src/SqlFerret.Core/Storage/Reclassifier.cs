// src/SqlFerret.Core/Storage/Reclassifier.cs
using SqlFerret.Core.Normalization;

namespace SqlFerret.Core.Storage;

/// <summary>
/// Bilan d'une reclassification. Invariant : <c>RowsExamined == RowsChanged + RowsUnchanged +
/// Unclassified + RowsWithoutSample</c>.
/// <para><c>Unclassified</c> compte les instructions que le classifieur n'a pas su typer — texte
/// non parsable, mais aussi construction valide sans visiteur dédié (<c>BEGIN TRANSACTION</c>,
/// par exemple). On ne prétend pas distinguer les deux : ScriptDom ne remonte pas l'information
/// jusqu'ici, et affirmer « échec de parsing » serait faux dans le second cas.</para>
/// <para><c>RowsWithoutSample</c> compte les signatures dont <b>aucun</b> texte source n'est
/// conservé, ni exécution ni <c>inputbuf</c> de blocage. Leur classification reste inchangée mais
/// leur version est tout de même portée : c'est ce qui fait converger le traitement, sans quoi
/// chaque passe les retrouverait et l'avertissement de version périmée ne s'éteindrait jamais.
/// L'hôte doit donc les annoncer pour ce qu'elles sont — un réimport, pas un <c>--force</c>.</para>
/// <para><c>RowsUnusableSample</c> compte celles dont le seul texte conservé est <b>déjà
/// normalisé</b> : <c>where c = ?</c> n'est pas du T-SQL, le parsing échoue et les reclasser les
/// dégraderait en <c>OTHER</c>. Compté à part de <c>Unclassified</c>, qui décrit une instruction
/// que le classifieur ne sait pas typer ; ici c'est l'échantillon qui est inexploitable, pas
/// l'instruction. Même traitement que <c>RowsWithoutSample</c> : classification intacte, version
/// portée, réimport comme seul recours.</para>
/// </summary>
public sealed record ReclassifyResult(
    long RowsExamined, long RowsChanged, long RowsUnchanged,
    long Unclassified, long RowsWithoutSample, long RowsUnusableSample,
    int FromVersion, int ToVersion);

/// <summary>
/// Rejoue le classifieur sur un projet existant, à partir du texte source déjà persisté, sans
/// réimporter la trace. On ne reclassifie jamais depuis <c>normalized_sql</c> : la normalisation
/// remplace les littéraux par <c>?</c>, et un <c>DEFAULT ?</c> n'est pas du T-SQL valide — le
/// parsing échouerait précisément sur les instructions qui nous intéressent.
/// <para>Le texte source est cherché dans <c>executions.sql_text_raw</c> puis, à défaut, dans
/// <c>blocking_processes.inputbuf</c>. Les signatures issues d'un rapport de blocage n'ont pas
/// d'exécution : sans ce second recours, elles étaient comptées « sans échantillon » et n'étaient
/// jamais reclassées, alors que leur texte est là et parfaitement analysable.</para>
/// <para>Encore faut-il que le texte conservé soit du T-SQL et non de la sortie du normaliseur.
/// Deux provenances ne le garantissent pas, et rien dans le texte lui-même ne permet de trancher —
/// seule la provenance le dit :
/// <list type="bullet">
/// <item><c>executions.sql_text_raw</c> d'un run importé en <c>--sanitize-sql-text literals</c>
/// (<c>ingestion_runs.sql_text_policy</c>) ;</item>
/// <item><c>blocking_processes.inputbuf</c>, qu'<c>IngestionService.PrepareProc</c> stocke normalisé
/// pour toute politique de rédaction autre que <c>off</c> (<c>ingestion_runs.redaction_policy</c>).
/// </item>
/// </list>
/// Un échantillon exploitable est donc préféré à un échantillon normalisé pour une même signature ;
/// à défaut, la signature est comptée dans <c>RowsUnusableSample</c> et laissée intacte. Un run
/// dont on ne retrouve pas la ligne <c>ingestion_runs</c> est réputé exploitable : c'est le
/// comportement historique, et supposer l'inverse dégraderait des projets sains.</para>
/// </summary>
public sealed class Reclassifier(DuckDbProject db)
{
    public ReclassifyResult Run(bool force = false)
    {
        var target = QueryNormalizer.Version;

        var pending = new List<(string Hash, string? Sample, bool AnyText, string Kind, string? Table, string? Obj, int Ver)>();
        using (var read = db.Connection.CreateCommand())
        {
            // coalesce(any_value(...), any_value(...)) et non any_value(coalesce(...)) : les deux
            // jointures ne ramènent pas les mêmes lignes, et une signature de blocage n'a aucune
            // ligne côté executions. L'agrégation reste dans le SQL, elle ne remonte pas en C#.
            //
            // Les FILTER écartent les échantillons déjà normalisés : le premier coalesce ne ramène
            // donc que du T-SQL analysable, et le second dit si un texte — fût-il inexploitable —
            // existait. Les trois jointures ajoutées portent sur des clés primaires : elles
            // élargissent la ligne, elles ne la multiplient pas.
            //
            // Une ligne ingestion_runs absente (projet de test, provenance perdue) vaut
            // « exploitable » : c'est le comportement historique.
            const string ExecutionIsRaw = "(er.run_id IS NULL OR coalesce(er.sql_text_policy, 'raw') = 'raw')";
            // Miroir exact d'IngestionService.PrepareProc : l'input buffer n'est verbatim que si
            // les deux politiques l'autorisent. Les deux conditions doivent bouger ensemble.
            const string InputBufIsRaw =
                "(brp.report_id IS NULL OR rr.run_id IS NULL OR " +
                "(rr.redaction_policy = 'off' AND coalesce(rr.sql_text_policy, 'raw') = 'raw'))";
            var select = $"""
              SELECT n.normalized_hash,
                     coalesce(any_value(e.sql_text_raw) FILTER (WHERE {ExecutionIsRaw}),
                              any_value(b.inputbuf)     FILTER (WHERE {InputBufIsRaw})),
                     coalesce(any_value(e.sql_text_raw), any_value(b.inputbuf)) IS NOT NULL,
                     n.statement_kind, n.primary_table, n.target_object, n.normalizer_version
              FROM normalized_queries n
              LEFT JOIN executions e ON e.normalized_hash = n.normalized_hash
              LEFT JOIN ingestion_runs er ON er.run_id = e.run_id
              LEFT JOIN blocking_processes b ON b.inputbuf_fingerprint = n.normalized_hash
              LEFT JOIN blocking_reports brp ON brp.report_id = b.report_id
              LEFT JOIN ingestion_runs rr ON rr.run_id = brp.run_id
              {(force ? "" : $"WHERE n.normalizer_version < {target}")}
              GROUP BY n.normalized_hash, n.statement_kind, n.primary_table,
                       n.target_object, n.normalizer_version
              """;
            read.CommandText = select;
            using var rd = read.ExecuteReader();
            while (rd.Read())
                pending.Add((rd.GetString(0),
                             rd.IsDBNull(1) ? null : rd.GetString(1),
                             !rd.IsDBNull(2) && rd.GetBoolean(2),
                             rd.IsDBNull(3) ? "OTHER" : rd.GetString(3),
                             rd.IsDBNull(4) ? null : rd.GetString(4),
                             rd.IsDBNull(5) ? null : rd.GetString(5),
                             rd.GetInt32(6)));
        }

        long changed = 0, unchanged = 0, unclassified = 0, noSample = 0, unusable = 0;
        var from = pending.Count == 0 ? target : pending.Min(p => p.Ver);

        using var tx = db.Connection.BeginTransaction();
        foreach (var p in pending)
        {
            string kind; string? table; string? obj;
            if (p.Sample is null)
            {
                // Aucun texte du tout, ou seulement du texte déjà normalisé : dans les deux cas
                // la classification est laissée telle quelle et seule la version est portée. Les
                // deux compteurs restent distincts parce que le remède diffère à peine — réimport
                // dans les deux cas — mais le diagnostic à afficher, lui, n'est pas le même.
                if (p.AnyText) unusable++; else noSample++;
                kind = p.Kind; table = p.Table; obj = p.Obj;   // inchangé, mais version portée
            }
            else
            {
                var c = AstClassifier.Classify(p.Sample);
                kind = c.Kind; table = c.PrimaryTable; obj = c.TargetObject;
                // Set() écrit les trois champs en bloc et aucun visiteur ne produit "OTHER" : le
                // kind suffit, et le triple test laissait croire à un invariant plus faible.
                if (kind == "OTHER") unclassified++;
                else if (kind == p.Kind && table == p.Table && obj == p.Obj) unchanged++;
                else changed++;
            }

            using var upd = db.Connection.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
              UPDATE normalized_queries
                 SET statement_kind = $kind, primary_table = $tbl,
                     target_object = $obj, normalizer_version = $ver
               WHERE normalized_hash = $h
              """;
            Bind(upd, "$kind", kind); Bind(upd, "$tbl", table); Bind(upd, "$obj", obj);
            Bind(upd, "$ver", target); Bind(upd, "$h", p.Hash);
            upd.ExecuteNonQuery();
        }
        tx.Commit();

        return new ReclassifyResult(
            pending.Count, changed, unchanged, unclassified, noSample, unusable, from, target);

        static void Bind(System.Data.Common.DbCommand c, string name, object? value)
        {
            var p = c.CreateParameter();
            p.ParameterName = name.TrimStart('$');
            p.Value = value ?? DBNull.Value;
            c.Parameters.Add(p);
        }
    }
}
