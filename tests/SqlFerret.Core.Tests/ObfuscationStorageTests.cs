using SqlFerret.Core.Obfuscation;
using SqlFerret.Core.Storage;

public class ObfuscationStorageTests
{
    static string NewDb() => Path.Combine(Path.GetTempPath(), $"obf_{Guid.NewGuid():N}.duckdb");

    [Fact]
    public void Save_then_load_roundtrips_and_inserts_only_new_entries()
    {
        var path = NewDb();
        try
        {
            using (var p = DuckDbProject.Open(path))
            {
                var m = new ObfuscationMap();
                m.Token(NameKind.Table, "Customers");
                m.Token(NameKind.Column, "SSN");
                p.SaveObfuscationMap(m);
                p.SaveObfuscationMap(m); // idempotent: ON CONFLICT DO NOTHING

                using var c = p.Connection.CreateCommand();
                c.CommandText = "SELECT COUNT(*) FROM obfuscation_map";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
            using (var p = DuckDbProject.Open(path))
            {
                var loaded = p.LoadObfuscationMap();
                Assert.Equal("Table1", loaded.Token(NameKind.Table, "Customers")); // reused, not re-numbered
                Assert.Equal("Table2", loaded.Token(NameKind.Table, "Orders"));    // continues numbering
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
