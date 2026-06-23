// tests/SqlFerret.Core.Tests/BlockingQueriesTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Tests;

public class BlockingQueriesTests
{
    private static DuckDbProject Seed()
    {
        var db = DuckDbProject.Open(Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb"));
        long run = db.BeginRun("logs/", 1, 0, "masked");
        PreparedBlockingProcess P(int spid, WaitResourceType t, long? wus, string role, string fp) =>
            new(new BlockingProcess(spid, 0, "s", "X", t, t == WaitResourceType.Object ? 99 : null, null, wus, "S", "rc", 1, "app", "h", "l", "sql", fp), null, "sql");
        // two reports: both blocked on OBJECT (locality = Object dominant), blocker fingerprint fp_block
        for (int i = 0; i < 2; i++)
            db.InsertBlockingBatch(run, [new PreparedBlockingReport(
                new BlockingReport(new DateTime(2026, 2, 24), 1, 5, default!, default!),
                P(200 + i, WaitResourceType.Object, 5_000_000L, "blocked", "fp_blocked"),
                P(118, WaitResourceType.Other, null, "blocking", "fp_block"))]);
        return db;
    }

    [Fact]
    public void Locality_and_top_blockers_aggregate()
    {
        using var db = Seed();
        var q = new BlockingQueries(db.Connection);
        Assert.Equal(2, q.Overview().ReportCount);
        var loc = q.Locality();
        Assert.Equal("Object", loc[0].WaitResourceType);   // dominant blocked wait-resource type
        Assert.Equal(2, loc[0].Count);
        var blockers = q.TopBlockers(10);
        Assert.Equal("fp_block", blockers[0].Fingerprint);
        Assert.Equal(2, blockers[0].Count);
        Assert.Equal(5_000_000L, q.WaitTimes().MaxUs);
    }

    private static string? FindSampleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.xel").Length > 0)
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public void Real_blocking_xel_ingests_and_aggregates()
    {
        var sampleDir = FindSampleDir();
        var sample = sampleDir is not null
            ? Directory.GetFiles(sampleDir, "*.xel")
                .FirstOrDefault(f => Path.GetFileName(f).Contains("block", StringComparison.OrdinalIgnoreCase))
            : null;
        Skip.If(sample is null, "no blocking sample .xel present");

        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            var res = ImportRunner.Run(db,
                new IngestionOptions(RedactionMode.Masked, []), sample!);
            Assert.True(res.Blocking + res.BlockingParseFailures > 0, "expected blocking events in the sample");
            var q = new BlockingQueries(db.Connection);
            Assert.NotEmpty(q.Locality());   // proves the field name 'blocked_process' is correct (spec §9)
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
