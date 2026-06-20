namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Unified gauge model for an import: per-file fraction, byte-weighted overall fraction,
/// and the running ingest detail counters. FileIndex is 1-based; 0 before the first file.
/// </summary>
public record ImportProgress(
    int FileIndex, int FileCount, string CurrentFile,
    double FileFraction, double OverallFraction,
    long Read, long Mapped, long Unmapped, long Cleaned, long TokenizeFailures);
