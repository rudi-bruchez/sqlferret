// tests/SqlFerret.Core.Tests/CliCompareTests.cs
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

public class CliCompareTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static string CliPath() =>
        Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "SqlFerret.Cli.exe" : "SqlFerret.Cli");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(CliPath()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static CompareFixture Project()
    {
        var f = new CompareFixture();
        f.Import(Enumerable.Range(0, 3).Select(i => new CompareFixture.Exec("h1", "exec AppSchema.WidgetRecalc @WidgetId = 42", 5_000, T0.AddMinutes(i))));
        return f;
    }

    [Fact]
    public void Compare_writes_nothing_into_either_project()
    {
        using var a = Project();
        using var b = Project();
        var before = (Snapshot(a.Dir), Snapshot(b.Dir));
        var (code, output, err) = Run("compare", "--base", a.Dir, "--target", b.Dir, "--format", "both");
        Assert.True(code == 0, err);
        Assert.Contains("## Coverage", output);
        Assert.Contains("SchemaVersion", output);
        Assert.Equal(before.Item1, Snapshot(a.Dir));
        Assert.Equal(before.Item2, Snapshot(b.Dir));
    }

    [Theory]
    [InlineData("--target")]
    [InlineData("--base")]
    public void A_missing_side_is_an_error(string present)
    {
        using var a = Project();
        var (code, _, err) = Run("compare", present, a.Dir);
        Assert.Equal(1, code);
        Assert.Contains("--base and --target", err);
    }

    [Theory]
    [InlineData("--format", "xml", "--format must be")]
    [InlineData("--limit", "0", "--limit must be")]
    [InlineData("--limit", "x", "--limit must be")]
    [InlineData("--out", "../escape.md", "invalid --out")]
    public void Bad_options_are_refused(string flag, string value, string message)
    {
        using var a = Project();
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, flag, value);
        Assert.Equal(1, code);
        Assert.Contains(message, err);
    }

    [Fact]
    public void A_bare_database_flag_is_refused_rather_than_read_as_all()
    {
        using var a = Project();
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--database");
        Assert.Equal(1, code);
        Assert.Contains("--database", err);
        Assert.Contains("needs a value", err);
    }

    [Fact]
    public void An_output_file_inside_a_project_is_refused()
    {
        using var a = Project();
        var before = Snapshot(a.Dir);
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--out", Path.Combine(a.Dir, "report.md"));
        Assert.Equal(1, code);
        Assert.Contains("inside", err);
        Assert.Equal(before, Snapshot(a.Dir));
    }

    [Fact]
    public void An_output_in_a_missing_directory_exits_one_without_a_stack_trace()
    {
        using var a = Project();
        var outPath = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}", "report.md");
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--out", outPath);
        Assert.Equal(1, code);
        Assert.Contains("cannot write", err);
        Assert.DoesNotContain("Unhandled", err);
    }

    [SkippableFact]
    public void An_output_reaching_a_project_through_a_symlink_is_refused()
    {
        Skip.If(OperatingSystem.IsWindows(), "creating a symbolic link needs a privilege on Windows");
        using var a = Project();
        var link = Path.Combine(Path.GetTempPath(), $"sf_cmp_link_{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, a.Dir);
        try
        {
            var before = Snapshot(a.Dir);
            var (code, _, err) = Run("compare", "--base", a.Dir, "--target", a.Dir, "--out", Path.Combine(link, "report.md"));
            Assert.Equal(1, code);
            Assert.Contains("inside", err);
            Assert.Equal(before, Snapshot(a.Dir));
        }
        finally { Directory.Delete(link); }   // supprime le lien, pas sa cible
    }

    [Fact]
    public void A_refusal_exits_one_with_its_message()
    {
        using var a = Project();
        var missing = Path.Combine(Path.GetTempPath(), $"sf_cmp_{Guid.NewGuid():N}");
        var (code, _, err) = Run("compare", "--base", a.Dir, "--target", missing);
        Assert.Equal(1, code);
        Assert.Contains("no project database", err);
        Assert.False(Directory.Exists(missing));
    }
}
