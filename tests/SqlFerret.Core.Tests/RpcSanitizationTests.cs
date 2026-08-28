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

    // Fix round 1: the params slot was passed through with no validation that it actually held
    // a declaration and not a value. Reproduced by an adversarial re-review; these three shapes
    // must never leak the embedded value regardless of how it's smuggled into that slot.

    [Fact]
    public void SpExecuteSql_rejects_a_value_smuggled_inside_the_params_declaration()
    {
        const string raw = $"exec sp_executesql N'SELECT a FROM dbo.t WHERE e=@e',N'@e nvarchar(50) = ''{Pii}''',@e=1";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
    }

    [Fact]
    public void SpExecuteSql_rejects_a_value_smuggled_inside_a_comment_in_the_params_slot()
    {
        const string raw = $"exec sp_executesql N'SELECT a FROM dbo.t',N'-- {Pii}'";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
    }

    [Fact]
    public void SpExecuteSql_rejects_a_params_slot_that_is_not_declaration_shaped()
    {
        const string raw = "exec sp_executesql N'SELECT 1', N'SECRET'";

        var result = Sanitize(raw);

        Assert.DoesNotContain("SECRET", result);
    }

    [Fact]
    public void SpExecuteSql_still_passes_a_genuine_multi_parameter_declaration_verbatim()
    {
        const string raw = "exec sp_executesql N'SELECT 1',N'@e nvarchar(50), @n int',@e='x',@n=1";

        var result = Sanitize(raw);

        Assert.Contains("N'@e nvarchar(50), @n int'", result);
    }

    [Fact]
    public void Batch_with_no_sp_executesql_sanitizes_identically_with_the_fast_path_guard()
    {
        const string raw = $"SELECT * FROM dbo.Customers WHERE Email = '{Pii}'";

        var result = Sanitize(raw);

        Assert.Equal(QueryNormalizer.Normalize(raw).NormalizedSql, result);
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
