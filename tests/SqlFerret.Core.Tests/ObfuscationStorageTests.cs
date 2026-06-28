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

    // ─── Review fix #5: multi-entry save is one atomic transaction (all-or-nothing) ──
    // Guards against a mis-scoped transaction (e.g. a missing Commit) regressing the save.
    [Fact]
    public void Save_persists_all_entries_atomically_across_kinds()
    {
        var path = NewDb();
        try
        {
            var m = new ObfuscationMap();
            for (int i = 0; i < 40; i++)
            {
                m.Token(NameKind.Table, $"T{i}");
                m.Token(NameKind.Column, $"C{i}");
            }
            int expected = m.Entries().Count();

            using (var p = DuckDbProject.Open(path))
            {
                p.SaveObfuscationMap(m);
                using var c = p.Connection.CreateCommand();
                c.CommandText = "SELECT COUNT(*) FROM obfuscation_map";
                Assert.Equal((long)expected, Convert.ToInt64(c.ExecuteScalar()));
            }
            using (var p = DuckDbProject.Open(path))
                Assert.Equal(expected, p.LoadObfuscationMap().Entries().Count());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ─── Review fix #11: an unknown 'kind' row must not brick the project on load ──
    [Fact]
    public void Load_skips_unknown_kind_rows_instead_of_throwing()
    {
        var path = NewDb();
        try
        {
            using (var p = DuckDbProject.Open(path))
            {
                var m = new ObfuscationMap();
                m.Token(NameKind.Table, "Customers");
                p.SaveObfuscationMap(m);
                // Simulate a legacy/future schema row whose kind is not in the current enum.
                using var c = p.Connection.CreateCommand();
                c.CommandText = "INSERT INTO obfuscation_map VALUES ('futurekind','X','Z1')";
                c.ExecuteNonQuery();
            }
            using (var p = DuckDbProject.Open(path))
            {
                var loaded = p.LoadObfuscationMap();           // must not throw
                Assert.Equal("Table1", loaded.Token(NameKind.Table, "Customers"));
                Assert.Single(loaded.Entries());               // the unknown row is skipped
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
