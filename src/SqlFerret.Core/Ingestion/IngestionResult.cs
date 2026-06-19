// src/SqlFerret.Core/Ingestion/IngestionResult.cs
namespace SqlFerret.Core.Ingestion;

public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures);
