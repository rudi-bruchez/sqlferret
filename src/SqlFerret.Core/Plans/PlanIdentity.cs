// src/SqlFerret.Core/Plans/PlanIdentity.cs
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SqlFerret.Core.Plans;

/// <summary>
/// Calcule l'identité stable d'un plan : le QueryPlanHash quand il y en a exactement un,
/// un hash composite pour un batch multi-instructions, un hash de contenu en dernier recours.
/// </summary>
public static class PlanIdentity
{
    public const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly XName StmtSimple = XName.Get("StmtSimple", Ns);
    private static readonly XName RunTimeInformation = XName.Get("RunTimeInformation", Ns);
    private static readonly XName QueryTimeStats = XName.Get("QueryTimeStats", Ns);
    private static readonly XName MemoryGrantInfo = XName.Get("MemoryGrantInfo", Ns);

    public static (string Hash, string Source, int StatementCount) Compute(XDocument plan)
    {
        var hashes = plan.Descendants(StmtSimple)
            .Select(s => (string?)s.Attribute("QueryPlanHash"))
            .Where(h => !string.IsNullOrEmpty(h))
            .Select(h => Strip0x(h!))
            .ToList();

        int statementCount = plan.Descendants(StmtSimple).Count();

        if (hashes.Count == 1) return (hashes[0], "queryplanhash", statementCount);
        if (hashes.Count > 1) return (Sha16(string.Join('|', hashes)), "multi", statementCount);

        var normalized = NormalizeForHashing(plan);
        return (Sha16(normalized.ToString(SaveOptions.DisableFormatting)), "content", statementCount);
    }

    /// <summary>
    /// Radical de nom de fichier. Le préfixe vit ICI et nulle part ailleurs : `plan_hash`
    /// reste nu en base pour rester joignable (la V2 corrèle sur des hashes bruts), et la
    /// colonne `file_stem` porte ce radical afin que le SQL de la passe finale n'ait pas à
    /// dupliquer la règle de préfixage.
    /// </summary>
    public static string FileStem(string planHash, string source) => source switch
    {
        "multi" => "m_" + planHash,
        "content" => "c_" + planHash,
        _ => "p_" + planHash,
    };

    /// <summary>
    /// Retire du plan tout ce qui varie d'une exécution à l'autre, pour qu'un hash de contenu
    /// reste stable. Les plans réels embarquent des compteurs par thread : les laisser
    /// produirait un hash différent à chaque exécution et détruirait le dédoublonnage.
    /// SerialRequiredMemory / SerialDesiredMemory sont conservés : propriétés de compilation.
    /// </summary>
    internal static XDocument NormalizeForHashing(XDocument plan)
    {
        var copy = new XDocument(plan);
        copy.Descendants(RunTimeInformation).Remove();
        copy.Descendants(QueryTimeStats).Remove();
        foreach (var mgi in copy.Descendants(MemoryGrantInfo))
        {
            mgi.Attribute("GrantedMemory")?.Remove();
            mgi.Attribute("MaxUsedMemory")?.Remove();
            mgi.Attribute("GrantWaitTime")?.Remove();
        }
        return copy;
    }

    private static string Strip0x(string h) =>
        h.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? h[2..] : h;

    private static string Sha16(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
}
