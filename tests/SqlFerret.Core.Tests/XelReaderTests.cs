// tests/SqlFerret.Core.Tests/XelReaderTests.cs
using SqlFerret.Core.Ingestion;
using Xunit;

public class XelReaderTests
{
    /// <summary>
    /// Walks up from AppContext.BaseDirectory until a directory named "sample"
    /// containing at least one *.xel file is found. Returns null if not found.
    /// </summary>
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

    [Fact]
    public void Empty_file_list_yields_no_events()
    {
        var result = new XelReader().Read([]);
        Assert.Empty(result);
    }

    [SkippableFact]
    public void Reads_real_workload_events()
    {
        var sampleDir = FindSampleDir();
        Skip.If(sampleDir is null, "sample/ folder not present (real .xel traces are gitignored / not on CI)");

        // Pick the smallest *.xel whose name contains "performances"; fall back to smallest overall.
        var allXels = Directory.GetFiles(sampleDir!, "*.xel")
            .Select(f => new FileInfo(f))
            .ToList();

        var performanceFiles = allXels.Where(f => f.Name.Contains("performances")).ToList();
        var chosen = (performanceFiles.Count > 0 ? performanceFiles : allXels)
            .OrderBy(f => f.Length)
            .First();

        var events = new XelReader().Read([chosen.FullName]).ToList();

        Assert.NotEmpty(events);

        if (chosen.Name.Contains("performances"))
        {
            Assert.Contains(events, e =>
                e.ev.Name.Contains("batch", StringComparison.OrdinalIgnoreCase) ||
                e.ev.Name.Contains("rpc", StringComparison.OrdinalIgnoreCase));
        }
    }

    [SkippableFact]
    public void Read_invokes_progress_callbacks_per_event_and_on_file_complete()
    {
        var sampleDir = FindSampleDir();
        Skip.If(sampleDir is null, "sample/ folder not present (real .xel traces are gitignored / not on CI)");

        var chosen = Directory.GetFiles(sampleDir!, "*.xel")
            .Select(f => new FileInfo(f)).OrderBy(f => f.Length).First();

        long lastRead = 0; int readCalls = 0;
        long completeCount = -1; int completeCalls = 0;

        var events = new XelReader().Read(
            [chosen.FullName],
            onRead: (_, n) => { lastRead = n; readCalls++; },
            onFileComplete: (_, n) => { completeCount = n; completeCalls++; }).ToList();

        Assert.NotEmpty(events);
        Assert.True(readCalls > 0, "onRead must fire at least once");
        Assert.Equal(1, completeCalls);                  // exactly one file completed
        Assert.Equal(events.Count, completeCount);       // exact count matches yielded events
        Assert.Equal(events.Count, lastRead);            // last running count == total
    }
}
