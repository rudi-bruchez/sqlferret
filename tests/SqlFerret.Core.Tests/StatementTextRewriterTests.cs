// tests/SqlFerret.Core.Tests/StatementTextRewriterTests.cs
using SqlFerret.Core.Obfuscation;

public class StatementTextRewriterTests
{
    private static ObfuscationMap MapWith(params (NameKind, string)[] names)
    {
        var m = new ObfuscationMap();
        foreach (var (k, n) in names) m.Token(k, n);
        return m;
    }

    [Fact]
    public void Maps_identifiers_and_scrubs_literals()
    {
        var m = MapWith((NameKind.Table, "Customers"), (NameKind.Column, "SSN"));
        var outSql = StatementTextRewriter.Rewrite("SELECT SSN FROM Customers WHERE SSN = '123-45-6789'", m);
        Assert.Contains("Col1", outSql);
        Assert.Contains("Table1", outSql);
        Assert.Contains("?", outSql);
        Assert.DoesNotContain("Customers", outSql);
        Assert.DoesNotContain("SSN", outSql);
        Assert.DoesNotContain("123-45-6789", outSql);
    }

    [Fact]
    public void Preserves_keywords_and_unmapped_identifiers()
    {
        var m = MapWith((NameKind.Table, "Customers"));
        var outSql = StatementTextRewriter.Rewrite("SELECT getdate() FROM Customers", m);
        Assert.Contains("getdate", outSql);   // built-in preserved (not in map)
        Assert.Contains("SELECT", outSql);     // keyword casing preserved
    }

    [Fact]
    public void Strips_comments_that_might_leak()
    {
        var m = MapWith((NameKind.Table, "Customers"));
        var outSql = StatementTextRewriter.Rewrite("SELECT 1 FROM Customers /* ssn 123-45-6789 */", m);
        Assert.DoesNotContain("123-45-6789", outSql);
    }

    [Fact]
    public void Fallback_on_unparsable_sql_still_scrubs_names_and_literals()
    {
        var m = MapWith((NameKind.Table, "Customers"));
        // Deliberately broken so ScriptDom.Parse reports errors and the fallback runs.
        var outSql = StatementTextRewriter.Rewrite("@@@ not sql (( Customers 'secret' 42", m);
        Assert.DoesNotContain("Customers", outSql);
        Assert.DoesNotContain("secret", outSql);
        Assert.DoesNotContain("42", outSql);
        Assert.Contains("Table1", outSql);
    }

    // ─── Fallback boundary fix: #temp and @param identifiers ────────────────

    [Fact]
    public void Fallback_temp_table_identifier_is_scrubbed()
    {
        // #Secret starts with '#' (non-word char): \b boundaries fail there.
        // The identifier-aware lookaround fix must catch it both bare and bracketed.
        // A predicate fragment like "[tempdb].[dbo].[#Secret].[Col]='v'" is NOT a valid
        // SQL statement, so ScriptDom.Parse always returns errors → Fallback runs.
        var m = MapWith((NameKind.TempTable, "#Secret"));
        var outSql = StatementTextRewriter.Rewrite("[tempdb].[dbo].[#Secret].[Col]='v'", m);
        Assert.DoesNotContain("#Secret", outSql);
        Assert.Contains("#Temp1", outSql);
    }

    [Fact]
    public void Fallback_at_parameter_identifier_is_scrubbed()
    {
        // @IdArticles starts with '@' (non-word char): \b boundaries fail there.
        var m = MapWith((NameKind.Parameter, "@IdArticles"));
        var outSql = StatementTextRewriter.Rewrite("@IdArticles > 0 @@@", m);
        Assert.DoesNotContain("IdArticles", outSql);
    }

    [Fact]
    public void Fallback_no_partial_match_in_longer_identifier()
    {
        // Mapping only "Foo" must not corrupt "FooBar" in the fallback path.
        var m = MapWith((NameKind.Table, "Foo"));
        var outSql = StatementTextRewriter.Rewrite("FooBar @@@", m);
        Assert.Contains("FooBar", outSql);
        Assert.DoesNotContain("Table1Bar", outSql);
    }
}
