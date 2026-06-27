// src/SqlFerret.Core/Obfuscation/ObfuscationMap.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFerret.Core.Obfuscation;

public enum NameKind { Database, Schema, Table, Column, Index, Statistics, Parameter, Alias }

public sealed class ObfuscationMap
{
    private static readonly Dictionary<NameKind, string> Prefixes = new()
    {
        [NameKind.Database] = "Db",
        [NameKind.Schema] = "Schema",
        [NameKind.Table] = "Table",
        [NameKind.Column] = "Col",
        [NameKind.Index] = "Idx",
        [NameKind.Statistics] = "Stat",
        [NameKind.Parameter] = "Param",
        [NameKind.Alias] = "Alias",
    };

    // Precedence for the flat text lookup when one name exists under several kinds.
    private static readonly NameKind[] TextPrecedence =
        [NameKind.Table, NameKind.Alias, NameKind.Column, NameKind.Index,
         NameKind.Statistics, NameKind.Schema, NameKind.Database, NameKind.Parameter];

    // kind -> (lowercased stripped key -> (original stripped, token))
    private readonly Dictionary<NameKind, Dictionary<string, (string Original, string Token)>> _maps = new();
    private readonly Dictionary<NameKind, int> _counters = new();

    public static string Strip(string name) => name.Trim().Trim('[', ']');

    public string Token(NameKind kind, string originalName)
    {
        var stripped = Strip(originalName);
        var key = stripped.ToLowerInvariant();
        var m = _maps.TryGetValue(kind, out var existing) ? existing : _maps[kind] = new();
        if (m.TryGetValue(key, out var hit)) return hit.Token;
        var n = (_counters.TryGetValue(kind, out var c) ? c : 0) + 1;
        _counters[kind] = n;
        var token = Prefixes[kind] + n;
        m[key] = (stripped, token);
        return token;
    }

    public IReadOnlyDictionary<string, string> BuildTextLookup()
    {
        var lookup = new Dictionary<string, string>();
        foreach (var kind in TextPrecedence)
            if (_maps.TryGetValue(kind, out var m))
                foreach (var (key, val) in m)
                    lookup.TryAdd(key, val.Token); // first (higher-precedence) kind wins
        return lookup;
    }

    public IEnumerable<(NameKind Kind, string Original, string Token)> Entries()
    {
        foreach (var (kind, m) in _maps)
            foreach (var (_, val) in m)
                yield return (kind, val.Original, val.Token);
    }

    public string ToJson()
    {
        var root = new JsonObject();
        foreach (var (kind, m) in _maps)
        {
            var section = new JsonObject();
            foreach (var (_, val) in m)
                section[val.Original] = val.Token;
            root[kind.ToString()] = section;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static ObfuscationMap FromJson(string json)
    {
        var map = new ObfuscationMap();
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var (kindName, section) in root)
        {
            var kind = Enum.Parse<NameKind>(kindName, ignoreCase: true);
            foreach (var (original, tokenNode) in section!.AsObject())
                map.Seed(kind, original, tokenNode!.GetValue<string>());
        }
        return map;
    }

    public static ObfuscationMap FromEntries(IEnumerable<(NameKind Kind, string Original, string Token)> entries)
    {
        var map = new ObfuscationMap();
        foreach (var (kind, original, token) in entries)
            map.Seed(kind, original, token);
        return map;
    }

    // Insert a known pair and keep the per-kind counter ahead of any numeric suffix seen.
    private void Seed(NameKind kind, string original, string token)
    {
        var stripped = Strip(original);
        var m = _maps.TryGetValue(kind, out var existing) ? existing : _maps[kind] = new();
        m[stripped.ToLowerInvariant()] = (stripped, token);
        var prefix = Prefixes[kind];
        if (token.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(token.AsSpan(prefix.Length), out var n))
            _counters[kind] = Math.Max(_counters.TryGetValue(kind, out var c) ? c : 0, n);
    }
}
