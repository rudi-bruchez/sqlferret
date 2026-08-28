namespace SqlFerret.Core.Storage;

/// <summary>The single source of the Query Store runtime-stats metric set. Each entry yields five
/// generated columns (avg_/min_/max_/last_/stdev_<Duck>) read from QS columns (avg_/.../stdev_<Qs>).</summary>
public static class QdsSchema
{
    // (DuckDB column prefix, Query Store source column base).
    public static readonly (string Duck, string Qs)[] RuntimeMetrics =
    [
        ("duration_us", "duration"),                            // µs (QS native)
        ("cpu_time_us", "cpu_time"),                            // µs
        ("clr_time_us", "clr_time"),                            // µs
        ("logical_io_reads", "logical_io_reads"),              // 8KB pages
        ("logical_io_writes", "logical_io_writes"),            // 8KB pages
        ("physical_io_reads", "physical_io_reads"),            // 8KB pages
        ("rowcount", "rowcount"),                              // rows
        ("dop", "dop"),                                        // degree of parallelism
        ("query_max_used_memory_8kb_pages", "query_max_used_memory"),
        ("tempdb_space_used_8kb_pages", "tempdb_space_used"),  // 2017+
        ("log_bytes_used", "log_bytes_used"),                  // 2017+, bytes
    ];

    // Metrics added in SQL Server 2017 (NULL on 2016).
    public static readonly HashSet<string> V2017Plus = ["tempdb_space_used", "log_bytes_used"];
}

/// <summary>
/// One aggregate set for a metric; any value may be NULL (QS NULLs, or a 2017+ metric on SQL 2016).
/// Avg/Min/Max/Last are stored as long: Query Store's avg_* columns are float, so fractional averages
/// are intentionally truncated (spec: only stdev is DOUBLE). Do not "fix" this without a schema change.
/// </summary>
public readonly record struct MetricAggregate(long? Avg, long? Min, long? Max, long? Last, double? Stdev);

public record QdsRunInfo(string? ServerName, string? DatabaseName, DateTime? WindowFrom, DateTime? WindowTo,
    string? SqlServerVersion, string? ActualState, string? DesiredState, bool WaitStatsAvailable, bool PlansRequested);

public record QdsRunCounters(long QueriesCount, long QueryTextCount, long PlansCount,
    long RuntimeStatRows, long WaitStatRows, long PlanFilesWritten, long PlanWriteFailures);

public record QdsQueryTextRow(long QueryTextId, string? QuerySqlText, bool IsPartOfEncryptedModule, bool HasRestrictedText);

public record QdsQueryRow(long QueryId, long? QueryTextId, long? ObjectId, string? ObjectName,
    string? QueryHash, string? QueryParameterizationType, bool IsInternalQuery, long CountCompiles, DateTime? LastExecutionTime);

public record QdsPlanRow(long PlanId, long QueryId, string? QueryPlanHash, string? EngineVersion,
    int? CompatibilityLevel, bool IsForcedPlan, bool IsTrivialPlan, bool IsParallelPlan,
    int ForceFailureCount, string? LastForceFailureReasonDesc, long CountCompiles, DateTime? LastExecutionTime,
    string? SqlplanPath, bool PlanWritten);

public record QdsRuntimeStatRow(long RuntimeStatsId, long PlanId, long IntervalId,
    DateTime IntervalStart, DateTime IntervalEnd, string? ExecutionType, long CountExecutions,
    IReadOnlyList<MetricAggregate> Metrics); // length 11, in QdsSchema.RuntimeMetrics order

public record QdsWaitStatRow(long WaitStatsId, long PlanId, long IntervalId, string? WaitCategory,
    string? ExecutionType, MetricAggregate WaitTimeUs, long TotalWaitTimeUs);
