// src/SqlFerret.Core/Plans/PlanProfile.cs
namespace SqlFerret.Core.Plans;

/// <summary>Un événement query_post_execution_plan_profile, décodé.</summary>
public record PlanProfile
{
    public required string PlanHash { get; init; }         // nu, sans préfixe : c'est une clé de jointure
    public required string PlanHashSource { get; init; }   // "queryplanhash" | "multi" | "content"
    public required string FileStem { get; init; }         // "p_" / "m_" / "c_" + PlanHash
    public int StatementCount { get; init; }
    public string? QueryHash { get; init; }
    public string? StatementType { get; init; }
    public string? StatementText { get; init; }
    public int StatementTextLength { get; init; }
    public DateTime CapturedAt { get; init; }
    public long? DurationUs { get; init; }
    public long? CpuTimeUs { get; init; }
    public double? EstimatedRows { get; init; }
    public double? SubtreeCost { get; init; }
    public int? Dop { get; init; }
    public long? SerialDesiredMemoryKb { get; init; }
    public long? GrantedMemoryKb { get; init; }
    public long? MaxUsedMemoryKb { get; init; }
    public IReadOnlyList<PlanFinding> Findings { get; init; } = [];
}

/// <summary>Une anomalie détectée dans un plan. DetailJson est un objet JSON sérialisé.</summary>
public record PlanFinding(string Kind, int? NodeId, string DetailJson);

/// <summary>Seuils de détection. Pas de plomberie de configuration en v1 : la couture suffit.</summary>
public record PlanFindingThresholds
{
    public double RowGoalRatio { get; init; } = 100;
    public double GrantOversizeRatio { get; init; } = 2.0;
    public long GrantAbsoluteKb { get; init; } = 1_048_576;   // 1 Go
    public double CardinalityRatio { get; init; } = 10;
    public long LargeScanRows { get; init; } = 1_000_000;
    public double RebindsThreshold { get; init; } = 1000;

    /// <summary>
    /// Plancher sous lequel le ratio accordé/utilisé n'est plus signalé. Surdimensionner
    /// d'un facteur 1000 un grant d'un mégaoctet est exact et sans conséquence ; ne le
    /// publier qu'au-delà d'un volume qui pèse réellement garde le signal lisible.
    /// Ne borne que la règle du ratio — le seuil absolu sur la demande reste actif.
    /// </summary>
    public long GrantRatioFloorKb { get; init; } = 16_384;   // 16 Mo

    /// <summary>
    /// Coût d'instruction au-dessus duquel une raison de non-parallélisme due au code est
    /// signalée. Le plan ne porte pas le coût seuil de parallélisme de l'instance ; la
    /// valeur par défaut documentée de ce réglage (5) en tient lieu.
    /// </summary>
    public double NonParallelMinCost { get; init; } = 5;

    /// <summary>Lignes totales d'un opérateur parallèle sous lesquelles le déséquilibre est ignoré.</summary>
    public long ThreadSkewMinRows { get; init; } = 10_000;

    /// <summary>Rapport du thread le plus chargé à la moyenne, thread 0 exclu.</summary>
    public double ThreadSkewRatio { get; init; } = 2;
}

/// <summary>
/// Ce que le writer a fait de ce plan. Porte `is_first` : seul WroteFirst le met à vrai.
/// </summary>
public enum PlanWriteOutcome { WroteFirst, WroteWorst, Skipped, Failed }

/// <summary>Une exécution retenue pour un plan, avec son instant exact et son fichier.</summary>
public record PlanFileRef(string Path, DateTime CapturedAtUtc, long? DurationUs, long? CpuTimeUs);

/// <summary>Une ligne agrégée par plan distinct, produite par une requête DuckDB.</summary>
public record PlanDigestRow
{
    public required string PlanHash { get; init; }
    public required string PlanHashSource { get; init; }
    public required string FileStem { get; init; }
    public string? QueryHash { get; init; }
    public string? StatementType { get; init; }
    public string? StatementText { get; init; }
    public int StatementTextLength { get; init; }
    public int StatementCount { get; init; }
    public long ExecutionCount { get; init; }
    public long? DurationMinUs { get; init; }
    public long? DurationMaxUs { get; init; }
    public long? CpuMinUs { get; init; }
    public long? CpuMaxUs { get; init; }
    public DateTime CapturedMinUtc { get; init; }
    public DateTime CapturedMaxUtc { get; init; }
    public int? Dop { get; init; }
    public long? SerialDesiredMemoryKb { get; init; }
    public long? GrantedMemoryKb { get; init; }
    public long? MaxUsedMemoryKb { get; init; }
    public PlanFileRef? First { get; init; }
    public PlanFileRef? Worst { get; init; }
    public IReadOnlyList<PlanFinding> Findings { get; init; } = [];
}
