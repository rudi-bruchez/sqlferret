// tests/SqlFerret.Core.Tests/CliExportHealthTests.cs
using System.Diagnostics;
using Xunit;

public class CliExportHealthTests
{
    private static string CliPath() =>
        Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "SqlFerret.Cli.exe" : "SqlFerret.Cli");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(CliPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    [Fact]
    public void Export_health_on_a_fresh_project_says_there_is_no_data_and_exits_zero()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var (code, output, _) = Run("export-health", "--project", dir, "--format", "md");

            Assert.Equal(0, code);
            Assert.Contains("Coverage", output, StringComparison.OrdinalIgnoreCase);
            // « aucune donnee » n'est pas « serveur sain » : les deux messages doivent differer.
            Assert.Contains("no diagnostics", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Format_both_produces_both_and_does_not_fall_back_to_markdown()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var (code, output, _) = Run("export-health", "--project", dir, "--format", "both");

            Assert.Equal(0, code);
            Assert.Contains("Coverage", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("schemaVersion", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unknown_format_is_refused_rather_than_silently_defaulted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var (code, _, err) = Run("export-health", "--project", dir, "--format", "xml");

            Assert.NotEqual(0, code);
            Assert.Contains("--format", err);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Out_rejects_path_traversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var (code, _, err) = Run("export-health", "--project", dir, "--out", "../escape.md");

            Assert.NotEqual(0, code);
            Assert.Contains("--out", err);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
