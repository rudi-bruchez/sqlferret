// tests/SqlFerret.Core.Tests/ObfuscationMapTests.cs
using SqlFerret.Core.Obfuscation;
using Xunit;

public class ObfuscationMapTests
{
    [Fact]
    public void Token_is_deterministic_and_per_kind_sequential()
    {
        var m = new ObfuscationMap();
        Assert.Equal("Table1", m.Token(NameKind.Table, "[Customers]"));
        Assert.Equal("Table2", m.Token(NameKind.Table, "Orders"));
        Assert.Equal("Table1", m.Token(NameKind.Table, "customers")); // case-insensitive, bracket-insensitive, reused
        Assert.Equal("Col1", m.Token(NameKind.Column, "SSN"));
    }

    [Fact]
    public void Json_roundtrip_preserves_entries()
    {
        var m = new ObfuscationMap();
        m.Token(NameKind.Table, "Customers");
        m.Token(NameKind.Column, "SSN");
        var back = ObfuscationMap.FromJson(m.ToJson());
        Assert.Equal("Table1", back.Token(NameKind.Table, "Customers"));
        Assert.Equal("Col1", back.Token(NameKind.Column, "ssn"));
    }

    [Fact]
    public void FromEntries_continues_numbering_without_collision()
    {
        var m = ObfuscationMap.FromEntries(new[] { (NameKind.Table, "Customers", "Table1") });
        Assert.Equal("Table1", m.Token(NameKind.Table, "Customers"));
        Assert.Equal("Table2", m.Token(NameKind.Table, "Orders")); // next free number, no clash
    }

    [Fact]
    public void TextLookup_resolves_table_over_column_on_collision()
    {
        var m = new ObfuscationMap();
        m.Token(NameKind.Column, "Name");
        m.Token(NameKind.Table, "Name");
        Assert.Equal("Table1", m.BuildTextLookup()["name"]); // Table precedence wins
    }

    // ─── Review fix #14: zero-hex suffix must not be treated as the mangling suffix ──

    [Fact]
    public void NormalizeTempName_does_not_collapse_names_without_hex_suffix()
    {
        // Trailing underscores with NO hex are not SQL Server's uniquifier suffix:
        // "#staging____" must stay distinct from "#staging".
        Assert.NotEqual(
            ObfuscationMap.NormalizeTempName("#staging"),
            ObfuscationMap.NormalizeTempName("#staging____"));
    }

    [Fact]
    public void NormalizeTempName_still_strips_real_mangling_suffix()
    {
        // The genuine SQL Server form (long underscore run + hex) still de-mangles to the base name.
        Assert.Equal("#staging", ObfuscationMap.NormalizeTempName("#staging_______________0000ABCD"));
    }

    // ─── Review fix #15: FromJson must not crash on null / non-object / unknown sections ──

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    public void FromJson_returns_empty_map_for_non_object_json(string json)
    {
        Assert.Empty(ObfuscationMap.FromJson(json).Entries());
    }

    [Fact]
    public void FromJson_skips_unknown_kind_sections()
    {
        var json = "{\"Table\":{\"Customers\":\"Table1\"},\"FutureKind\":{\"X\":\"Y1\"}}";
        var map = ObfuscationMap.FromJson(json);
        Assert.Equal("Table1", map.Token(NameKind.Table, "Customers"));
        Assert.Single(map.Entries()); // the unknown "FutureKind" section is ignored, not fatal
    }
}
