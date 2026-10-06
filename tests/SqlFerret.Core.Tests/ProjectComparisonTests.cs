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
}
