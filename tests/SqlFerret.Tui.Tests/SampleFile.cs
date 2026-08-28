// tests/SqlFerret.Tui.Tests/SampleFile.cs
// Shared helper for locating the gitignored sample/ folder of .xel traces.

internal static class SampleFile
{
    /// <summary>
    /// Walk up from the test bin directory to find the gitignored sample/ folder.
    /// Prefers the smallest non-performances_* .xel; falls back to the smallest .xel overall.
    /// Returns null (test should Skip) when no sample directory or .xel file is found.
    /// </summary>
    public static string? FindSmallest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sample = Path.Combine(dir.FullName, "sample");
            if (Directory.Exists(sample))
            {
                var nonPerf = Directory.GetFiles(sample, "*.xel")
                    .Where(f => !Path.GetFileName(f).StartsWith("performances_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => new FileInfo(f).Length)
                    .FirstOrDefault();
                if (nonPerf is not null) return nonPerf;

                var any = Directory.GetFiles(sample, "*.xel")
                    .OrderBy(f => new FileInfo(f).Length)
                    .FirstOrDefault();
                if (any is not null) return any;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
