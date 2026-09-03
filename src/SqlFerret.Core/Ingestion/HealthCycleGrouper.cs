// src/SqlFerret.Core/Ingestion/HealthCycleGrouper.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

/// <param name="CycleAt">Le plus tot des horodatages du cycle.</param>
/// <param name="SeriesKey">Voir <see cref="HealthCycleGrouper"/>.</param>
public record HealthCycle(
    DateTime CycleAt, string SeriesKey, IReadOnlyList<ServerDiagnosticsSample> Samples);

/// <summary>
/// Regroupe les evenements de diagnostics en cycles.
/// <para>Les quatre evenements d'un cycle ne partagent pas leur horodatage : mesure sur une capture
/// reelle, 1344 evenements pour 1344 horodatages distincts, mais exactement 336 groupes a la
/// seconde pres, l'ecart intra-cycle allant de 0,22 a 0,67 ms. Prendre <c>captured_at</c> pour cle
/// de cycle aurait annonce 1344 cycles et un intervalle median de 0,2 ms au lieu de 336 et de
/// minutes. La troncature a la seconde est donc la cle.</para>
/// <para><c>SeriesKey</c> repond a un cas observe dans cette meme capture : un dossier peut contenir
/// deux sessions echantillonnant le meme serveur en parallele, decalees d'une trentaine de minutes.
/// Les metriques a portee d'intervalle — <c>intervalLongIos</c>, <c>tasksCompletedWithinInterval</c>
/// — sont rattachees a l'intervalle de leur propre session et ne s'additionnent pas a travers deux
/// series. Le regroupement se fait sur la seconde-dans-la-minute du cycle, stable pour une session
/// donnee et differente entre deux sessions demarrees a des instants differents.</para>
/// <para>C'est une heuristique, pas un fait : l'hote doit annoncer combien de series elle a trouvees
/// plutot que de la laisser agir en silence.</para>
/// </summary>
public static class HealthCycleGrouper
{
    public static IReadOnlyList<HealthCycle> Group(IReadOnlyList<ServerDiagnosticsSample> samples) =>
        samples
            .GroupBy(s => new DateTime(
                s.CapturedAt.Ticks - s.CapturedAt.Ticks % TimeSpan.TicksPerSecond, s.CapturedAt.Kind))
            .OrderBy(g => g.Key)
            .Select(g => new HealthCycle(
                g.Min(s => s.CapturedAt),
                g.Key.Second.ToString("D2"),
                [.. g.OrderBy(s => s.CapturedAt)]))
            .ToList();
}
