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
    public void SpExecuteSql_collapses_the_parameter_declaration_to_placeholder()
    {
        // Removed after three rounds of adversarial review each found a new shape that slipped
        // a value through a "keep the declaration verbatim" predicate. It now collapses like any
        // other literal in the outer call — parameter names/types are already persisted per
        // execution in execution_parameters, so nothing of substance is lost.
        const string raw = $"exec sp_executesql N'SELECT Name FROM dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='{Pii}'";

        var result = Sanitize(raw);

        Assert.DoesNotContain("nvarchar", result);
        Assert.Contains("sp_executesql N'select Name from dbo.Customers where Email = @e',?,@e=?", result);
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
            "exec sp_executesql N'select Name from dbo.Customers where Email = @e',?,@e=?",
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

    // Fix rounds 1 and 2: the params slot was originally passed through verbatim with no
    // validation, then with a "declaration-shaped" predicate that three rounds of adversarial
    // review each found a new way past (a value assigned inside the declaration, a comment, a
    // bare word, a bracketed identifier, a numeric default). The predicate was removed entirely
    // — the slot now always collapses like any other literal — so all of these must never leak
    // the embedded value, regardless of how it's smuggled into that slot.

    [Fact]
    public void SpExecuteSql_rejects_a_value_smuggled_inside_the_params_declaration()
    {
        const string raw = $"exec sp_executesql N'SELECT a FROM dbo.t WHERE e=@e',N'@e nvarchar(50) = ''{Pii}''',@e=1";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
        Assert.Equal("exec sp_executesql N'select a from dbo.t where e=@e',?,@e=?", result);
    }

    [Fact]
    public void SpExecuteSql_rejects_a_value_smuggled_inside_a_comment_in_the_params_slot()
    {
        const string raw = $"exec sp_executesql N'SELECT a FROM dbo.t',N'-- {Pii}'";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
        Assert.Equal("exec sp_executesql N'select a from dbo.t',?", result);
    }

    [Fact]
    public void SpExecuteSql_rejects_a_params_slot_that_is_not_declaration_shaped()
    {
        const string raw = "exec sp_executesql N'SELECT 1', N'SECRET'";

        var result = Sanitize(raw);

        Assert.DoesNotContain("SECRET", result);
    }

    [Fact]
    public void SpExecuteSql_rejects_a_numeric_default_hidden_in_a_permitted_integer_token()
    {
        // Integer tokens must stay legal for `nvarchar(50)` to remain verbatim under the old
        // predicate — which is exactly what made a numeric default (`@e int = 123456789`) able
        // to ride through unexamined. There is no longer a predicate to exploit: the whole slot
        // collapses to '?' regardless of what token types it contains.
        const string raw = "exec sp_executesql N'SELECT a FROM dbo.t WHERE e=@e',N'@e int = 123456789',@e=1";

        var result = Sanitize(raw);

        Assert.DoesNotContain("123456789", result);
        Assert.Equal("exec sp_executesql N'select a from dbo.t where e=@e',?,@e=?", result);
    }

    [Fact]
    public void SpExecuteSql_rejects_a_value_hidden_inside_a_bracketed_identifier()
    {
        const string raw = $"exec sp_executesql N'SELECT a FROM dbo.t WHERE e=@e',N'@e nvarchar(50) = [{Pii}]',@e=1";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
        Assert.Equal("exec sp_executesql N'select a from dbo.t where e=@e',?,@e=?", result);
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

    // FIX 1 — double-quoted values under SET QUOTED_IDENTIFIER OFF tokenize as QuotedIdentifier,
    // not a string literal, and were re-emitted verbatim at Literals. The decision is fail-safe:
    // collapse a double-quoted token to '?' too, at Literals only.

    [Fact]
    public void Double_quoted_value_in_a_plain_batch_is_collapsed_at_literals()
    {
        const string raw = $"SELECT Name FROM dbo.Customers WHERE Email = \"{Pii}\"";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
        Assert.Equal("select Name from dbo.Customers where Email = ?", result);
    }

    [Fact]
    public void Double_quoted_value_survives_untouched_at_raw()
    {
        const string raw = $"SELECT Name FROM dbo.Customers WHERE Email = \"{Pii}\"";

        var result = Sanitize(raw, SqlTextSanitization.Raw);

        Assert.Equal(raw, result);
    }

    [Fact]
    public void Double_quoted_value_inside_sp_executesql_inner_statement_is_collapsed()
    {
        const string raw = $"exec sp_executesql N'SELECT a FROM dbo.t WHERE e = \"{Pii}\"'";

        var result = Sanitize(raw);

        Assert.DoesNotContain(Pii, result);
        Assert.Contains("dbo.t", result); // schema/table survives — only the value is lost
        Assert.Equal("exec sp_executesql N'select a from dbo.t where e = ?'", result);
    }

    [Fact]
    public void Bracketed_identifier_is_not_collapsed_at_literals()
    {
        const string raw = "SELECT a FROM [Order Details] WHERE b = 'x'";

        var result = Sanitize(raw);

        Assert.Contains("[Order Details]", result);
    }

    [Fact]
    public void NormalizedHash_is_unchanged_between_raw_and_literals_for_a_double_quoted_query()
    {
        const string raw = $"SELECT Name FROM dbo.Customers WHERE Email = \"{Pii}\"";
        var nq = QueryNormalizer.Normalize(raw);

        var (_, rawNormalized, _) = SqlTextSanitizer.Apply(raw, nq, SqlTextSanitization.Raw);
        var (_, literalsNormalized, _) = SqlTextSanitizer.Apply(raw, nq, SqlTextSanitization.Literals);

        Assert.Equal(rawNormalized.NormalizedHash, literalsNormalized.NormalizedHash);
        Assert.Equal(nq.NormalizedHash, literalsNormalized.NormalizedHash);
    }
}
