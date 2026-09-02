// src/SqlFerret.Core/Analysis/AdHocQuery.cs
using System.Collections;
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>
/// Résultat d'une requête ad hoc. Les valeurs sont brutes : la conversion en texte et le
/// formatage des durées appartiennent à l'hôte, qui seul connaît les préférences d'affichage.
/// <para>
/// « Brutes » ne veut pas dire « quelconques » : le jeu de types CLR que peut contenir
/// <see cref="Rows"/> est <b>fermé et connu</b>. C'est celui que rend la bibliothèque DuckDB,
/// normalisé par <see cref="AdHocQuery"/> ; un hôte qui les affiche doit tous les traiter, et
/// c'est ici qu'est la liste, pas chez lui :
/// </para>
/// <list type="table">
/// <item><term>entiers exacts</term><description><c>sbyte</c> (TINYINT), <c>short</c>, <c>int</c>,
///   <c>long</c>, <c>byte</c> (UTINYINT), <c>ushort</c>, <c>uint</c>, <c>ulong</c>, et
///   <c>System.Numerics.BigInteger</c> pour HUGEINT / UHUGEINT / VARINT — donc pour
///   <c>sum()</c> d'un BIGINT, l'agrégat le plus banal de l'outil</description></item>
/// <item><term>fractionnaires</term><description><c>float</c>, <c>double</c> (dont
///   <c>avg()</c>), <c>decimal</c> (DECIMAL)</description></item>
/// <item><term>texte, booléen</term><description><c>string</c> (VARCHAR, ENUM, BIT),
///   <c>bool</c></description></item>
/// <item><term>temps</term><description><c>DateTime</c> (TIMESTAMP, TIMESTAMPTZ, TIMESTAMP_NS),
///   <c>DateOnly</c> (DATE), <c>TimeOnly</c> (TIME), <c>TimeSpan</c> (INTERVAL)</description></item>
/// <item><term>binaire, identité</term><description><c>byte[]</c> (BLOB, voir
///   <see cref="AdHocQuery.Detach"/>), <c>Guid</c> (UUID)</description></item>
/// <item><term>composites</term><description><c>List&lt;object?&gt;</c> (LIST, ARRAY) et
///   <c>Dictionary&lt;string, object?&gt;</c> (STRUCT, MAP), normalisés par
///   <see cref="AdHocQuery.Detach"/> et dont les éléments relèvent récursivement de cette même
///   liste. La bibliothèque les rend typés (<c>List&lt;Stream&gt;</c>,
///   <c>Dictionary&lt;int, Stream&gt;</c>…) ; on les réécrit pour pouvoir y détacher les BLOB
///   imbriqués, qu'un conteneur typé refuserait. Une clé de MAP devient son texte — c'est déjà
///   la seule forme que sachent rendre les hôtes.</description></item>
/// </list>
/// </summary>
public sealed record ResultTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);

/// <summary>
/// Exécute du SQL fourni par l'utilisateur sur le projet. La non-écriture n'est pas obtenue en
/// analysant la requête — ce serait une passoire — mais en ouvrant la connexion en lecture seule
/// via <see cref="Storage.DuckDbProject.OpenReadOnly"/>.
/// </summary>
public sealed class AdHocQuery(DuckDBConnection conn)
{
    /// <summary>
    /// Limite conseillée aux hôtes quand l'utilisateur n'en fixe pas. Le jeu de résultats est
    /// entièrement matérialisé en mémoire managée : sans limite, un <c>SELECT * FROM executions</c>
    /// — requête parfaitement normale — épuise le tas sur une trace réelle.
    /// </summary>
    public const int DefaultLimit = 1000;

    /// <param name="sql">Le texte exécuté tel quel.</param>
    /// <param name="limit">
    /// Nombre maximal de lignes rendues ; <c>null</c> signifie <b>aucune limite</b>. Les hôtes
    /// passent normalement <see cref="DefaultLimit"/> et réservent <c>null</c> à une demande
    /// explicite de l'utilisateur.
    /// </param>
    public ResultTable Run(string sql, int? limit = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++) columns[i] = reader.GetName(i);

        List<IReadOnlyList<object?>> rows = [];
        var truncated = false;
        while (reader.Read())
        {
            if (limit is { } max && rows.Count >= max) { truncated = true; break; }
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : Detach(reader.GetValue(i));
            rows.Add(row);
        }

        return new ResultTable(columns, rows, truncated);
    }

    /// <summary>
    /// Détache la valeur du lecteur. La seule qui ne survive pas à sa fermeture est le BLOB, rendu
    /// en <see cref="Stream"/> sur la mémoire non managée du chunk courant ; le lire après coup —
    /// ce que fait forcément l'hôte, <see cref="ResultTable"/> lui étant remis lecteur fermé —
    /// serait une lecture après libération. Tout le reste (nombres, chaînes, dates) est déjà
    /// managé et copié.
    /// <para><b>La descente dans les composites est indispensable, pas un raffinement</b> : un
    /// BLOB placé dans un LIST ou un STRUCT est tout aussi volatil, et l'oublier le faisait sortir
    /// en <c>"System.IO.UnmanagedMemoryStream"</c> — une valeur fausse, exit 0, sans un mot. Les
    /// conteneurs sont donc recopiés dans des collections à éléments <c>object?</c> : ceux que
    /// rend la bibliothèque sont typés (<c>List&lt;Stream&gt;</c>) et refuseraient d'accueillir le
    /// <c>byte[]</c> qui remplace le flux.</para>
    /// </summary>
    private static object Detach(object v)
    {
        switch (v)
        {
            case Stream s:
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }

            // L'ordre compte : une chaîne est un IEnumerable, un byte[] aussi, et un IDictionary
            // encore. Sans ces deux gardes, la branche composite les émietterait en listes.
            case string or byte[]:
                return v;

            case IDictionary map:
                {
                    var copy = new Dictionary<string, object?>(map.Count);
                    foreach (DictionaryEntry e in map)
                        copy[e.Key?.ToString() ?? ""] = e.Value is null ? null : Detach(e.Value);
                    return copy;
                }

            case IEnumerable list:
                {
                    List<object?> copy = [];
                    foreach (var e in list) copy.Add(e is null ? null : Detach(e));
                    return copy;
                }

            default:
                return v;
        }
    }
}
