// src/SqlFerret.Core/Storage/PreparedBlocking.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Storage;

public record PreparedBlockingProcess(BlockingProcess Process, NormalizedQuery? Normalized, string? StoredInputBuf);
/// <param name="Source">
/// <c>event</c> pour un <c>blocked_process_report</c> declenche par seuil, <c>diagnostics</c> pour
/// un rapport extrait de <c>queryProcessing/blockingTasks</c> d'un cycle sp_server_diagnostics.
/// Les deux ne mesurent pas la meme chose et ne se comptent jamais ensemble : le second est un
/// instantane pris a cadence fixe, le premier un evenement declenche.
/// </param>
public record PreparedBlockingReport(
    BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking,
    string? RawXml = null, string Source = "event");
