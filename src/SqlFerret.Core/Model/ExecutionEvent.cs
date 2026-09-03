namespace SqlFerret.Core.Model;

public record ExecutionEvent
{
    public DateTime CapturedAt { get; init; }
    public required string EventName { get; init; }
    public EventClass EventClass { get; init; } = EventClass.Unknown;
    public string? ObjectName { get; init; }
    public bool IsSystem { get; init; }
    public string? DatabaseName { get; init; }
    public string? LoginName { get; init; }
    public string? ClientHostname { get; init; }
    public string? ClientAppName { get; init; }
    public int? SessionId { get; init; }
    public long? DurationUs { get; init; }
    public long? CpuTimeUs { get; init; }
    public long? LogicalReads { get; init; }
    public long? PhysicalReads { get; init; }
    public long? Writes { get; init; }
    public long? RowCount { get; init; }
    public string? QueryHash { get; init; }
    public string? QueryPlanHash { get; init; }
    public required string SqlTextRaw { get; init; }

    /// <summary>
    /// The ingest-time sanitization policy of the run this execution came from
    /// (<c>ingestion_runs.sql_text_policy</c>). Null for an event that was never loaded from a
    /// project, or for a project created before the column existed. Anything other than
    /// <c>"raw"</c> means <see cref="SqlTextRaw"/> is not executable T-SQL.
    /// </summary>
    public string? SqlTextPolicy { get; init; }

    public IReadOnlyList<RawParameter> Parameters { get; init; } = [];
    public required string XeFileName { get; init; }
    public long FileOffset { get; init; }
}
