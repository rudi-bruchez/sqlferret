using System.Text.Json;

namespace SqlFerret.Core.Project;

/// <summary>
/// Provenance for an audit project directory, persisted as <c>project.json</c>.
/// Maintained by the tool — not meant to be hand-edited.
/// </summary>
public record ProjectManifest(
    int SchemaVersion,
    string ToolVersion,
    DateTime CreatedUtc,
    DateTime LastOpenedUtc,
    string? Notes)
{
    /// <summary>Current version of the project-directory format.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>Reads the manifest, or returns null if absent or malformed (treat as fresh).</summary>
    public static ProjectManifest? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            // Malformed manifest → caller re-initializes. Deliberate fallback path.
            return null;
        }
    }

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts));
}
