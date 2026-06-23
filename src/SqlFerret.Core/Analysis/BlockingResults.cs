// src/SqlFerret.Core/Analysis/BlockingResults.cs
namespace SqlFerret.Core.Analysis;

public record BlockingOverview(long ReportCount, long DeadlockCount, DateTime? FirstAt, DateTime? LastAt);
public record LocalityStat(string WaitResourceType, long Count, double Pct);
public record ContentionStat(string Key, long Count);
public record BlockingStat(string Fingerprint, string NormalizedSql, long Count);
public record WaitTimeDist(long P50Us, long P95Us, long MaxUs);
public record ChainStat(int? MonitorLoop, int Depth, int? HeadSpid, long EdgeCount);
