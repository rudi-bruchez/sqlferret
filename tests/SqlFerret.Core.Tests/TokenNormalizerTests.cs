using SqlFerret.Core.Normalization;
using Xunit;

public class TokenNormalizerTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 42",      "select * from dbo.Users where Id = ?")]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 99",      "select * from dbo.Users where Id = ?")]
    [InlineData("EXEC dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR'",
                "exec dbo.GetOrder @OrderId = ?, @Culture = ?")]
    [InlineData("SELECT 1 -- a comment\nFROM t",              "select ? from t")]
    [InlineData("SELECT * FROM t WHERE c IN (1, 2, 3)",       "select * from t where c in (?)")]
    [InlineData("SELECT * FROM [my table] WHERE x = 0x1A",    "select * from [my table] where x = ?")]
    [InlineData("SELECT * FROM t WHERE s = 'it''s'",          "select * from t where s = ?")]
    public void Normalizes_literals_and_shape(string raw, string expected)
    {
        var (normalized, failed) = TokenNormalizer.Normalize(raw);
        Assert.False(failed);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Unparseable_input_falls_back_and_flags()
    {
        var (normalized, failed) = TokenNormalizer.Normalize("@@@ not sql ((");
        Assert.True(failed);
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }
}
