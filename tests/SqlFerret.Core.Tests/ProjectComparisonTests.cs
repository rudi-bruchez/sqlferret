// tests/SqlFerret.Core.Tests/ProjectComparisonTests.cs
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Storage;
using Xunit;

public class ProjectComparisonTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CompareOptions Opt = new(10, null, new CompareThresholds());
    private const string Exec1 = "exec AppSchema.WidgetRecalc @WidgetId = 42";

    private static IEnumerable<CompareFixture.Exec> Burst(string hash, int n, long durationUs, DateTime start, int stepSeconds = 60, string db = "AppDb") =>
        Enumerable.Range(0, n).Select(i => new CompareFixture.Exec(hash, Exec1, durationUs, start.AddSeconds(i * stepSeconds), db));

    [Fact]
    public void Attach_sql_doubles_single_quotes_and_nothing_else()
    {
        Assert.Equal("ATTACH '/tmp/it''s base/sqlferret.duckdb' AS base (READ_ONLY)",
            ProjectComparison.AttachSql("/tmp/it's base/sqlferret.duckdb", "base"));
        Assert.Equal(@"ATTACH 'C:\new\sqlferret.duckdb' AS target (READ_ONLY)",
            ProjectComparison.AttachSql(@"C:\new\sqlferret.duckdb", "target"));
    }

    [Fact]
    public void A_project_compares_with_itself_through_a_path_holding_a_quote()
    {
        using var f = new CompareFixture("_it's");
        f.Import(Burst("h1", 1, 5_000, T0));
        new ProjectComparison(f.DbPath, f.DbPath).Check(Opt);   // must not throw
    }

    [Fact]
    public void A_side_whose_executions_all_lack_a_duration_is_accepted()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 2, 5_000, T0));
        using var b = new CompareFixture();
        b.Import([new CompareFixture.Exec("h1", "SELECT 1", null, T0)]);
        new ProjectComparison(a.DbPath, b.DbPath).Check(Opt);   // must not throw: §5 asks for an execution, not a duration
    }

    [Fact]
    public void A_missing_database_file_is_refused_with_its_path()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        var missing = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}", "sqlferret.duckdb");
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, missing).Check(Opt));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void A_project_missing_a_migrated_column_is_refused_and_told_how_to_migrate()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        b.Sql("ALTER TABLE ingestion_runs DROP COLUMN sql_text_policy");
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, b.DbPath).Check(Opt));
        Assert.Contains("sql_text_policy", ex.Message);
        Assert.Contains("top-slow", ex.Message);
    }

    [Fact]
    public void An_empty_side_after_the_database_filter_is_refused()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        var ex = Assert.Throws<CompareRefusedException>(() =>
            new ProjectComparison(a.DbPath, b.DbPath).Check(Opt with { Database = "NoSuchDb" }));
        Assert.Contains("no execution", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fingerprint_generations_v3_and_v4_are_comparable_and_v2_is_not()
    {
        Assert.Equal(ProjectComparison.FingerprintGeneration(3), ProjectComparison.FingerprintGeneration(4));
        Assert.NotEqual(ProjectComparison.FingerprintGeneration(2), ProjectComparison.FingerprintGeneration(4));
    }

    [Fact]
    public void Different_generations_across_sides_are_refused_even_after_reclassify()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        // Un import ancien, puis un vrai reclassify : il remet normalized_queries a jour, pas le run.
        b.Sql("UPDATE ingestion_runs SET normalizer_version = 2");
        b.Sql("UPDATE normalized_queries SET normalizer_version = 2");
        using (var p = DuckDbProject.Open(b.DbPath)) new Reclassifier(p).Run();
        var ex = Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, b.DbPath).Check(Opt));
        Assert.Contains("re-import", ex.Message);
        Assert.DoesNotContain("reclassify", ex.Message);
    }

    [Fact]
    public void Two_generations_on_one_side_are_refused()
    {
        using var a = new CompareFixture();
        var old = a.Import(Burst("h1", 1, 5_000, T0));
        a.Import(Burst("h1", 1, 5_000, T0.AddHours(1)));
        a.Sql($"UPDATE ingestion_runs SET normalizer_version = 2 WHERE run_id = {old}");
        Assert.Throws<CompareRefusedException>(() => new ProjectComparison(a.DbPath, a.DbPath).Check(Opt));
    }

    [Fact]
    public void A_v3_side_against_a_v4_side_is_accepted()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        b.Sql("UPDATE ingestion_runs SET normalizer_version = 3");
        new ProjectComparison(a.DbPath, b.DbPath).Check(Opt);   // must not throw
    }

    [Fact]
    public void A_lock_error_is_described_as_another_process_holding_the_project()
    {
        var msg = ProjectComparison.DescribeAttachFailure("/p/sqlferret.duckdb",
            new Exception("IO Error: Could not set lock on file \"/p/sqlferret.duckdb\": Conflicting lock is held in /usr/bin/x (PID 3)"));
        Assert.Contains("/p/sqlferret.duckdb", msg);
        Assert.Contains("another SQLFerret process", msg);
    }

    [Fact]
    public void Active_span_is_the_sum_of_run_spans_not_the_gap_between_imports()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 2, 5_000, T0));             // 0 and +60 s
        a.Import(Burst("h1", 2, 5_000, T0.AddDays(1)));  // a day later, 0 and +60 s
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(2, cov.Base.Runs.Count);
        Assert.Equal(120_000_000L, cov.Base.ActiveSpanUs);
        Assert.Equal(4L, cov.Base.Executions);
    }

    [Fact]
    public void A_short_active_span_adds_the_per_hour_note()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 3, 5_000, T0));             // 120 s, under 10 minutes
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Contains(cov.Notes, n => n.Contains("active span under the threshold", StringComparison.Ordinal));
    }

    [Fact]
    public void A_large_gap_inside_one_run_is_named()
    {
        using var a = new CompareFixture();
        var run = a.Import(
            Burst("h1", 2, 5_000, T0, 300).Concat(Burst("h1", 2, 5_000, T0.AddHours(3), 300)));
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(run, cov.Base.LargestGapRunId);
        Assert.Contains(cov.Notes, n => n.Contains($"run {run} has a gap"));
    }

    [Fact]
    public void Each_run_is_checked_for_a_split_against_its_own_span()
    {
        using var a = new CompareFixture();
        var steady = a.Import(Burst("h1", 6, 5_000, T0, 7200));    // every 2 h over 10 h: the largest gap, no split
        var day = T0.AddDays(1);
        var split = a.Import(Burst("h1", 2, 5_000, day).Concat(Burst("h1", 1, 5_000, day.AddMinutes(31))));   // 0, 1 min, 31 min
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(steady, cov.Base.LargestGapRunId);
        Assert.Contains(cov.Notes, n => n.Contains($"run {split} has a gap"));
        Assert.DoesNotContain(cov.Notes, n => n.Contains($"run {steady} has a gap"));
    }

    [Fact]
    public void Coverage_reports_policies_and_plan_eligibility()
    {
        using var a = new CompareFixture();
        var run = a.Import(Burst("h1", 2, 5_000, T0), SqlTextSanitization.Literals, "hash");
        a.Plans(run,
            CompareFixture.Plan("0A1B2C3D4E5F6071", "P1", 1_000),
            CompareFixture.Plan("0A1B2C3D4E5F6071", "M1", 1_000, "multi"));
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(["literals"], cov.Base.SqlTextPolicies);
        Assert.Equal(["hash"], cov.Base.RedactionPolicies);
        Assert.Equal(1L, cov.Base.EligiblePlanProfiles);
        Assert.Equal(1L, cov.Base.ExcludedPlanProfiles);
        Assert.Contains(cov.Notes, n => n.Contains("predicates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Executions_without_a_duration_are_left_out_and_counted()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 2, 5_000, T0).Append(new CompareFixture.Exec("h1", Exec1, null, T0.AddMinutes(5))));
        var cov = new ProjectComparison(a.DbPath, a.DbPath).CoverageOnly(Opt);
        Assert.Equal(2L, cov.Base.Executions);
        Assert.Equal(1L, cov.Base.ExecutionsWithoutDuration);
    }

    private const string Leaky = "select * from AppSchema.WidgetRecalc where Code = \"GADGET-7781\"";

    [Fact]
    public void A_raw_first_side_is_not_trusted_and_the_literals_side_text_is_printed()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        b.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));                       // raw first
        b.Import(Burst("h1", 1, 5_000, T0.AddDays(1)).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        var text = new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h1");
        Assert.NotNull(text);
        Assert.DoesNotContain("GADGET-7781", text);
        Assert.NotEqual(ProjectComparison.TextWithheld, text);
    }

    [Fact]
    public void A_hash_known_only_to_a_raw_first_side_is_withheld()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0), SqlTextSanitization.Literals);
        b.Import(Burst("h2", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        b.Import(Burst("h1", 1, 5_000, T0.AddDays(1)), SqlTextSanitization.Literals);
        Assert.Equal(ProjectComparison.TextWithheld, new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h2"));
    }

    [Fact]
    public void Two_all_raw_projects_print_the_stored_text()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        b.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        Assert.Contains("GADGET-7781", new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h1"));
    }

    [Fact]
    public void A_hash_first_met_in_a_raw_blocking_report_is_withheld()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0), SqlTextSanitization.Literals);
        b.BlockingOnly("hb", Leaky, SqlTextSanitization.Raw);                                         // wrote the text
        b.Import(Burst("hb", 1, 5_000, T0.AddHours(1)).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        Assert.Equal(ProjectComparison.TextWithheld, new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "hb"));
    }

    [Fact]
    public void Sanitizing_is_required_unless_every_run_on_both_sides_is_raw()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0));
        b.Import(Burst("h1", 1, 5_000, T0));
        Assert.False(ProjectComparison.MustSanitize(new ProjectComparison(a.DbPath, b.DbPath).CoverageOnly(Opt)));
        b.Import(Burst("h1", 1, 5_000, T0.AddHours(1)), SqlTextSanitization.Literals);
        Assert.True(ProjectComparison.MustSanitize(new ProjectComparison(a.DbPath, b.DbPath).CoverageOnly(Opt)));

        // mirrored: a literals run on the base side only, the target all raw
        using var c = new CompareFixture();
        using var d = new CompareFixture();
        c.Import(Burst("h1", 1, 5_000, T0), SqlTextSanitization.Literals);
        d.Import(Burst("h1", 1, 5_000, T0));
        Assert.True(ProjectComparison.MustSanitize(new ProjectComparison(c.DbPath, d.DbPath).CoverageOnly(Opt)));
    }

    [Fact]
    public void A_literals_base_against_a_raw_target_prints_the_base_text()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }), SqlTextSanitization.Literals);
        b.Import(Burst("h1", 1, 5_000, T0).Select(e => e with { Sql = Leaky }));
        var text = new ProjectComparison(a.DbPath, b.DbPath).TextOf(Opt, "h1");
        Assert.NotNull(text);
        Assert.DoesNotContain("GADGET-7781", text);
        Assert.NotEqual(ProjectComparison.TextWithheld, text);
        Assert.Equal(new ProjectComparison(a.DbPath, a.DbPath).TextOf(Opt, "h1"), text);
    }

    [Fact]
    public void Ten_times_more_frequent_at_the_same_speed_ranks_as_a_load_increase()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("h1", 6, 10_000, T0, 600));      // 6 over 50 min
        b.Import(Burst("h1", 60, 10_000, T0, 50));      // 60 over 49 min 10 s
        var load = new ProjectComparison(a.DbPath, b.DbPath).LoadOnly(Opt);
        Assert.NotNull(load);
        var row = Assert.Single(load.Value.Up);
        Assert.Equal("h1", row.NormalizedHash);
        // Base : 60 000 us sur 3 000 s de span actif, soit 72 000 us par heure.
        Assert.Equal(72_000d, row.BaseUsPerHour, 0);
        Assert.Empty(load.Value.Down);
    }

    [Fact]
    public void Load_is_not_computed_below_the_active_span_threshold()
    {
        using var a = new CompareFixture();
        a.Import(Burst("h1", 3, 10_000, T0));
        Assert.Null(new ProjectComparison(a.DbPath, a.DbPath).LoadOnly(Opt));
    }

    [Fact]
    public void Appeared_and_disappeared_are_listed_with_their_full_counts()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("gone1", 2, 10_000, T0, 600).Concat(Burst("gone2", 2, 10_000, T0, 600)).Concat(Burst("both", 2, 10_000, T0, 600)));
        b.Import(Burst("new1", 2, 10_000, T0, 600).Concat(Burst("both", 2, 10_000, T0, 600)));
        var (appeared, disappeared) = new ProjectComparison(a.DbPath, b.DbPath).OneSidedOnly(Opt with { Limit = 1 });
        Assert.Equal(1L, appeared.Total);
        Assert.Equal("new1", Assert.Single(appeared.Rows).NormalizedHash);
        Assert.Equal(2L, disappeared.Total);
        Assert.Single(disappeared.Rows);
    }

    [Fact]
    public void The_database_filter_applies_to_one_sided_statements()
    {
        using var a = new CompareFixture();
        using var b = new CompareFixture();
        a.Import(Burst("both", 2, 10_000, T0, 600).Concat(Burst("otherdb", 2, 10_000, T0, 600, db: "AppDb2")));
        b.Import(Burst("both", 2, 10_000, T0, 600));
        var (_, disappeared) = new ProjectComparison(a.DbPath, b.DbPath).OneSidedOnly(Opt with { Database = "AppDb" });
        Assert.Equal(0L, disappeared.Total);
        var (_, all) = new ProjectComparison(a.DbPath, b.DbPath).OneSidedOnly(Opt);
        Assert.Equal(1L, all.Total);
    }
}
