// src/SqlFerret.Core/Ingestion/IngestionResult.cs
namespace SqlFerret.Core.Ingestion;

public record IngestionResult(long RunId, long Read, long Mapped, long Unmapped, long Cleaned,
    long TokenizeFailures, long Blocking, long Deadlocks, long BlockingParseFailures,
    long PlanProfiles = 0, long PlanParseFailures = 0, long PlanWriteFailures = 0,
    long SqlTextSanitizeFailures = 0,
    long ServerDiagnostics = 0, long ServerDiagnosticsUnhandled = 0,
    long ServerDiagnosticsParseFailures = 0,
    // Sous-documents d'un evenement, pas des evenements : hors de la somme de reconciliation.
    long EmbeddedBlocking = 0, long EmbeddedBlockingFailures = 0);
