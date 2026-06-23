using SqlFerret.Core.Project;
using Xunit;

// Item 3: Isolate env-mutating tests from xUnit parallelism.
[CollectionDefinition("EnvMutating", DisableParallelization = true)]
public class EnvMutatingCollection { }

[Collection("EnvMutating")]
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
            Assert.True(Directory.Exists(p.ExportsFolder));   // Item 8: exports/ created eagerly
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
            Thread.Sleep(50);
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
    public void PlansFolder_absolute_config_honored_as_is()
    {
        var dir = NewTempDir();
        var abs = NewTempDir(); // an absolute path that is not under the project dir
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sqlferret.config.json"),
            $$"""{ "server": { "plansFolder": "{{abs.Replace("\\", "\\\\")}}" } }""");
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.Equal(abs, p.PlansFolder);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (Directory.Exists(abs)) Directory.Delete(abs, true);
        }
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

    // Item 4: friendly error when --project resolves to an existing regular file.
    [Fact]
    public void OpenOrCreate_throws_IOException_when_path_is_existing_file()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ap_{Guid.NewGuid():N}.duckdb");
        File.WriteAllText(filePath, "placeholder");
        try
        {
            var ex = Assert.Throws<IOException>(() => AuditProject.OpenOrCreate(filePath));
            Assert.Contains("--project must be a directory", ex.Message);
            Assert.Contains(filePath, ex.Message);
        }
        finally { if (File.Exists(filePath)) File.Delete(filePath); }
    }

    // Item 5: corrupt manifest → RecoveredFromCorruptManifest = true; normal reopen = false.
    [Fact]
    public void RecoveredFromCorruptManifest_true_when_json_unreadable()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), "{ broken ");
        try
        {
            var p = AuditProject.OpenOrCreate(dir);
            Assert.True(p.RecoveredFromCorruptManifest);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RecoveredFromCorruptManifest_false_on_normal_reopen()
    {
        var dir = NewTempDir();
        try
        {
            AuditProject.OpenOrCreate(dir);          // create
            var second = AuditProject.OpenOrCreate(dir); // reopen
            Assert.False(second.RecoveredFromCorruptManifest);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
