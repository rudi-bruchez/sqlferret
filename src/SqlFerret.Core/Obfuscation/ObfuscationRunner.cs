// src/SqlFerret.Core/Obfuscation/ObfuscationRunner.cs
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Obfuscation;

/// <summary>
/// One-call obfuscation orchestration shared by every host (CLI now, TUI later):
/// owns the file and map I/O so hosts stay thin. Mirrors <see cref="Ingestion.ImportRunner"/>.
/// </summary>
public readonly record struct ObfuscationResult(string AnonPath, string MapPath, int NamesMapped);

public readonly record struct FolderObfuscationResult(
    int FilesFound,
    int FilesProcessed,
    int FilesFailed,
    int NamesMapped,
    string MapPath,
    IReadOnlyList<string> Failures);

public static class ObfuscationRunner
{
    public static ObfuscationResult RunStandalone(string inPath, string outPath)
    {
        if (!File.Exists(inPath)) throw new FileNotFoundException("input plan not found", inPath);
        var (anon, map) = PlanObfuscator.Obfuscate(File.ReadAllText(inPath), new ObfuscationMap());
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir); // review fix #7
        File.WriteAllText(outPath, anon);
        var mapPath = MapJsonPath(outPath);
        File.WriteAllText(mapPath, map.ToJson());
        return new ObfuscationResult(outPath, mapPath, map.Entries().Count());
    }

    public static ObfuscationResult RunProject(DuckDbProject db, string plansFolder, string planId)
    {
        // planId must be a bare file-name component — reject path traversal in Core, not just at the
        // CLI boundary, since Core is host-agnostic (review fix #6; mirrors EstimatedPlanService.Save).
        if (string.IsNullOrEmpty(planId)
            || planId.Contains('/') || planId.Contains('\\') || planId.Contains("..")
            || Path.GetFileName(planId) != planId)
            throw new ArgumentException("Invalid planId: must be a bare file name component", nameof(planId));

        var src = Path.Combine(plansFolder, $"{planId}.sqlplan");
        if (!File.Exists(src)) throw new FileNotFoundException("plan not found", src);

        var map = db.LoadObfuscationMap();
        var (anon, enriched) = PlanObfuscator.Obfuscate(File.ReadAllText(src), map);
        Directory.CreateDirectory(plansFolder);
        var anonPath = Path.Combine(plansFolder, $"{planId}.anon.sqlplan");
        var mapPath = Path.Combine(plansFolder, $"{planId}.map.json");
        File.WriteAllText(anonPath, anon);
        File.WriteAllText(mapPath, enriched.ToJson());
        db.SaveObfuscationMap(enriched);
        return new ObfuscationResult(anonPath, mapPath, enriched.Entries().Count());
    }

    public static FolderObfuscationResult RunFolder(string inDir, string outDir, string? mapPath = null)
    {
        if (!Directory.Exists(inDir)) throw new DirectoryNotFoundException($"input folder not found: {inDir}");

        var files = Directory.EnumerateFiles(inDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var filesFound = files.Count;
        var map = new ObfuscationMap();
        var filesProcessed = 0;
        var filesFailed = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(inDir, file);
            try
            {
                var text = File.ReadAllText(file);
                var (anon, _) = PlanObfuscator.Obfuscate(text, map);
                // Note: on a case-sensitive filesystem two inputs differing only in extension case
                // (e.g. "a.sqlplan" vs "a.SQLPlan") produce the same output path — last write wins.
                // Accepted edge case; `EnumerateFiles` on Linux can surface both.
                var outFile = Path.Combine(outDir, Path.ChangeExtension(rel, null) + ".anon.sqlplan");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                File.WriteAllText(outFile, anon);
                filesProcessed++;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                failures.Add($"{rel}: {ex.Message}");
                filesFailed++;
            }
        }

        var resolvedMapPath = mapPath ?? DefaultFolderMapPath(outDir);
        var mapDir = Path.GetDirectoryName(resolvedMapPath);
        // GetDirectoryName returns "" (not null) for a bare filename; CreateDirectory("") throws
        // (review fix #12). Only create a directory when there actually is one.
        if (!string.IsNullOrEmpty(mapDir)) Directory.CreateDirectory(mapDir);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(resolvedMapPath, map.ToJson());

        return new FolderObfuscationResult(filesFound, filesProcessed, filesFailed, map.Entries().Count(), resolvedMapPath, failures);
    }

    /// <summary>
    /// Returns the default map path for a folder obfuscation run: a sibling of <paramref name="outDir"/>
    /// named <c>&lt;outDirName&gt;.map.json</c>, so the de-anonymization key is OUTSIDE the shareable
    /// output folder.
    /// </summary>
    public static string DefaultFolderMapPath(string outDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        var parent = Path.GetDirectoryName(full);          // may be null only for a root path
        var name = Path.GetFileName(full);
        var mapName = (string.IsNullOrEmpty(name) ? "folder" : name) + ".map.json";
        return parent is null ? Path.Combine(full, mapName) : Path.Combine(parent, mapName);
    }

    internal static string MapJsonPath(string outPath) =>
        outPath.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase)
            ? outPath[..^".sqlplan".Length] + ".map.json"
            : outPath + ".map.json";
}
