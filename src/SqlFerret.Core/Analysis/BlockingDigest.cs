// src/SqlFerret.Core/Analysis/BlockingDigest.cs
using DuckDB.NET.Data;

namespace SqlFerret.Core.Analysis;

/// <summary>The reusable digest engine. CLI exposes it now; the Spec 2 MCP server reuses it verbatim.
/// Pure data out (BlockingDigestResult) — no formatting.</summary>
public class BlockingDigest(DuckDBConnection conn)
{
    public const int SchemaVersion = 1;

    public BlockingDigestResult Build(int samplesPerPattern = 5, int topK = 10)
    {
        var q = new BlockingQueries(conn);
        var topBlockers = q.TopBlockers(topK);
        var samples = topBlockers
            .Select(b => new BlockingSample(b.Fingerprint, q.SampleReports(b.Fingerprint, samplesPerPattern)))
            .Where(s => s.Reports.Count > 0)
            .ToList();
        return new BlockingDigestResult(
            q.Overview(), q.Locality(), q.TopObjects(topK), topBlockers, q.TopBlocked(topK),
            q.LockModes(), q.IsolationLevels(), q.WaitTimes(), q.Chains(), samples);
    }
}
