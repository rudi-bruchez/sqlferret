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
/// de 336 et de minutes. La troncature a la seconde est la cle, et elle est robuste.</para>
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
    public static IReadOnlyList<HealthCycle> Group(IReadOnlyList<ServerDiagnosticsSample> samples) =>
        samples
            .GroupBy(s => new DateTime(
                s.CapturedAt.Ticks - s.CapturedAt.Ticks % TimeSpan.TicksPerSecond, s.CapturedAt.Kind))
            .OrderBy(g => g.Key)
            .Select(g => new HealthCycle(
                g.Min(s => s.CapturedAt), "1", [.. g.OrderBy(s => s.CapturedAt)]))
            .ToList();
}
