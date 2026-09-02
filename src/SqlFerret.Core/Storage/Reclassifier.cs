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
/// </summary>
public sealed record ReclassifyResult(
    long RowsExamined, long RowsChanged, long RowsUnchanged,
    long Unclassified, long RowsWithoutSample,
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
/// </summary>
public sealed class Reclassifier(DuckDbProject db)
{
    public ReclassifyResult Run(bool force = false)
    {
        var target = QueryNormalizer.Version;

        var pending = new List<(string Hash, string? Sample, string Kind, string? Table, string? Obj, int Ver)>();
        using (var read = db.Connection.CreateCommand())
        {
            // coalesce(any_value(...), any_value(...)) et non any_value(coalesce(...)) : les deux
            // jointures ne ramènent pas les mêmes lignes, et une signature de blocage n'a aucune
            // ligne côté executions. L'agrégation reste dans le SQL, elle ne remonte pas en C#.
            var select = $"""
              SELECT n.normalized_hash,
                     coalesce(any_value(e.sql_text_raw), any_value(b.inputbuf)),
                     n.statement_kind, n.primary_table, n.target_object, n.normalizer_version
              FROM normalized_queries n
              LEFT JOIN executions e ON e.normalized_hash = n.normalized_hash
              LEFT JOIN blocking_processes b ON b.inputbuf_fingerprint = n.normalized_hash
              {(force ? "" : $"WHERE n.normalizer_version < {target}")}
              GROUP BY n.normalized_hash, n.statement_kind, n.primary_table,
                       n.target_object, n.normalizer_version
              """;
            read.CommandText = select;
            using var rd = read.ExecuteReader();
            while (rd.Read())
                pending.Add((rd.GetString(0),
                             rd.IsDBNull(1) ? null : rd.GetString(1),
                             rd.IsDBNull(2) ? "OTHER" : rd.GetString(2),
                             rd.IsDBNull(3) ? null : rd.GetString(3),
                             rd.IsDBNull(4) ? null : rd.GetString(4),
                             rd.GetInt32(5)));
        }

        long changed = 0, unchanged = 0, unclassified = 0, noSample = 0;
        var from = pending.Count == 0 ? target : pending.Min(p => p.Ver);

        using var tx = db.Connection.BeginTransaction();
        foreach (var p in pending)
        {
            string kind; string? table; string? obj;
            if (p.Sample is null)
            {
                noSample++;
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

        return new ReclassifyResult(pending.Count, changed, unchanged, unclassified, noSample, from, target);

        static void Bind(System.Data.Common.DbCommand c, string name, object? value)
        {
            var p = c.CreateParameter();
            p.ParameterName = name.TrimStart('$');
            p.Value = value ?? DBNull.Value;
            c.Parameters.Add(p);
        }
    }
}
