// src/SqlFerret.Core/Ingestion/ServerDiagnosticsParser.cs
using System.Globalization;
using System.Xml.Linq;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Lit le payload XML de <c>sp_server_diagnostics_component_result</c>. Ne lance jamais : une
/// entree inutilisable rend un echantillon dont l'issue le dit, et l'appelant incremente le
/// compteur correspondant.
/// </summary>
public static class ServerDiagnosticsParser
{
    /// <summary>Composant -> nom de racine attendu. Une racine qui ne correspond pas est un
    /// echec : mieux vaut le compter que de ranger des attributs sous le mauvais composant.</summary>
    private static readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QUERY_PROCESSING"] = "queryProcessing",
        ["RESOURCE"] = "resource",
        ["SYSTEM"] = "system",
        ["IO_SUBSYSTEM"] = "ioSubsystem",
    };

    /// <summary>
    /// Scalaires exprimes en millisecondes par la source. Convertis en microsecondes et renommes
    /// avec le suffixe <c>Us</c> : une ligne cle/valeur ne peut pas porter un nom de colonne, donc
    /// le mecanisme habituel de l'invariant — le suffixe <c>_us</c> — ne les atteint pas, et
    /// <c>sqlferret query</c>, qui formate d'apres le nom de colonne, ne peut pas aider le lecteur.
    /// Liste explicite plutot qu'heuristique sur le nom : y ajouter une entree doit etre une
    /// modification visible.
    /// </summary>
    private static readonly HashSet<string> MillisecondScalars =
        new(StringComparer.Ordinal) { "oldestPendingTaskWaitingTime", "processOutOfMemoryPeriod" };

    public static ServerDiagnosticsSample TryParse(
        string? component, string? state, string? xml, DateTime capturedAt)
    {
        var comp = component ?? "";
        if (!Roots.TryGetValue(comp, out var expectedRoot))
            return Empty(capturedAt, comp, state, DiagnosticsOutcome.Unhandled);

        if (string.IsNullOrWhiteSpace(xml))
            return Empty(capturedAt, comp, state, DiagnosticsOutcome.Failed);

        XElement root;
        // Chemin de repli delibere : un XML malforme est compte, pas propage.
        try { root = XElement.Parse(xml); }
        catch { return Empty(capturedAt, comp, state, DiagnosticsOutcome.Failed); }

        if (!string.Equals(root.Name.LocalName, expectedRoot, StringComparison.Ordinal))
            return Empty(capturedAt, comp, state, DiagnosticsOutcome.Failed);

        var metrics = new List<HealthMetric>();
        foreach (var a in root.Attributes())
        {
            var name = a.Name.LocalName;
            if (MillisecondScalars.Contains(name) && TryLong(a.Value, out var ms))
            {
                metrics.Add(new HealthMetric(name + "Us", null, ms * 1000L, null));
                continue;
            }
            if (TryLong(a.Value, out var big)) metrics.Add(new HealthMetric(name, null, big, null));
            else if (TryDouble(a.Value, out var d)) metrics.Add(new HealthMetric(name, d, null, null));
            else metrics.Add(new HealthMetric(name, null, null, a.Value));
        }

        var waits = new List<HealthWait>();
        foreach (var (kind, preemptive) in new[] { ("nonPreemptive", false), ("preemptive", true) })
            foreach (var ranking in new[] { "byCount", "byDuration" })
                foreach (var w in root.Descendants(kind).Descendants(ranking).Descendants("wait"))
                    waits.Add(new HealthWait(
                        preemptive, ranking, (string?)w.Attribute("waitType") ?? "",
                        Long(w, "waits") ?? 0,
                        (Long(w, "averageWaitTime") ?? 0) * 1000L,
                        (Long(w, "maxWaitTime") ?? 0) * 1000L));

        var cpu = root.Descendants("cpuIntensiveRequests").Descendants("request")
            .Select(r => new HealthCpuRequest(
                Int(r, "sessionId"), Int(r, "requestId"), (string?)r.Attribute("command"),
                Long(r, "cpuTimeMs") is { } cpuMs ? cpuMs * 1000L : null,
                Dbl(r, "cpuUtilization"), (string?)r.Attribute("taskAddress")))
            .ToList();

        var pending = root.Descendants("pendingTasks").Descendants("entryPoint")
            .Select(e => new HealthPendingTask(
                (string?)e.Attribute("name") ?? "", Long(e, "count") ?? 0))
            .ToList();

        var io = root.Descendants("longestPendingRequests").Descendants("pendingRequest")
            .Select(r => new HealthPendingIo(
                Long(r, "duration") is { } ms2 ? ms2 * 1000L : null,
                (string?)r.Attribute("filePath"), (string?)r.Attribute("handle"),
                Long(r, "offset")))
            .ToList();

        var mem = new List<HealthMemoryEntry>();
        foreach (var report in root.Descendants("memoryReport"))
        {
            var reportName = (string?)report.Attribute("name") ?? "";
            var unit = (string?)report.Attribute("unit");
            foreach (var e in report.Elements("entry"))
            {
                var raw = (string?)e.Attribute("value");
                var numeric = raw is not null && TryDouble(raw, out var v);
                mem.Add(new HealthMemoryEntry(
                    reportName, unit, (string?)e.Attribute("description") ?? "",
                    numeric ? double.Parse(raw!, NumberStyles.Float, CultureInfo.InvariantCulture) : null,
                    numeric ? null : raw));
            }
        }

        var embedded = root.Descendants("blockingTasks").Elements("blocked-process-report")
            .Select(x => x.ToString()).ToList();

        return new ServerDiagnosticsSample(capturedAt, comp, state, DiagnosticsOutcome.Parsed,
            metrics, waits, cpu, pending, io, mem, embedded);
    }

    private static ServerDiagnosticsSample Empty(
        DateTime ts, string comp, string? state, DiagnosticsOutcome outcome) =>
        new(ts, comp, state, outcome, [], [], [], [], [], [], []);

    private static bool TryLong(string s, out long v) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static bool TryDouble(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static long? Long(XElement e, string a) =>
        (string?)e.Attribute(a) is { } s && TryLong(s, out var v) ? v : null;

    private static int? Int(XElement e, string a) => (int?)Long(e, a);

    private static double? Dbl(XElement e, string a) =>
        (string?)e.Attribute(a) is { } s && TryDouble(s, out var v) ? v : null;
}
