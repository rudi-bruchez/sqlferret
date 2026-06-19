// tests/SqlFerret.Core.Tests/AstClassifierTests.cs
using SqlFerret.Core.Normalization;
using Xunit;

public class AstClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users WHERE Id = 1", "SELECT", "dbo.Users")]
    [InlineData("INSERT INTO Orders (Id) VALUES (1)",    "INSERT", "Orders")]
    [InlineData("UPDATE dbo.T SET x = 1 WHERE id = 2",   "UPDATE", "dbo.T")]
    [InlineData("DELETE FROM Logs WHERE d < '2020-01-01'", "DELETE", "Logs")]
    [InlineData("EXEC dbo.GetOrder @id = 1",             "EXEC",   "dbo.GetOrder")]
    public void Classifies_kind_and_table(string raw, string kind, string? table)
    {
        var (k, t) = AstClassifier.Classify(raw);
        Assert.Equal(kind, k);
        Assert.Equal(table, t);
    }

    [Fact]
    public void Garbage_is_OTHER()
    {
        var (k, t) = AstClassifier.Classify("@@@ ((");
        Assert.Equal("OTHER", k);
        Assert.Null(t);
    }
}
