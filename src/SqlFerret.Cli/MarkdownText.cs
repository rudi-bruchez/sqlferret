// src/SqlFerret.Cli/MarkdownText.cs
using System.Text;

namespace SqlFerret.Cli;

/// <summary>
/// Echappement du texte venu d'une capture avant qu'il n'entre dans un document Markdown.
///
/// <para>Une capture est une donnee d'une AUTRE machine, et les digests sont faits pour etre
/// partages — colles dans un ticket, ouverts dans un portail, envoyes a un client. Interpole tel
/// quel, un texte de capture sort de sa cellule : un backtick ferme le span de code, une barre
/// ouvre une colonne, un saut de ligne termine le tableau ou la liste, et la suite devient du
/// Markdown a part entiere. Titres fabriques, lignes de metriques forgees, HTML brut.</para>
///
/// <para>Le cas ordinaire suffit a le justifier : un SQL sur plusieurs lignes disloque une liste
/// sans qu'aucun attaquant s'en mele. Le cas hostile n'est que le meme defaut pousse plus loin —
/// et il est atteignable, car quiconque cree une base choisit le nom de ses fichiers, et un groupe
/// de disponibilite porte le nom qu'on lui a donne.</para>
///
/// <para>Ce helper est partage entre les deux digests deliberement. Il a d'abord ete ecrit en
/// double, et la copie du digest de blocage n'a jamais recu les trois corrections apportees a
/// l'autre : l'ordre des remplacements, le bloc de notes, et l'unite des entrees memoire. Deux
/// copies derivent, c'est mesure, pas suppose.</para>
/// </summary>
public static class MarkdownText
{
    /// <summary>
    /// Rend une valeur de capture inerte sans la rendre illisible.
    ///
    /// <para>L'antislash passe EN PREMIER. L'echapper apres la barre produirait <c>\\|</c> pour
    /// une entree <c>\|</c>, et en GFM une barre precedee d'un nombre PAIR d'antislashs redevient
    /// un separateur de cellule — l'echappement se retournerait contre lui-meme.</para>
    ///
    /// <para>Le backtick est remplace plutot qu'echappe : Markdown n'offre aucun echappement du
    /// backtick a l'interieur d'un span de code. C'est coherent avec la regle du projet —
    /// <c>md</c> et <c>table</c> sont des formats de PRESENTATION, <c>csv</c> et <c>json</c> les
    /// formats fideles. La valeur exacte reste en base et dans le JSON.</para>
    /// </summary>
    public static string Safe(string? v) => string.IsNullOrEmpty(v) ? "" : new StringBuilder(v)
        .Replace((char)13, ' ').Replace((char)10, ' ').Replace((char)9, ' ')
        .Replace("\\", @"\\").Replace("`", "'").Replace("|", @"\|").ToString();
}
