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

/// <param name="SpanMinutes">
/// La duree que ce delta couvre reellement, qui n'est pas celle de la fenetre : seuls les N
/// premiers types d'attente apparaissent par cycle, donc un type qui entre ou sort de la liste a
/// un delta calcule sur une portion. Comparer un delta sur 40 minutes a un delta sur 14 heures
/// comme s'ils etaient la meme mesure serait la meme erreur d'un cran plus bas, d'ou son
/// transport jusqu'a l'affichage.
/// </param>
/// <param name="LifetimeAvgWaitUs">
/// Moyenne cumulee depuis le demarrage de l'instance, arrondie a la milliseconde par la source :
/// mesure, elle vaut 0 pour les attentes les plus frequentes. Transportee, jamais classee dessus.
/// </param>
/// <param name="LifetimeMaxWaitUs">
/// Maximum courant depuis le demarrage. Mesure : constant sur 147 cycles pour chaque type
/// d'attente, son record ayant ete etabli avant la fenetre. Ce n'est PAS la pire attente de la
/// capture et il ne doit jamais etre presente comme telle.
/// </param>
/// <param name="RestartIntervalsDropped">
/// Nombre d'intervalles ou le compteur a recule, ce qui ne peut signifier qu'un redemarrage
/// d'instance. Ecartes du delta et comptes : les ramener a zero en silence ferait passer un
/// redemarrage pour une periode calme.
/// </param>
public record WaitDelta(
    string WaitType, bool Preemptive, string Ranking, long WaitsDelta,
    double SpanMinutes, double PerMinute, long LifetimeAvgWaitUs, long LifetimeMaxWaitUs,
    long RestartIntervalsDropped);

/// <summary>Jauge instantanee : min / mediane / p95 / max, jamais une somme.</summary>
public record HealthScalar(string Name, double? Min, double? Median, double? P95, double? Max, long Samples);

public record StateCount(string Component, string State, long Cycles);

/// <param name="Change">
/// Dernier moins premier, signe. Le classement se fait sur la valeur absolue : une chute de
/// memoire disponible est au moins aussi interessante qu'une hausse, et ne garder que les hausses
/// cacherait exactement le cas qu'on cherche.
/// </param>
public record MemoryMovement(
    string ReportName, string? Unit, string Description, double First, double Last, double Change);

/// <param name="FilePath">
/// Chemin cote serveur, stocke verbatim et retire par aucune politique de redaction. L'hote qui
/// l'affiche doit dire que le digest divulgue la disposition des disques et les noms de fichiers.
/// </param>
public record PendingIoRow(DateTime CapturedAt, long? DurationUs, string? FilePath, string? Handle);

/// <summary>
/// Instantane pris pendant un cycle de diagnostics, jamais un rapport declenche par seuil : les
/// deux ne se comptent pas ensemble, et l'hote doit le dire quand il les affiche.
/// </summary>
public record DiagnosticsBlockingRow(
    DateTime CapturedAt, int? BlockedSpid, int? BlockingSpid, long? WaitTimeUs,
    string? WaitResourceType, string? BlockedInputBuf);
