// src/SqlFerret.Core/Analysis/HealthResults.cs
namespace SqlFerret.Core.Analysis;

/// <param name="MedianIntervalMin">
/// Cadence propre a cette serie. Deux sessions entrelacees produisent, prises ensemble, une
/// cadence apparente deux fois plus rapide que celle qu'aucune des deux n'a reellement.
/// </param>
public record HealthSeries(
    string SeriesKey, long Cycles, DateTime First, DateTime Last, double MedianIntervalMin);

/// <param name="NonDiagnosticsShare">
/// Part des evenements de la capture qui ne sont pas des diagnostics. Sur la capture qui a servi
/// a concevoir ceci : 98,8 %, dont 97,2 % pour une seule erreur de securite en boucle. C'est ce
/// chiffre qui dit au lecteur ce que vaut le reste du digest.
/// </param>
public record HealthCoverage(
    long Cycles, DateTime? First, DateTime? Last, double SpanMinutes,
    double MedianIntervalMin, double LargestGapMin, double NonDiagnosticsShare,
    IReadOnlyList<HealthSeries> Series);
