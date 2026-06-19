// src/SqlFerret.Core/Analysis/Results.cs
namespace SqlFerret.Core.Analysis;

public record QueryStat(string NormalizedHash, string StatementKind, string? PrimaryTable,
    string NormalizedSql, long Count, double AvgDurationUs, long P95DurationUs,
    long MaxDurationUs, long TotalDurationUs);

public record Occurrence(long ExecutionId, DateTime CapturedAt, string? Database, string? Login,
    long? DurationUs, string SqlTextRaw);

public record ParamImpact(string ValueText, long Count, double AvgDurationUs, long P95DurationUs, long MaxDurationUs);

public record DimensionStat(string Value, long Count, long TotalDurationUs);

public record QualityStat(long EventsRead, long EventsMapped, long EventsUnmapped, long EventsCleaned, long TokenizeFailures);
