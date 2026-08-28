// tests/SqlFerret.Core.Tests/SqlTextSanitizerTests.cs
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using Xunit;

public class SqlTextSanitizerTests
{
    private const string Pii = "alice@example.com";

    [Fact]
    public void Raw_returns_the_statement_and_the_normalized_query_untouched()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Email = 'alice@example.com'";
        var nq = QueryNormalizer.Normalize(sql);

        var (text, normalized, failed) = SqlTextSanitizer.Apply(sql, nq, SqlTextSanitization.Raw);

        Assert.Equal(sql, text);
        Assert.Same(nq, normalized);
        Assert.False(failed);
    }

    [Fact]
    public void Literals_stores_the_normalized_sql_and_drops_the_value()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Email = 'alice@example.com'";
        var nq = QueryNormalizer.Normalize(sql);
        Assert.False(nq.TokenizeFailed);   // guards the premise of this test

        var (text, normalized, failed) = SqlTextSanitizer.Apply(sql, nq, SqlTextSanitization.Literals);

        Assert.Equal(nq.NormalizedSql, text);
        Assert.DoesNotContain(Pii, text);
        Assert.Contains("?", text);
        Assert.False(failed);
    }

    [Fact]
    public void Literals_keeps_identifiers_because_it_removes_values_not_schema()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Email = 'alice@example.com'";
        var nq = QueryNormalizer.Normalize(sql);

        var (text, _, _) = SqlTextSanitizer.Apply(sql, nq, SqlTextSanitization.Literals);

        Assert.Contains("Customers", text);
    }

    // ScriptDom fails on an unterminated string literal; TokenNormalizer then falls back to
    // FallbackCollapse, which only lowercases and collapses whitespace — the literal survives.
    // Both the statement text AND the normalized SQL must be replaced by the placeholder.
    [Fact]
    public void Literals_on_tokenize_failure_scrubs_text_and_normalized_sql()
    {
        const string truncated = $"exec dbo.X @Email='{Pii}";   // no closing quote
        var nq = QueryNormalizer.Normalize(truncated);
        Assert.True(nq.TokenizeFailed);           // guards the premise of this test
        Assert.Contains(Pii, nq.NormalizedSql);   // documents why the guard is needed

        var (text, normalized, failed) = SqlTextSanitizer.Apply(truncated, nq, SqlTextSanitization.Literals);

        Assert.Equal(SqlTextSanitizer.Placeholder, text);
        Assert.Equal(SqlTextSanitizer.Placeholder, normalized.NormalizedSql);
        Assert.DoesNotContain(Pii, normalized.NormalizedSql);
        Assert.True(failed);
    }

    [Fact]
    public void Literals_on_tokenize_failure_preserves_the_fingerprint()
    {
        const string truncated = $"exec dbo.X @Email='{Pii}";
        var nq = QueryNormalizer.Normalize(truncated);

        var (_, normalized, _) = SqlTextSanitizer.Apply(truncated, nq, SqlTextSanitization.Literals);

        Assert.Equal(nq.NormalizedHash, normalized.NormalizedHash);
        Assert.Equal(nq.StatementKind, normalized.StatementKind);
    }

    [Fact]
    public void Version_is_one()
    {
        Assert.Equal(1, SqlTextSanitizer.Version);
    }
}
