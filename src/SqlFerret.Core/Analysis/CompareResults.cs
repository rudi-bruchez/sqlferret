// src/SqlFerret.Core/Analysis/CompareResults.cs
namespace SqlFerret.Core.Analysis;

/// <summary>Seuils de compare. Tous en microsecondes ou en comptes : l'invariant de Core.</summary>
public record CompareThresholds(
    int MinExecutions = 5,
    long MinAvgDurationUs = 1_000,
    long MinActiveSpanUs = 600_000_000);

public record CompareOptions(int Limit, string? Database, CompareThresholds Thresholds);

public record CompareRunSpan(long RunId, DateTime First, DateTime Last, long SpanUs, long Executions);

public record CompareSideCoverage(
    string ProjectDir,
    IReadOnlyList<CompareRunSpan> Runs,
    long ActiveSpanUs,
    long? LargestGapUs,
    long? LargestGapRunId,
    long Executions,
    long ExecutionsWithoutDuration,
    long DistinctStatements,
    IReadOnlyList<string> Databases,
    long OtherDatabases,
    IReadOnlyList<int> NormalizerVersions,
    IReadOnlyList<string> RedactionPolicies,
    IReadOnlyList<string> SqlTextPolicies,
    long? MinDurationUs,
    double QueryHashShare,
    long EligiblePlanProfiles,
    long ExcludedPlanProfiles);

public record CompareCoverage(CompareSideCoverage Base, CompareSideCoverage Target, IReadOnlyList<string> Notes);

/// <param name="Ratio">Moyenne cible sur moyenne de base ; NULL quand la base vaut zero (§7).</param>
public record CostRow(
    string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql,
    long BaseCount, long TargetCount,
    double BaseAvgUs, double TargetAvgUs, double BaseP95Us, double TargetP95Us,
    double? BaseAvgCpuUs, double? TargetAvgCpuUs, double? BaseAvgReads, double? TargetAvgReads,
    double? Ratio);

public record LoadRow(
    string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql,
    double BaseExecPerHour, double TargetExecPerHour,
    double BaseUsPerHour, double TargetUsPerHour, double DeltaUsPerHour);

public record OneSideRow(
    string NormalizedHash, string StatementKind, string? PrimaryTable, string NormalizedSql,
    long Executions, long TotalDurationUs, double? UsPerHour);

public record OneSideList(IReadOnlyList<OneSideRow> Rows, long Total);

public record PlanChangeRow(
    string QueryHash,
    IReadOnlyList<string> BasePlanHashes, IReadOnlyList<string> TargetPlanHashes, bool PlanChanged,
    IReadOnlyList<string> AppearedKinds, IReadOnlyList<string> DisappearedKinds,
    double? BaseMedianUs, double? TargetMedianUs, string? LinkedNormalizedHash);

public record PlanSection(
    bool Skipped, string? SkipReason, IReadOnlyList<PlanChangeRow> Rows, long Total, long UnlinkedExcluded);

public record CompareDigestResult(
    CompareCoverage Coverage,
    IReadOnlyList<CostRow> Regressions, IReadOnlyList<CostRow> Gains,
    bool LoadComputed, IReadOnlyList<LoadRow> LoadIncreases, IReadOnlyList<LoadRow> LoadDecreases,
    OneSideList Appeared, OneSideList Disappeared,
    PlanSection Plans);

public record CompareDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, CompareDigestResult Digest);

/// <summary>Un refus du §5 : le message est destine tel quel a l'utilisateur.</summary>
public sealed class CompareRefusedException(string message) : Exception(message);
