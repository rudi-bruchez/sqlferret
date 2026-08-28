using SqlFerret.Core.Normalization;
using Xunit;

public class QueryNormalizerTests
{
    [Fact]
    public void Same_shape_different_literals_share_hash()
    {
        var a = QueryNormalizer.Normalize("SELECT * FROM dbo.Users WHERE Id = 42");
        var b = QueryNormalizer.Normalize("SELECT * FROM dbo.Users WHERE Id = 99");
        Assert.Equal(a.NormalizedHash, b.NormalizedHash);
        Assert.Equal("SELECT", a.StatementKind);
        Assert.Equal("dbo.Users", a.PrimaryTable);
        Assert.False(a.TokenizeFailed);
    }

    [Fact]
    public void Different_shape_differs()
    {
        var a = QueryNormalizer.Normalize("SELECT a FROM t");
        var b = QueryNormalizer.Normalize("SELECT b FROM t");
        Assert.NotEqual(a.NormalizedHash, b.NormalizedHash);
    }
}
