using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFerret.Core.Filtering;

namespace SqlFerret.Core.Config;

public class UiState
{
    public record ViewLayout(string[] Columns, string Sort);

    public List<FilterRule> Filters { get; set; } = new();
    public Dictionary<string, ViewLayout> Views { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static UiState Load(string path)
    {
        if (!File.Exists(path)) return new UiState();
        try { return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Opts) ?? new UiState(); }
        catch { return new UiState(); }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
}
