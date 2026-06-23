using SqlFerret.Core.Project;
using Xunit;

public class AuditProjectTests
{
    static string NewTempDir() => Path.Combine(Path.GetTempPath(), $"ap_{Guid.NewGuid():N}");

    [Fact]
    public void OpenOrCreate_creates_skeleton()
    {
        var dir = NewTempDir();
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.True(Directory.Exists(p.Directory));
            Assert.True(Directory.Exists(Path.Combine(p.Directory, "plans")));
            Assert.True(File.Exists(Path.Combine(p.Directory, "project.json")));
            Assert.True(File.Exists(Path.Combine(p.Directory, "README.md")));
            Assert.Equal(ProjectManifest.CurrentSchemaVersion, p.Manifest.SchemaVersion);
            Assert.Equal(p.Manifest.CreatedUtc, p.Manifest.LastOpenedUtc);
            Assert.Equal(Path.Combine(p.Directory, "sqlferret.duckdb"), p.DuckDbPath);
            Assert.Equal(Path.Combine(p.Directory, "exports"), p.ExportsFolder);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reopen_bumps_LastOpenedUtc_keeps_CreatedUtc()
    {
        var dir = NewTempDir();
        try
        {
            var first = AuditProject.OpenOrCreate(dir);
            var created = first.Manifest.CreatedUtc;
            Thread.Sleep(15);
            var second = AuditProject.OpenOrCreate(dir);
            Assert.Equal(created, second.Manifest.CreatedUtc);
            Assert.True(second.Manifest.LastOpenedUtc > first.Manifest.LastOpenedUtc);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Malformed_manifest_reinitializes_without_throwing()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), "{ broken ");
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(ProjectManifest.CurrentSchemaVersion, p.Manifest.SchemaVersion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Readme_not_overwritten_on_reopen()
    {
        var dir = NewTempDir();
        try
        {
            AuditProject.OpenOrCreate(dir);
            var readme = Path.Combine(Path.GetFullPath(dir), "README.md");
            File.WriteAllText(readme, "USER EDIT");
            AuditProject.OpenOrCreate(dir);
            Assert.Equal("USER EDIT", File.ReadAllText(readme));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void OpenDb_opens_database_at_project_path()
    {
        var dir = NewTempDir();
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            using var db = p.OpenDb();
            Assert.NotNull(db.Connection);
            Assert.True(File.Exists(p.DuckDbPath));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PlansFolder_defaults_inside_project()
    {
        var dir = NewTempDir();
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(Path.Combine(p.Directory, "plans"), p.PlansFolder);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PlansFolder_relative_config_resolved_against_project()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sqlferret.config.json"),
            """{ "server": { "plansFolder": "myplans" } }""");
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(Path.Combine(p.Directory, "myplans"), p.PlansFolder);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Project_config_wins_over_cwd_config()
    {
        var proj = NewTempDir();
        var cwd = NewTempDir();
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "sqlferret.config.json"),
            """{ "display": { "durationUnit": "us" } }""");
        File.WriteAllText(Path.Combine(proj, "sqlferret.config.json"),
            """{ "display": { "durationUnit": "s" } }""");
        try
        {
            var p = AuditProject.OpenOrCreate(proj, cwd);
            Assert.Equal("s", p.Config.DurationUnit);
        }
        finally { Directory.Delete(proj, true); Directory.Delete(cwd, true); }
    }

    [Fact]
    public void Project_env_wins_over_cwd_env()
    {
        var proj = NewTempDir();
        var cwd = NewTempDir();
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, ".env"), "SF_PREC_A=cwd\n");
        File.WriteAllText(Path.Combine(proj, ".env"), "SF_PREC_A=project\n");
        Environment.SetEnvironmentVariable("SF_PREC_A", null);
        try
        {
            AuditProject.OpenOrCreate(proj, cwd);
            Assert.Equal("project", Environment.GetEnvironmentVariable("SF_PREC_A"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SF_PREC_A", null);
            Directory.Delete(proj, true); Directory.Delete(cwd, true);
        }
    }

    [Fact]
    public void Real_env_wins_over_project_env()
    {
        var proj = NewTempDir();
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, ".env"), "SF_PREC_B=project\n");
        Environment.SetEnvironmentVariable("SF_PREC_B", "real");
        try
        {
            AuditProject.OpenOrCreate(proj);
            Assert.Equal("real", Environment.GetEnvironmentVariable("SF_PREC_B"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SF_PREC_B", null);
            Directory.Delete(proj, true);
        }
    }
}
