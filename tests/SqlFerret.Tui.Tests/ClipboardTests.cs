// tests/SqlFerret.Tui.Tests/ClipboardTests.cs
using SqlFerret.Tui.Clipboard;

public class ClipboardTests
{
    [Fact]
    public void FileFallback_writes_sql_file_and_returns_path()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clip = new FileFallbackClipboard(dir);
            var res = clip.Copy("SELECT 1;", "exec-42");
            Assert.False(res.ToClipboard);
            Assert.NotNull(res.FilePath);
            Assert.True(File.Exists(res.FilePath));
            Assert.Equal("SELECT 1;", File.ReadAllText(res.FilePath!));
            Assert.EndsWith("exec-42.sql", res.FilePath);
        }
        finally { Directory.Delete(dir, true); }
    }
}
