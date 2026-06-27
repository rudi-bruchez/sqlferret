// src/SqlFerret.Core/Obfuscation/ObfuscationRunner.cs
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Obfuscation;

/// <summary>
/// One-call obfuscation orchestration shared by every host (CLI now, TUI later):
/// owns the file and map I/O so hosts stay thin. Mirrors <see cref="Ingestion.ImportRunner"/>.
/// </summary>
public readonly record struct ObfuscationResult(string AnonPath, string MapPath, int NamesMapped);

public static class ObfuscationRunner
{
    public static ObfuscationResult RunStandalone(string inPath, string outPath)
    {
        if (!File.Exists(inPath)) throw new FileNotFoundException("input plan not found", inPath);
        var (anon, map) = PlanObfuscator.Obfuscate(File.ReadAllText(inPath), new ObfuscationMap());
        File.WriteAllText(outPath, anon);
        var mapPath = MapJsonPath(outPath);
        File.WriteAllText(mapPath, map.ToJson());
        return new ObfuscationResult(outPath, mapPath, map.Entries().Count());
    }

    public static ObfuscationResult RunProject(DuckDbProject db, string plansFolder, string planId)
    {
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

    internal static string MapJsonPath(string outPath) =>
        outPath.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase)
            ? outPath[..^".sqlplan".Length] + ".map.json"
            : outPath + ".map.json";
}
