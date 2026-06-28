// tests/SqlFerret.Core.Tests/ObfuscationRunnerTests.cs
using SqlFerret.Core.Obfuscation;
using SqlFerret.Core.Storage;

public class ObfuscationRunnerTests
{
    private static string Plan(string tbl) =>
        "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\">" +
        $"<RelOp><Object Database=\"[Sales]\" Schema=\"[dbo]\" Table=\"[{tbl}]\" /></RelOp></ShowPlanXML>";

    [Fact]
    public void RunStandalone_writes_anon_plan_and_sibling_map()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obfrun_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var inPath = Path.Combine(dir, "p.sqlplan");
            var outPath = Path.Combine(dir, "p.anon.sqlplan");
            File.WriteAllText(inPath, Plan("Customers"));

            var result = ObfuscationRunner.RunStandalone(inPath, outPath);

            Assert.True(File.Exists(outPath));
            Assert.True(File.Exists(Path.Combine(dir, "p.anon.map.json")));
            Assert.Equal(Path.Combine(dir, "p.anon.map.json"), result.MapPath);
            var anon = File.ReadAllText(outPath);
            Assert.DoesNotContain("Customers", anon);
            Assert.Contains("Table1", anon);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunProject_shares_tokens_across_plans_via_persisted_map()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obfrunp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "wl.duckdb");
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.sqlplan"), Plan("Customers"));
            File.WriteAllText(Path.Combine(dir, "b.sqlplan"), Plan("Customers"));

            using (var db = DuckDbProject.Open(dbPath))
            {
                ObfuscationRunner.RunProject(db, dir, "a");
                ObfuscationRunner.RunProject(db, dir, "b");
            }

            var a = File.ReadAllText(Path.Combine(dir, "a.anon.sqlplan"));
            var b = File.ReadAllText(Path.Combine(dir, "b.anon.sqlplan"));
            Assert.Contains("Table1", a);
            Assert.Contains("Table1", b); // Customers -> Table1 in both via the shared, persisted map
            Assert.True(File.Exists(Path.Combine(dir, "a.map.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunProject_throws_for_missing_plan()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obfmiss_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var db = DuckDbProject.Open(Path.Combine(dir, "wl.duckdb"));
            Assert.Throws<FileNotFoundException>(() => ObfuscationRunner.RunProject(db, dir, "nope"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunFolder_processes_all_plans_with_shared_map()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"obffolder_{Guid.NewGuid():N}");
        var inDir = Path.Combine(baseDir, "in");
        var outDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(inDir);
        var subDir = Path.Combine(inDir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            // a has Customers only -> Table1 (first encounter in ordinal sort)
            // b has Orders only    -> Table2 with shared map; Table1 with a per-file map (falsifiable)
            // c has Customers only -> Table1 again via the shared map (reuse, not re-assign)
            File.WriteAllText(Path.Combine(inDir, "a.sqlplan"), Plan("Customers"));
            File.WriteAllText(Path.Combine(inDir, "b.sqlplan"), Plan("Orders"));
            File.WriteAllText(Path.Combine(subDir, "c.sqlplan"), Plan("Customers"));

            var result = ObfuscationRunner.RunFolder(inDir, outDir);

            Assert.Equal(3, result.FilesFound);
            Assert.Equal(3, result.FilesProcessed);
            Assert.Equal(0, result.FilesFailed);
            Assert.True(File.Exists(Path.Combine(outDir, "a.anon.sqlplan")));
            Assert.True(File.Exists(Path.Combine(outDir, "b.anon.sqlplan")));
            Assert.True(File.Exists(Path.Combine(outDir, "sub", "c.anon.sqlplan")));

            // Map must be OUTSIDE out-dir (sibling), not inside it.
            var expectedMapPath = ObfuscationRunner.DefaultFolderMapPath(outDir);
            Assert.True(File.Exists(expectedMapPath));
            Assert.Equal(expectedMapPath, result.MapPath);
            Assert.False(File.Exists(Path.Combine(outDir, "_folder.map.json")));
            Assert.False(result.MapPath.StartsWith(Path.GetFullPath(outDir) + Path.DirectorySeparatorChar));

            var a = File.ReadAllText(Path.Combine(outDir, "a.anon.sqlplan"));
            var b = File.ReadAllText(Path.Combine(outDir, "b.anon.sqlplan"));
            var c = File.ReadAllText(Path.Combine(outDir, "sub", "c.anon.sqlplan"));

            // a: Customers -> Table1
            Assert.DoesNotContain("Customers", a);
            Assert.Contains("Table1", a);

            // b: Orders -> Table2 (shared map carries Table1 from a's Customers).
            // With a per-file map Orders would be Table1 — this assertion is the falsifiable guard.
            Assert.DoesNotContain("Orders", b);
            Assert.Contains("Table2", b);
            Assert.DoesNotContain("Table1", b);

            // c: Customers reuses Table1 from the shared map
            Assert.DoesNotContain("Customers", c);
            Assert.Contains("Table1", c);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void RunFolder_all_failed_files_writes_map_and_reports_FilesFound()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"obfallfail_{Guid.NewGuid():N}");
        var inDir = Path.Combine(baseDir, "in");
        var outDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(inDir);
        try
        {
            File.WriteAllText(Path.Combine(inDir, "bad1.sqlplan"), "<bad");
            File.WriteAllText(Path.Combine(inDir, "bad2.sqlplan"), "<bad");

            // Must not throw even when every file is malformed.
            var result = ObfuscationRunner.RunFolder(inDir, outDir);

            Assert.Equal(2, result.FilesFound);
            Assert.Equal(0, result.FilesProcessed);
            Assert.Equal(2, result.FilesFailed);
            Assert.Equal(2, result.Failures.Count);
            // Map file is still written (empty map is valid output) — outside out-dir.
            var expectedMapPath = ObfuscationRunner.DefaultFolderMapPath(outDir);
            Assert.True(File.Exists(expectedMapPath));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void RunFolder_records_malformed_plan_as_failure_and_continues()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"obffail_{Guid.NewGuid():N}");
        var inDir = Path.Combine(baseDir, "in");
        var outDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(inDir);
        try
        {
            File.WriteAllText(Path.Combine(inDir, "bad.sqlplan"), "<not valid xml");
            File.WriteAllText(Path.Combine(inDir, "good.sqlplan"), Plan("Orders"));

            var result = ObfuscationRunner.RunFolder(inDir, outDir);

            Assert.Equal(1, result.FilesProcessed);
            Assert.Equal(1, result.FilesFailed);
            Assert.Single(result.Failures);
            Assert.Contains("bad", result.Failures[0]);
            Assert.True(File.Exists(Path.Combine(outDir, "good.anon.sqlplan")));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void RunFolder_throws_for_missing_input_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"obfmissdir_{Guid.NewGuid():N}");
        Assert.Throws<DirectoryNotFoundException>(() => ObfuscationRunner.RunFolder(missing, Path.GetTempPath()));
    }

    [Fact]
    public void RunFolder_map_override_writes_to_custom_path()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"obfoverride_{Guid.NewGuid():N}");
        var inDir = Path.Combine(baseDir, "in");
        var outDir = Path.Combine(baseDir, "out");
        var customMapPath = Path.Combine(baseDir, "custom.map.json");
        Directory.CreateDirectory(inDir);
        try
        {
            File.WriteAllText(Path.Combine(inDir, "a.sqlplan"), Plan("Customers"));

            var result = ObfuscationRunner.RunFolder(inDir, outDir, customMapPath);

            Assert.True(File.Exists(customMapPath));
            Assert.Equal(customMapPath, result.MapPath);
            Assert.False(File.Exists(Path.Combine(outDir, "_folder.map.json")));
            Assert.False(File.Exists(ObfuscationRunner.DefaultFolderMapPath(outDir)));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void RunFolder_matches_sqlplan_case_insensitively()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"obfcase_{Guid.NewGuid():N}");
        var inDir = Path.Combine(baseDir, "in");
        var outDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(inDir);
        try
        {
            File.WriteAllText(Path.Combine(inDir, "plan.SQLPlan"), Plan("Products"));

            var result = ObfuscationRunner.RunFolder(inDir, outDir);

            Assert.Equal(1, result.FilesProcessed);
            Assert.True(File.Exists(Path.Combine(outDir, "plan.anon.sqlplan")));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }
}
