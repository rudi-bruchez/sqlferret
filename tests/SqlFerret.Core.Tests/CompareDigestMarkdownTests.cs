// tests/SqlFerret.Core.Tests/CompareDigestMarkdownTests.cs
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Normalization;
using Xunit;

public class CompareDigestMarkdownTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CompareOptions Opt = new(10, null, new CompareThresholds());
    private const string Exec1 = "exec AppSchema.WidgetRecalc @WidgetId = 42";
    private const string Leaky = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";

    [Fact]
    public void Markdown_has_every_section_in_order_and_says_when_one_is_empty()
    {
        using var a = new CompareFixture();
        a.Import(Enumerable.Range(0, 3).Select(i => new CompareFixture.Exec("h1", Exec1, 5_000, T0.AddMinutes(i))));
        var d = new ProjectComparison(a.DbPath, a.DbPath).Run(Opt);
        var md = CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms");
        string[] order = ["## Coverage", "## Cost per execution", "## Load per hour", "## Appeared and disappeared", "## Plans"];
        var at = order.Select(h => md.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0));
        Assert.Equal(at.OrderBy(i => i), at);
        Assert.Contains("No regression above the thresholds.", md);
        Assert.Contains("not computed", md);
        Assert.Contains("Base runs:", md);
        Assert.Contains("| Executions | 3 | 3 |", md);
        Assert.Contains("| Smallest duration | 5 ms | 5 ms |", md);
    }

    [Fact]
    public void Markdown_of_a_raw_then_literals_project_does_not_carry_the_value()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        var execs = Enumerable.Range(0, 6).Select(i => new CompareFixture.Exec("h1", Leaky, 10_000 * (i + 1), T0.AddMinutes(i * 10))).ToList();
        a.Import(execs, SqlTextSanitization.Literals);
        b.Import(execs);
        b.Import(execs.Select(e => e with { At = e.At.AddDays(1), DurationUs = e.DurationUs * 3 }), SqlTextSanitization.Literals);
        b.Import([new CompareFixture.Exec("h9", Leaky, 50_000, T0)]);
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(Opt);
        Assert.DoesNotContain("GADGET-7781", CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms"));
    }

    [Fact]
    public void Markdown_lists_each_run_the_largest_gap_and_never_a_plan_statement_text()
    {
        using var a = new CompareFixture();
        var r1 = a.Import(Enumerable.Range(0, 2).Select(i => new CompareFixture.Exec("h1", Exec1, 5_000, T0.AddMinutes(i * 5))));
        a.Import(Enumerable.Range(0, 2).Select(i => new CompareFixture.Exec("h1", Exec1, 5_000, T0.AddDays(1).AddMinutes(i))));
        a.Plans(r1, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));
        var d = new ProjectComparison(a.DbPath, a.DbPath).Run(Opt);
        var md = CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms");
        Assert.Contains($"| {r1} |", md);
        Assert.Contains("Largest gap", md);
        Assert.Equal(2, md.Split($"(run {r1})").Length - 1);   // une fois par cote
        Assert.DoesNotContain("WidgetId = 4242", md);
    }

    [Fact]
    public void Markdown_lists_a_changed_plan_row_without_its_statement_text()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        CompareFixture.Exec Linked() => new("h1", Exec1, 5_000, T0, QueryHash: "728224406569967729");
        var ra = a.Import([Linked()]);
        var rb = b.Import([Linked()]);
        a.Plans(ra, CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000));
        b.Plans(rb, CompareFixture.Plan("0A1B2C3D4E5F6071", "P2", 9_000, "queryplanhash",
            new SqlFerret.Core.Plans.PlanFinding("spill_to_tempdb", 3, "{}")));
        var d = new ProjectComparison(a.DbPath, b.DbPath).Run(Opt);
        var md = CompareDigestMarkdown.Render(new CompareDigestEnvelope(1, T0, d), "ms");
        Assert.Contains("| 0A1B2C3D4E5F6071 | yes | spill_to_tempdb |", md);
        Assert.Contains("| h1 |", md);
        Assert.DoesNotContain("WidgetId = 4242", md);
    }
}
