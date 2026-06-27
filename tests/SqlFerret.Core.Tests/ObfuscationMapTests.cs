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
}
