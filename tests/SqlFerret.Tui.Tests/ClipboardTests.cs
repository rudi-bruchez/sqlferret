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

    [Fact]
    public void NativeClipboard_null_setter_uses_fallback()
    {
        var fallback = new FakeFallback();
        var native = new NativeClipboard(fallback, trySetSystemClipboard: null);

        var res = native.Copy("text", "base");

        Assert.True(fallback.WasCalled);
        Assert.Equal(fallback.SentinelResult, res);
    }

    [Fact]
    public void NativeClipboard_setter_returns_false_uses_fallback()
    {
        var fallback = new FakeFallback();
        var native = new NativeClipboard(fallback, trySetSystemClipboard: _ => false);

        var res = native.Copy("text", "base");

        Assert.True(fallback.WasCalled);
        Assert.Equal(fallback.SentinelResult, res);
    }

    [Fact]
    public void NativeClipboard_setter_throws_uses_fallback()
    {
        var fallback = new FakeFallback();
        var native = new NativeClipboard(fallback, trySetSystemClipboard: _ => throw new InvalidOperationException("test"));

        var res = native.Copy("text", "base");

        Assert.True(fallback.WasCalled);
        Assert.Equal(fallback.SentinelResult, res);
    }

    [Fact]
    public void NativeClipboard_setter_returns_true_skips_fallback()
    {
        var fallback = new FakeFallback();
        var native = new NativeClipboard(fallback, trySetSystemClipboard: _ => true);

        var res = native.Copy("text", "base");

        Assert.False(fallback.WasCalled);
        Assert.True(res.ToClipboard);
        Assert.Null(res.FilePath);
        Assert.Equal("copied to clipboard", res.Description);
    }

    private class FakeFallback : IClipboard
    {
        public bool WasCalled { get; private set; }
        public ClipboardResult SentinelResult { get; } = new(false, "/fake/path", "fallback result");

        public ClipboardResult Copy(string text, string suggestedFileBaseName)
        {
            WasCalled = true;
            return SentinelResult;
        }
    }
}
