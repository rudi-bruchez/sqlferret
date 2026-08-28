// src/SqlFerret.Core/Plans/PlanProfileParser.cs
using System.Globalization;
using System.Xml.Linq;
using SqlFerret.Core.Ingestion;

namespace SqlFerret.Core.Plans;

/// <summary>
/// showplan XML → PlanProfile. Pur : aucune I/O, aucune dépendance hors XDocument.
/// CachedPlanSize et CompileTime ne sont volontairement pas lus (aucune règle ni champ du
/// digest ne les utilise, et CompileTime est en millisecondes alors que Core est en µs).
/// </summary>
public static class PlanProfileParser
{
    private static readonly XName StmtSimple = XName.Get("StmtSimple", PlanIdentity.Ns);
    private static readonly XName QueryPlan = XName.Get("QueryPlan", PlanIdentity.Ns);
    private static readonly XName MemoryGrantInfo = XName.Get("MemoryGrantInfo", PlanIdentity.Ns);

    public static PlanProfile? TryParse(string showplanXml, IXeEventData ev, PlanFindingThresholds thresholds)
    {
        XDocument doc;
        try { doc = XDocument.Parse(showplanXml); }
        catch (System.Xml.XmlException) { return null; }

        var (hash, source, statementCount) = PlanIdentity.Compute(doc);

        var stmt = doc.Descendants(StmtSimple).FirstOrDefault();
        var plan = doc.Descendants(QueryPlan).FirstOrDefault();
        var grant = doc.Descendants(MemoryGrantInfo).FirstOrDefault();
        var text = (string?)stmt?.Attribute("StatementText");

        return new PlanProfile
        {
            PlanHash = hash,
            PlanHashSource = source,
            FileStem = PlanIdentity.FileStem(hash, source),
            StatementCount = statementCount,
            QueryHash = Hex(stmt, "QueryHash"),
            StatementType = (string?)stmt?.Attribute("StatementType"),
            StatementText = text,
            StatementTextLength = text?.Length ?? 0,
            CapturedAt = Utc(ev.Timestamp),
            DurationUs = Long(ev, "duration"),
            CpuTimeUs = Long(ev, "cpu_time"),
            EstimatedRows = Double(stmt, "StatementEstRows"),
            SubtreeCost = Double(stmt, "StatementSubTreeCost"),
            Dop = Int(plan, "DegreeOfParallelism"),
            SerialDesiredMemoryKb = LongAttr(grant, "SerialDesiredMemory"),
            GrantedMemoryKb = LongAttr(grant, "GrantedMemory"),
            MaxUsedMemoryKb = LongAttr(grant, "MaxUsedMemory"),
            Findings = PlanFindings.Detect(doc, thresholds),
        };
    }

    /// <summary>
    /// Tout le contrat de corrélation manuelle repose sur un horodatage réellement UTC.
    /// Un DateTime de Kind Local doit être CONVERTI, pas réétiqueté : réétiqueter décalerait
    /// silencieusement d'un fuseau entier — exactement le mode de défaillance que la spec
    /// documente pour SSMS.
    /// </summary>
    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),   // Unspecified : XELite émet de l'UTC
    };

    private static string? Hex(XElement? e, string name)
    {
        var v = (string?)e?.Attribute(name);
        if (string.IsNullOrEmpty(v)) return null;
        return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v[2..] : v;
    }

    private static long? Long(IXeEventData ev, string field) =>
        ev.Fields.TryGetValue(field, out var v) && v is not null ? Convert.ToInt64(v) : null;

    private static long? LongAttr(XElement? e, string name) =>
        long.TryParse((string?)e?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static int? Int(XElement? e, string name) =>
        int.TryParse((string?)e?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static double? Double(XElement? e, string name) =>
        double.TryParse((string?)e?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
