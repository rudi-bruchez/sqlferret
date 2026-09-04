// src/SqlFerret.Core/Ingestion/HealthCycleGrouper.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

/// <param name="CycleAt">Le plus tot des horodatages du cycle.</param>
/// <param name="SeriesKey">Voir <see cref="HealthCycleGrouper"/>.</param>
public record HealthCycle(
    DateTime CycleAt, string SeriesKey, IReadOnlyList<ServerDiagnosticsSample> Samples);

/// <summary>
/// Regroupe les evenements de diagnostics en cycles.
///
/// <para><b>Le cycle.</b> Les quatre evenements d'un cycle ne partagent pas leur horodatage :
/// mesure sur une capture reelle, 1344 evenements pour 1344 horodatages distincts, mais exactement
/// 336 groupes a la seconde pres, l'ecart intra-cycle allant de 0,22 a 0,67 ms. Prendre
/// <c>captured_at</c> pour cle aurait annonce 1344 cycles et un intervalle median de 0,2 ms au lieu
/// de 336 et de minutes.</para>
///
/// <para>Le regroupement se fait par <b>proximite</b> et non par troncature a la seconde : quatre
/// evenements separes de moins d'une milliseconde peuvent enjamber une frontiere de seconde
/// (12:00:59,999 et 12:01:00,000), et une troncature les rangerait alors dans deux cycles de deux
/// echantillons chacun. Un nouveau cycle commence quand l'ecart avec l'echantillon precedent
/// depasse une seconde — deux ordres de grandeur au-dessus de l'ecart intra-cycle mesure, et deux
/// ordres de grandeur en dessous de la cadence la plus rapide que la source puisse produire.</para>
///
/// <para><b>La serie, et pourquoi il n'y en a qu'une.</b> Un dossier peut contenir deux sessions
/// echantillonnant le meme serveur en parallele — cas observe sur cette capture, deux series
/// decalees de 35 min 32 s pour une periode de 5 min, soit 30 secondes effectives une fois le
/// decalage pris modulo la periode. Attribuer chaque cycle a sa session demande de savoir quand
/// chaque session a commence, ce que la capture ne dit pas : deux tentatives d'heuristique s'y sont
/// cassees, l'une decoupant une session unique en soixante series des que sa cadence n'etait pas un
/// nombre rond de minutes, l'autre coupant en deux la session qui demarrait la premiere.</para>
///
/// <para>Tous les cycles portent donc la serie <c>"1"</c>, et l'irregularite de la cadence est
/// signalee au niveau du digest, ou elle est observable sans etre attribuee. Ce n'est pas une perte :
/// les compteurs de cette source sont cumulatifs <i>par instance</i> et les jauges sont des mesures
/// d'instant, donc un <c>dernier moins premier</c> a travers des echantillons entrelaces du meme
/// serveur reste juste. Deux valeurs seulement dependent de la session — <c>intervalLongIos</c> et
/// <c>tasksCompletedWithinInterval</c> — et le digest les tait quand la cadence est irreguliere,
/// plutot que de deviner a qui elles appartiennent.</para>
///
/// <para>Question ouverte assumee, deja notee dans la spec §15.</para>
/// </summary>
public static class HealthCycleGrouper
{
    /// <summary>
    /// Au-dela de cet ecart, l'echantillon appartient au cycle suivant. Public parce que
    /// l'ingestion pose la meme question en O(1) a l'arrivee de chaque echantillon, pour eviter
    /// d'appeler <see cref="Group"/> — qui trie tout le tampon — sur chaque evenement.
    /// </summary>
    public static readonly TimeSpan CycleGap = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<HealthCycle> Group(IReadOnlyList<ServerDiagnosticsSample> samples)
    {
        var ordered = samples.OrderBy(s => s.CapturedAt).ToList();
        var cycles = new List<HealthCycle>();
        var current = new List<ServerDiagnosticsSample>();

        foreach (var s in ordered)
        {
            if (current.Count > 0 && s.CapturedAt - current[^1].CapturedAt > CycleGap)
            {
                cycles.Add(new HealthCycle(current[0].CapturedAt, "1", [.. current]));
                current.Clear();
            }
            current.Add(s);
        }
        if (current.Count > 0) cycles.Add(new HealthCycle(current[0].CapturedAt, "1", [.. current]));
        return cycles;
    }
}
