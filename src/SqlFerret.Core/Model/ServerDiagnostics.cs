// src/SqlFerret.Core/Model/ServerDiagnostics.cs
namespace SqlFerret.Core.Model;

/// <summary>
/// Trois issues et non deux. Un composant inconnu n'est pas un echec : <c>events</c> est un
/// cinquieme composant documente, et une instance Always On emet une ligne par groupe de
/// disponibilite, nommee d'apres le groupe. Les compter en echec ferait qu'un compteur d'erreur se
/// declencherait a chaque cycle sur une instance parfaitement saine, et un compteur qui se
/// declenche toujours n'apprend rien.
/// </summary>
public enum DiagnosticsOutcome
{
    /// <summary>Un des quatre composants connus, XML bien forme.</summary>
    Parsed,
    /// <summary>Un autre composant. La ligne est conservee, sans enfants.</summary>
    Unhandled,
    /// <summary>Un composant connu dont le XML est absent, malforme, ou de racine inattendue.</summary>
    Failed,
}

/// <param name="ValueBig">
/// Les compteurs de SQL Server sont des entiers, et <c>DOUBLE</c> perd les entiers au-dela de
/// 2^53. Un entier atterrit ici, un reel dans <c>ValueNum</c>, le reste dans <c>ValueText</c>.
/// </param>
public record HealthMetric(string Name, double? ValueNum, long? ValueBig, string? ValueText);

public record HealthWait(
    bool Preemptive, string Ranking, string WaitType, long Waits, long AvgWaitUs, long MaxWaitUs);

public record HealthCpuRequest(
    int? SessionId, int? RequestId, string? Command, long? CpuTimeUs,
    double? CpuUtilization, string? TaskAddress);

public record HealthPendingTask(string EntryPoint, long TaskCount);

public record HealthPendingIo(long? DurationUs, string? FilePath, string? Handle, long? OffsetBytes);

public record HealthMemoryEntry(
    string ReportName, string? Unit, string Description, double? ValueNum, string? ValueText);

/// <param name="EmbeddedBlockingXml">
/// Les <c>blocked-process-report</c> extraits de <c>blockingTasks</c>, rendus tels quels : c'est
/// <c>BlockingReportParser</c> qui les lit, pas ce parseur-ci, et ils rejoignent les tables de
/// blocage existantes avec leur porte de confidentialite.
/// </param>
public record ServerDiagnosticsSample(
    DateTime CapturedAt, string Component, string? State, DiagnosticsOutcome Outcome,
    IReadOnlyList<HealthMetric> Metrics,
    IReadOnlyList<HealthWait> Waits,
    IReadOnlyList<HealthCpuRequest> CpuRequests,
    IReadOnlyList<HealthPendingTask> PendingTasks,
    IReadOnlyList<HealthPendingIo> PendingIo,
    IReadOnlyList<HealthMemoryEntry> MemoryEntries,
    IReadOnlyList<string> EmbeddedBlockingXml);
