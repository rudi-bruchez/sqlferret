// src/SqlFerret.Core/Storage/DuckDbProject.Obfuscation.cs
using DuckDB.NET.Data;
using SqlFerret.Core.Obfuscation;

namespace SqlFerret.Core.Storage;

public sealed partial class DuckDbProject
{
    internal static void CreateObfuscationSchema(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS obfuscation_map (
          kind          VARCHAR NOT NULL,
          original_name VARCHAR NOT NULL,
          token         VARCHAR NOT NULL,
          PRIMARY KEY (kind, original_name)
        );
        """;
        cmd.ExecuteNonQuery();
    }

    public ObfuscationMap LoadObfuscationMap()
    {
        var entries = new List<(NameKind, string, string)>();
        using var c = Connection.CreateCommand();
        c.CommandText = "SELECT kind, original_name, token FROM obfuscation_map";
        using var r = c.ExecuteReader();
        while (r.Read())
            entries.Add((Enum.Parse<NameKind>(r.GetString(0), ignoreCase: true), r.GetString(1), r.GetString(2)));
        return ObfuscationMap.FromEntries(entries);
    }

    public void SaveObfuscationMap(ObfuscationMap map)
    {
        foreach (var (kind, original, token) in map.Entries())
        {
            using var c = Connection.CreateCommand();
            c.CommandText = "INSERT INTO obfuscation_map(kind, original_name, token) VALUES ($k,$o,$t) ON CONFLICT DO NOTHING";
            Add(c, "$k", kind.ToString().ToLowerInvariant());
            Add(c, "$o", original);
            Add(c, "$t", token);
            c.ExecuteNonQuery();
        }
    }
}
