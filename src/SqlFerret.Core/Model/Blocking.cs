// src/SqlFerret.Core/Model/Blocking.cs
namespace SqlFerret.Core.Model;

public enum WaitResourceType { Key, Object, Page, Rid, Database, PageLatch, AppLock, Other }

public record BlockingProcess(
    int? Spid, int? Ecid, string? Status,
    string? WaitResourceRaw, WaitResourceType WaitResourceType,
    long? ObjectId, long? HobtId,
    long? WaitTimeUs, string? LockMode, string? IsolationLevel, int? TranCount,
    string? ClientApp, string? HostName, string? LoginName,
    string? InputBufRaw, string? InputBufFingerprint);

public record BlockingReport(
    DateTime CapturedAt, int? MonitorLoop, int? DatabaseId,
    BlockingProcess Blocked, BlockingProcess Blocking);

public record DeadlockReport(
    DateTime CapturedAt, IReadOnlyList<int> VictimSpids,
    IReadOnlyList<int> ParticipantSpids, string GraphXmlRedacted);
