// tests/SqlFerret.Core.Tests/RpcSanitizationTests.cs
using SqlFerret.Core.Normalization;
using Xunit;

public class RpcSanitizationTests
{
    private const string Pii = "alice@example.com";

    private static string Sanitize(string raw, SqlTextSanitization level = SqlTextSanitization.Literals)
    {
        var nq = QueryNormalizer.Normalize(raw);
        var (text, _, _) = SqlTextSanitizer.Apply(raw, nq, level);
        return text;
    }

    [Fact]
    public void SpExecuteSql_keeps_the_inner_querys_table_and_column_names()
    {
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw);

        Assert.Contains("dbo.Customers", result);
        Assert.Contains("Name", result);
        Assert.Contains("Email", result);
    }

    [Fact]
    public void SpExecuteSql_replaces_the_inner_querys_literal_values_with_placeholder()
    {
        const string raw = "exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Status = ''active''',N'@e nvarchar(50)',@e='x'";

        var result = Sanitize(raw);

        Assert.DoesNotContain("active", result);
    }

    [Fact]
    public void SpExecuteSql_keeps_the_parameter_declaration_verbatim()
    {
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw);

        Assert.Contains("N'@e nvarchar(50)'", result);
    }

    [Fact]
    public void SpExecuteSql_collapses_trailing_parameter_values_to_placeholder()
    {
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
        Assert.Contains("@e=?", result);
    }

    [Fact]
    public void SpExecuteSql_matches_the_worked_example_exactly()
    {
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw);

        Assert.Equal(
            "exec sp_executesql N'select Name from dbo.Customers where Email = @e',N'@e nvarchar(50)',@e=?",
            result);
    }

    [Fact]
    public void Plain_stored_procedure_rpc_still_sanitizes_correctly()
    {
        const string raw = "exec dbo.GetCustomer @Email='x'";

        var result = Sanitize(raw);

        Assert.Equal("exec dbo.GetCustomer @Email=?", result);
        Assert.DoesNotContain("'x'", result);
    }

    [Fact]
    public void SpExecuteSql_with_statement_in_a_variable_falls_back_and_leaks_no_value()
    {
        const string raw = $"exec sp_executesql @stmt, @params, @e='{Pii}'";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
    }

    [Fact]
    public void SpExecuteSql_with_unparseable_inner_statement_falls_back_and_leaks_no_value()
    {
        // The inner statement text (the outer literal's *content*) has an unmatched '(' — a
        // parse error for TokenNormalizer.Normalize(inner), even though the outer exec text
        // parses fine (it's just a string literal from the outer parser's point of view).
        // TokenNormalizer's fallback for the inner text would keep the PII literal intact, so
        // this must fall back to nq.NormalizedSql (fully collapsed) instead of re-emitting it.
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = ''{Pii}'' AND (',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
    }

    [Fact]
    public void Raw_is_unaffected()
    {
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw, SqlTextSanitization.Raw);

        Assert.Equal(raw, result);
    }
}
