// tests/SqlFerret.Core.Tests/BlockingDigestTests.cs
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Model;
using SqlFerret.Core.Storage;

public class HasTraversalTests
{
    // Fix 3 — path-segment traversal guard
    [Theory]
    [InlineData("../etc/passwd", true)]
    [InlineData("a/../b", true)]
    [InlineData("foo/../../secret", true)]
    [InlineData("/tmp/report.md", false)]
    [InlineData("q1..q2.md", false)]
    [InlineData("report.md", false)]
    [InlineData("/abs/path/ok.md", false)]
    public void HasTraversal_detects_dotdot_segments(string path, bool expected) =>
        Assert.Equal(expected, BlockingDigestMarkdown.HasTraversal(path));
}

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

    [Fact]
    public void Markdown_render_includes_samples_section()
    {
        var blocker = new SqlFerret.Core.Model.BlockingProcess(
            118, 0, "running", null, SqlFerret.Core.Model.WaitResourceType.Other,
            null, null, null, "X", "rc", 1, "app", "h", "l",
            "update dbo.Orders set status=1 where id=42", "fp_blocker");
        var blocked = new SqlFerret.Core.Model.BlockingProcess(
            201, 0, "suspended", "OBJECT: 5:99:0", SqlFerret.Core.Model.WaitResourceType.Object,
            99, null, 3_750_000L, "S", "rc", 1, "app", "h", "l",
            "select * from dbo.Orders where id=42", "fp_blocked");
        var report = new SqlFerret.Core.Model.BlockingReport(
            new DateTime(2026, 2, 24), 1, 5, blocked, blocker);
        var sample = new BlockingSample("fp_blocker", [report]);

        var digest = new BlockingDigestResult(
            new BlockingOverview(1, 0, new DateTime(2026, 2, 24), new DateTime(2026, 2, 24)),
            [new LocalityStat("Object", 1, 100.0)],
            [], [new BlockingStat("fp_blocker", "update dbo.Orders ...", 1)], [],
            [new ContentionStat("X", 1)], [new ContentionStat("rc", 1)],
            new WaitTimeDist(3_750_000, 3_750_000, 3_750_000),
            [], [sample]);

        var md = BlockingDigestMarkdown.Render(digest);
        Assert.Contains("## Sample", md);
        Assert.Contains("fp_blocker", md);
        Assert.Contains("dbo.Orders", md);
        Assert.Contains("3750000", md);
    }
    /// <summary>
    /// Meme classe d'injection que celle fermee dans le digest de sante, et elle etait restee
    /// ouverte ici. Elle est pire a deux titres : la valeur interpolee est du texte SQL, donc
    /// portant des backticks et des barres sans qu'on l'ait cherche, et sous
    /// <c>--redaction off --sanitize-sql-text raw</c> c'est le texte verbatim du serveur.
    /// <para>Et le cas ordinaire suffit : un SQL sur plusieurs lignes — le cas courant, pas le cas
    /// hostile — sortait deja de sa puce et disloquait la liste. Ce test le prend donc sans
    /// malveillance d'abord, avec malveillance ensuite.</para>
    /// </summary>
    [Fact]
    public void Markdown_render_keeps_capture_text_inside_its_bullet()
    {
        const string Multiline = "update AppSchema.Widget\n  set WidgetScaling = 0\nwhere WidgetId = 42";
        const string Hostile = "select 1` \n\n## Injected heading\n<img src=x onerror=alert(1)>\n\n`";

        var blocker = new SqlFerret.Core.Model.BlockingProcess(
            118, 0, "running", null, SqlFerret.Core.Model.WaitResourceType.Other,
            null, null, null, "X", "rc", 1, "app", "h", "l", Multiline, "fp_blocker");
        var blocked = new SqlFerret.Core.Model.BlockingProcess(
            201, 0, "suspended", "OBJECT: 5:99:0", SqlFerret.Core.Model.WaitResourceType.Object,
            99, null, 3_750_000L, "S", "rc", 1, "app", "h", "l", Hostile, "fp_blocked");
        var report = new SqlFerret.Core.Model.BlockingReport(
            new DateTime(2026, 2, 24), 1, 5, blocked, blocker);

        var digest = new BlockingDigestResult(
            new BlockingOverview(1, 0, new DateTime(2026, 2, 24), new DateTime(2026, 2, 24)),
            [new LocalityStat("Object", 1, 100.0)],
            [],
            [new BlockingStat("fp_blocker", Hostile, 1)],
            [],
            [new ContentionStat("S", 1)],
            [new ContentionStat("read committed (2)", 1)],
            new WaitTimeDist(1, 1, 1),
            [],
            [new BlockingSample("fp_blocker", [report])]);

        var md = BlockingDigestMarkdown.Render(digest);
        var lines = md.Split((char)10).Select(l => l.TrimEnd((char)13)).ToList();

        // Rien ne s'echappe de sa puce : ni le SQL multiligne ordinaire, ni la charge hostile.
        Assert.DoesNotContain(lines, l => l.StartsWith("## Injected", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("<img", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("  set WidgetScaling", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("where WidgetId", StringComparison.Ordinal));
        // Le texte reste lisible, il n'est pas censure.
        Assert.Contains("set WidgetScaling = 0", md, StringComparison.Ordinal);
    }
}
