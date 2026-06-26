using SqlFerret.Core.Storage;

public class EventExportSchemaTests
{
    [Fact]
    public void Blocking_reports_has_raw_xml_column_and_reopen_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT raw_xml FROM blocking_reports LIMIT 0";
                using var r = c.ExecuteReader();   // throws if the column is missing
                Assert.Equal("raw_xml", r.GetName(0));
            }
            // Re-open: the ADD COLUMN IF NOT EXISTS migration must not throw on an existing DB.
            using (DuckDbProject.Open(path)) { }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
