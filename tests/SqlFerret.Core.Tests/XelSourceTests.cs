// tests/SqlFerret.Core.Tests/XelSourceTests.cs
using SqlFerret.Core.Ingestion;
using Xunit;

public class XelSourceTests
{
    [Fact]
    public void Single_file_returns_itself()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var f = Path.Combine(dir, "s_0.xel"); File.WriteAllText(f, "x");
        var (files, bytes) = XelSource.Resolve(f);
        Assert.Single(files);
        Assert.Equal(1, bytes);
    }

    [Fact]
    public void Folder_returns_only_xel_nonrecursive()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "s_0.xel"), "ab");
        File.WriteAllText(Path.Combine(dir, "s_1.xel"), "c");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignore");
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "deep.xel"), "z");
        var (files, bytes) = XelSource.Resolve(dir);
        Assert.Equal(2, files.Count);
        Assert.Equal(3, bytes);
        Assert.EndsWith("s_0.xel", files[0]);
    }

    [Fact]
    public void Missing_path_throws()
        => Assert.Throws<FileNotFoundException>(() => XelSource.Resolve("/no/such/path.xel"));
}
