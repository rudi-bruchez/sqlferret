// src/SqlFerret.Core/Ingestion/IngestionProgress.cs
namespace SqlFerret.Core.Ingestion;

public record IngestionProgress(
    long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures, string CurrentFile);
