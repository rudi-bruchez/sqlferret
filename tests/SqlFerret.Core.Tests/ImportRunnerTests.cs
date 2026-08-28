using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Tests;

public class ImportRunnerTests
{
    private static string? FindSampleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.xel").Length > 0)
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public void Run_imports_sample_and_emits_monotonic_overall_ending_at_full()
    {
        var sampleDir = FindSampleDir();
        Skip.If(sampleDir is null, "sample/ folder not present");

        var chosen = Directory.GetFiles(sampleDir!, "*.xel")
            .Select(f => new FileInfo(f)).OrderBy(f => f.Length).First();

        var dbPath = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using var db = DuckDbProject.Open(dbPath);
            var ticks = new List<ImportProgress>();
            var sink = new ListProgress(ticks.Add);

            var result = ImportRunner.Run(db,
                new IngestionOptions(RedactionMode.Masked, []), chosen.FullName, sink);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);

            // Overall fraction is monotonic non-decreasing …
            var overall = ticks.Select(t => t.OverallFraction).ToList();
            Assert.True(overall.SequenceEqual(overall.OrderBy(x => x)), "overall must be monotonic");

            // … and reaches 1.0 by the end (single-file import).
            Assert.Equal(1.0, ticks[^1].OverallFraction, 3);
            Assert.Equal(1, ticks[^1].FileCount);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
