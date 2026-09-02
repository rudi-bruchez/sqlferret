// src/SqlFerret.Cli/ResultFormatter.cs
using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;

namespace SqlFerret.Cli;

/// <summary>
/// Rend un <see cref="ResultTable"/> pour la console. Le formatage des durées suit le **nom de la
/// colonne de sortie** : une colonne dont le nom se termine par <c>_us</c> est rendue en ms ou en
/// s. Un alias qui perd le suffixe perd donc le formatage — c'est assumé, le type SQL ne
/// distinguant pas une durée d'un entier quelconque.
/// <para>
/// Les quatre formats se répartissent en deux familles, et la différence est assumée :
/// <c>csv</c> et <c>json</c> sont <b>fidèles</b> (ils conservent les sauts de ligne et les types),
/// <c>table</c> et <c>md</c> sont des formats de <b>présentation</b>, à un enregistrement par
/// ligne, où les sauts de ligne sont rendus par des échappements lisibles.
/// </para>
/// <para>
/// Les deux chemins de rendu — <see cref="Text"/> et <see cref="WriteJson"/> — traitent le jeu de
/// types documenté sur <see cref="ResultTable"/> sans jamais déléguer à la réflexion :
/// <c>System.Text.Json</c> n'a pas de convertisseur pour <see cref="BigInteger"/> et sérialisait
/// ses <i>propriétés</i> (<c>IsPowerOfTwo</c>, <c>IsZero</c>…), ce qui perdait silencieusement
/// toute valeur HUGEINT, c'est-à-dire tout <c>sum()</c> d'un BIGINT.
/// </para>
/// <para>
/// Les deux n'ont pas la même forme, et c'est voulu. <see cref="WriteJson"/> <b>énumère</b> :
/// JSON distingue nombre, chaîne, booléen, tableau et objet, donc chaque type doit être aiguillé.
/// <see cref="Text"/> ne nomme que les types dont le <c>ToString</c> invariant serait faux,
/// ambigu ou inutilisable (dates, <c>byte[]</c>, composites) et laisse les autres — nombres,
/// <c>bool</c>, <c>Guid</c> — à <c>IFormattable</c>, qui les rend correctement. Le repli final de
/// chacun rend le <i>texte</i> de la valeur : dégrader lisiblement plutôt qu'échouer, si une
/// version ultérieure de la bibliothèque introduisait un type inconnu.
/// </para>
/// </summary>
public static class ResultFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="truncationCause">
    /// Ce qui a tronqué la sortie, tel que l'hôte le formule pour <c>stderr</c> — la même phrase
    /// des deux côtés, sinon le pied de page finit par mentir. Vide ou <c>null</c>, le pied de page
    /// se contente de signaler la troncature sans en nommer la cause.
    /// </param>
    public static string Render(ResultTable t, string format, string durationUnit, bool raw,
                                string? truncationCause = null)
    {
        // JSON est un format machine : on n'y injecte jamais de présentation. Les valeurs y sont
        // typées (un nombre reste un nombre), sans quoi tout traitement aval devrait reparser.
        if (format == "json") return Json(t);

        var cells = Project(t, durationUnit, raw);
        var body = format switch
        {
            "csv" => Csv(t.Columns, cells),
            "md" => Markdown(t.Columns, cells),
            _ => Aligned(t.Columns, cells),
        };
        if (!t.Truncated || format == "csv") return body;
        return body + Environment.NewLine + (string.IsNullOrEmpty(truncationCause)
            ? "(sortie tronquée)"
            : $"(sortie tronquée : {truncationCause})");
    }

    private static string?[][] Project(ResultTable t, string unit, bool raw)
    {
        var isDuration = new bool[t.Columns.Count];
        for (var i = 0; i < t.Columns.Count; i++)
            isDuration[i] = !raw && t.Columns[i].EndsWith("_us", StringComparison.OrdinalIgnoreCase);

        var outRows = new string?[t.Rows.Count][];
        for (var r = 0; r < t.Rows.Count; r++)
        {
            outRows[r] = new string?[t.Columns.Count];
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var v = t.Rows[r][c];
                outRows[r][c] =
                    v is null ? null
                    : isDuration[c] && AsMicroseconds(v) is { } us ? DisplayFormat.Duration(us, unit)
                    : Text(v);
            }
        }
        return outRows;
    }

    /// <summary>
    /// Les microsecondes portées par une valeur numérique, quelle que soit sa largeur, ou
    /// <c>null</c> si la valeur n'est pas un nombre.
    /// <para>
    /// C'est le point qui manquait : la liste blanche précédente ne connaissait que les entiers
    /// natifs, alors que DuckDB rend <c>sum(BIGINT)</c> en HUGEINT (<see cref="BigInteger"/>),
    /// <c>avg()</c> en <c>double</c> et une colonne DECIMAL en <c>decimal</c>. Deux colonnes
    /// <c>_us</c> de la même ligne sortaient donc dans deux unités différentes, sans marqueur, et
    /// <c>22288368299</c> se lisait comme des millisecondes.
    /// </para>
    /// <para>
    /// L'arrondi à la microseconde entière est celui qu'impose déjà
    /// <see cref="DisplayFormat.Duration(long, string)"/>, et <c>--raw</c> donne la valeur exacte.
    /// Hors de la plage de <c>long</c> — 292 000 ans de microsecondes — on rend le nombre brut
    /// plutôt qu'un résultat faux.
    /// </para>
    /// </summary>
    private static long? AsMicroseconds(object v) => v switch
    {
        sbyte or short or int or long or byte or ushort or uint => Convert.ToInt64(v, Inv),
        ulong u => u <= long.MaxValue ? (long)u : null,
        BigInteger b => b >= long.MinValue && b <= long.MaxValue ? (long)b : null,
        decimal m => m >= long.MinValue && m <= long.MaxValue ? decimal.ToInt64(Math.Round(m)) : null,
        float or double => Convert.ToDouble(v, Inv) is var d && double.IsFinite(d)
                           && d >= long.MinValue && d <= long.MaxValue
                               ? (long)Math.Round(d)
                               : null,
        _ => null,
    };

    /// <summary>
    /// Rendu textuel d'une valeur, pour <c>table</c>, <c>csv</c> et <c>md</c>. Chaque type
    /// documenté sur <see cref="ResultTable"/> a sa branche ; le repli final est délibéré et rend
    /// le texte de la valeur, jamais ses propriétés.
    /// </summary>
    private static string Text(object v) => v switch
    {
        string s => s,
        // Format invariant explicite : le ToString invariant d'un DateTime donne
        // "08/13/2026 05:31:22", ambigu et intriable. Les traces sont pleines
        // d'horodatages ; ils doivent rester ISO et triables en texte.
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv),
        // DATE et TIME ne mappent pas sur DateTime : faute de branche à eux, ils tombaient dans
        // IFormattable, qui rend une date ambiguë ("01/02/2026") et perd les secondes ("10:11").
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        TimeOnly t => t.ToString("HH:mm:ss.ffffff", Inv),
        TimeSpan s => s.ToString("c", Inv),
        byte[] b => "0x" + Convert.ToHexString(b),
        // LIST / STRUCT / MAP : leur ToString donne le nom du type générique. On les rend dans
        // leur forme JSON compacte, seule écriture textuelle qui conserve la valeur.
        IDictionary or IEnumerable => JsonScalar(v),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    /// <summary>
    /// Rend une valeur sur une seule ligne. <c>table</c> et <c>md</c> alignent un enregistrement
    /// par ligne : un saut de ligne dans une valeur — <c>executions.sql_text_raw</c> en contient
    /// systématiquement — casse la grille de <c>table</c> et rompt le document <c>md</c>. On le
    /// rend donc par l'échappement littéral correspondant. <c>csv</c> n'en a pas besoin, il cite
    /// la valeur (RFC 4180) ; <c>json</c> échappe lui-même.
    /// <para>L'antislash n'est pas doublé : <c>DOMAIN\host</c> et les chemins Windows sont partout
    /// dans ces traces, et les rendre <c>DOMAIN\\host</c> coûterait plus de lisibilité que n'en
    /// rapporterait une réversibilité que ces deux formats de présentation ne promettent pas.</para>
    /// </summary>
    private static string OneLine(string s) =>
        s.AsSpan().IndexOfAny('\n', '\r') < 0 && !s.Contains('\t')
            ? s
            : s.Replace("\r", @"\r").Replace("\n", @"\n").Replace("\t", @"\t");

    private static string Aligned(IReadOnlyList<string> cols, string?[][] rows)
    {
        var head = cols.Select(OneLine).ToArray();
        var cells = rows.Select(r => r.Select(v => OneLine(v ?? "")).ToArray()).ToArray();

        var width = new int[cols.Count];
        for (var i = 0; i < cols.Count; i++) width[i] = head[i].Length;
        foreach (var row in cells)
            for (var i = 0; i < cols.Count; i++)
                width[i] = Math.Max(width[i], row[i].Length);

        var sb = new StringBuilder();
        for (var i = 0; i < cols.Count; i++) sb.Append(head[i].PadRight(width[i])).Append("  ");
        sb.AppendLine();
        for (var i = 0; i < cols.Count; i++) sb.Append(new string('-', width[i])).Append("  ");
        sb.AppendLine();
        foreach (var row in cells)
        {
            for (var i = 0; i < cols.Count; i++) sb.Append(row[i].PadRight(width[i])).Append("  ");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string Markdown(IReadOnlyList<string> cols, string?[][] rows)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", cols.Select(Esc))).AppendLine(" |");
        sb.Append('|').Append(string.Join("|", cols.Select(_ => "---"))).AppendLine("|");
        foreach (var row in rows)
            sb.Append("| ").Append(string.Join(" | ", row.Select(v => Esc(v ?? "")))).AppendLine(" |");
        return sb.ToString().TrimEnd();

        static string Esc(string s) => OneLine(s).Replace("|", @"\|");
    }

    private static string Csv(IReadOnlyList<string> cols, string?[][] rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(Q)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(v => Q(v ?? ""))));
        return sb.ToString().TrimEnd();

        static string Q(string s) =>
            s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
    }

    /// <summary>
    /// Sérialise les valeurs <b>brutes</b> : un entier reste un entier, un horodatage sort en ISO
    /// 8601. Passer par les chaînes de <see cref="Project"/> produirait <c>"1500"</c> au lieu de
    /// <c>1500</c>, et interdirait toute arithmétique en aval.
    /// <para>L'écriture passe par <see cref="Utf8JsonWriter"/> et non par <c>JsonSerializer</c> :
    /// un sérialiseur généraliste traite tout type qu'il ne connaît pas par réflexion, ce qui
    /// rendait <c>sum(duration_us)</c> — un HUGEINT, donc un <see cref="BigInteger"/> — sous la
    /// forme <c>{ "IsPowerOfTwo": false, "IsZero": false, ... }</c>, sans le moindre avertissement
    /// et dans le format explicitement déclaré « machine ».</para>
    /// </summary>
    private static string Json(ResultTable t)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach (var row in t.Rows)
            {
                // Le dictionnaire conserve le comportement documenté des colonnes homonymes : la
                // dernière écrase la première, et DuplicateColumnNames permet de le signaler.
                var d = new Dictionary<string, object?>(t.Columns.Count);
                for (var i = 0; i < t.Columns.Count; i++) d[t.Columns[i]] = row[i];
                w.WriteStartObject();
                foreach (var (name, value) in d) { w.WritePropertyName(name); WriteJson(w, value); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Forme JSON compacte d'une valeur isolée, pour les composites en mode texte.</summary>
    private static string JsonScalar(object v)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) WriteJson(w, v);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteJson(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case string s: w.WriteStringValue(s); break;

            case sbyte or short or int or byte or ushort: w.WriteNumberValue(Convert.ToInt32(v, Inv)); break;
            case uint or long: w.WriteNumberValue(Convert.ToInt64(v, Inv)); break;
            case ulong u: w.WriteNumberValue(u); break;
            case decimal m: w.WriteNumberValue(m); break;
            case float or double:
                var d = Convert.ToDouble(v, Inv);
                // JSON n'a ni NaN ni Infinity ; les écrire en nombre lèverait et emporterait toute
                // la sortie. On les rend en chaîne, seule façon de ne rien effacer.
                if (double.IsFinite(d)) w.WriteNumberValue(d); else w.WriteStringValue(d.ToString(Inv));
                break;
            // HUGEINT / UHUGEINT / VARINT. Le littéral numérique est écrit tel quel : c'est le seul
            // rendu qui conserve la valeur, faute de convertisseur pour ce type.
            case BigInteger bi: w.WriteRawValue(bi.ToString(Inv), skipInputValidation: true); break;

            case DateTime dt: w.WriteStringValue(dt); break;      // ISO 8601 natif
            case Guid g: w.WriteStringValue(g); break;
            case DateOnly or TimeOnly or TimeSpan: w.WriteStringValue(Text(v)); break;
            case byte[] bytes: w.WriteStringValue("0x" + Convert.ToHexString(bytes)); break;

            case IDictionary map:
                w.WriteStartObject();
                foreach (DictionaryEntry e in map)
                {
                    w.WritePropertyName(e.Key?.ToString() ?? "");
                    WriteJson(w, e.Value);
                }
                w.WriteEndObject();
                break;
            case IEnumerable list:
                w.WriteStartArray();
                foreach (var e in list) WriteJson(w, e);
                w.WriteEndArray();
                break;

            // Repli délibéré : un type que la bibliothèque DuckDB aurait ajouté depuis. Une chaîne
            // est honnête, elle dit « voici le texte de la valeur » ; la réflexion, elle, produit
            // un objet qui a l'air d'une donnée et n'en est pas.
            default: w.WriteStringValue(v.ToString()); break;
        }
    }

    /// <summary>
    /// Noms de colonnes présents plus d'une fois dans <paramref name="t"/> — par exemple un
    /// <c>JOIN</c> sans alias sur une colonne partagée (<c>id</c> des deux côtés). Sans effet pour
    /// les formats indexés (table/csv/md), qui adressent chaque cellule par position ; seul
    /// <c>json</c> indexe par nom, donc <see cref="Json"/> écraserait silencieusement la première
    /// valeur par la seconde. L'appelant décide s'il faut avertir : cette méthode se contente de
    /// détecter, elle n'écrit rien.
    /// </summary>
    public static IReadOnlyList<string> DuplicateColumnNames(ResultTable t) =>
        [.. t.Columns.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)];
}
