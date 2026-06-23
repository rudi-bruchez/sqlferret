// tests/SqlFerret.Core.Tests/BlockingDigestTests.cs
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;

public class BlockingDigestTests
{
    [Fact]
    public void Build_assembles_rollups_and_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(path);
            long run = db.BeginRun("logs/", 1, 0, "masked");
            BlockingProcess B(int spid, string role, string fp) =>
                new(spid, 0, "s", "OBJECT: 5:99:0", WaitResourceType.Object, 99, null, 5_000_000L, "S", "rc", 1, "app", "h", "l", "sql", fp);
            db.InsertBlockingBatch(run, [new PreparedBlockingReport(
                new BlockingReport(new DateTime(2026, 2, 24), 1, 5, default!, default!),
                new PreparedBlockingProcess(B(201, "blocked", "fp_bd"), null, "exec X"),
                new PreparedBlockingProcess(B(118, "blocking", "fp_bk"), null, "update Y"))]);

            var digest = new BlockingDigest(db.Connection).Build(samplesPerPattern: 3, topK: 5);
            Assert.Equal(1, digest.Overview.ReportCount);
            Assert.Equal("Object", digest.Locality[0].WaitResourceType);
            Assert.Equal("fp_bk", digest.TopBlockers[0].Fingerprint);
            Assert.Single(digest.Samples);                       // one dominant blocker pattern
            Assert.Equal("fp_bk", digest.Samples[0].Fingerprint);
            Assert.Single(digest.Samples[0].Reports);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Markdown_render_includes_locality_and_top_blockers()
    {
        var digest = new BlockingDigestResult(
            new BlockingOverview(2, 0, new DateTime(2026, 2, 24), new DateTime(2026, 2, 24)),
            [new LocalityStat("Object", 2, 100.0)],
            [],
            [new BlockingStat("fp_bk", "UPDATE dbo.Y SET ...", 2)],
            [],
            [new ContentionStat("S", 2)],
            [new ContentionStat("read committed (2)", 2)],
            new WaitTimeDist(5_000_000, 5_000_000, 5_000_000),
            [],
            []);
        var md = BlockingDigestMarkdown.Render(digest);
        Assert.Contains("Object", md);
        Assert.Contains("UPDATE dbo.Y", md);
        Assert.Contains("100", md);
    }
}
