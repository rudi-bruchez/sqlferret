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
}
