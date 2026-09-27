// src/SqlFerret.Core/Plans/PlanFindings.cs
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace SqlFerret.Core.Plans;

/// <summary>Règles de détection d'anomalies dans un plan. Pur, sans I/O.</summary>
public static class PlanFindings
{
    private static XName N(string n) => XName.Get(n, PlanIdentity.Ns);

    public static IReadOnlyList<PlanFinding> Detect(XDocument plan, PlanFindingThresholds t)
    {
        var found = new List<PlanFinding>();
        DetectGrant(plan, t, found);
        DetectMissingIndexes(plan, found);
        foreach (var op in plan.Descendants(N("RelOp")))
        {
            DetectLargeScan(op, t, found);
            DetectSpill(op, found);
            DetectWarnings(op, found);
            DetectRowGoal(op, t, found);
            DetectCardinality(op, t, found);
            DetectRebinds(op, t, found);
        }
        return found;
    }

    private static void DetectGrant(XDocument plan, PlanFindingThresholds t, List<PlanFinding> found)
    {
        var g = plan.Descendants(N("MemoryGrantInfo")).FirstOrDefault();
        if (g is null) return;

        long? desired = L(g, "SerialDesiredMemory"), granted = L(g, "GrantedMemory"), used = L(g, "MaxUsedMemory");

        // Le ratio porte sur accordé / utilisé : deux grandeurs de même nature.
        // Il n'est signalé qu'au-delà d'un plancher : un grant d'un mégaoctet
        // surdimensionné mille fois est exact et sans conséquence, et publier ces
        // cas-là noie les rares qui pèsent vraiment.
        bool ratioHit = granted >= t.GrantRatioFloorKb && used > 0
                        && (double)granted.Value / used.Value > t.GrantOversizeRatio;
        // Le seuil absolu porte sur la demande : une demande démesurée écrêtée par le
        // plafond du moteur reste un défaut, et le ratio ne la verrait pas. Le plancher
        // ne s'y applique pas — l'accordé peut être modeste précisément parce que le
        // moteur a écrêté.
        bool absoluteHit = desired is not null && desired > t.GrantAbsoluteKb;

        if (!ratioHit && !absoluteHit) return;

        found.Add(new PlanFinding("memory_grant_oversized", null, Json(new
        {
            serial_desired_kb = desired,
            granted_kb = granted,
            max_used_kb = used,
            ratio = granted > 0 && used > 0 ? Math.Round((double)granted.Value / used.Value, 2) : (double?)null,
        })));
    }

    private static void DetectLargeScan(XElement op, PlanFindingThresholds t, List<PlanFinding> found)
    {
        var physical = (string?)op.Attribute("PhysicalOp") ?? "";
        if (!physical.Contains("Scan", StringComparison.Ordinal)) return;

        long? card = L(op, "TableCardinality");
        if (card is null || card <= t.LargeScanRows) return;

        var obj = op.Descendants(N("Object")).FirstOrDefault();
        found.Add(new PlanFinding("large_scan", I(op, "NodeId"), Json(new
        {
            op = physical,
            table = Trim((string?)obj?.Attribute("Table")),
            index = Trim((string?)obj?.Attribute("Index")),
            table_cardinality = card,
        })));
    }

    private static void DetectSpill(XElement op, List<PlanFinding> found)
    {
        // Le schéma showplan ne place SortSpillDetails et HashSpillDetails que dans
        // WarningsType, et le moteur les écrit sous RelOp/Warnings : mesuré sur 158 plans
        // réels et sur un tri et un hachage forcés en débordement (SQL Server 2025).
        var w = op.Element(N("Warnings"));
        if (w is null) return;
        foreach (var name in (string[])["SortSpillDetails", "HashSpillDetails"])
        {
            var s = w.Element(N(name));
            if (s is null) continue;
            found.Add(new PlanFinding("spill_to_tempdb", I(op, "NodeId"), Json(new
            {
                kind = name,
                granted_kb = L(s, "GrantedMemoryKb"),
                used_kb = L(s, "UsedMemoryKb"),
                writes_to_tempdb = L(s, "WritesToTempDb"),
            })));
        }
    }

    private static void DetectWarnings(XElement op, List<PlanFinding> found)
    {
        var w = op.Element(N("Warnings"));
        if (w is null) return;
        foreach (var child in w.Elements())
            found.Add(new PlanFinding("plan_warning", I(op, "NodeId"), Json(new
            {
                warning = child.Name.LocalName,
                detail = string.Join(", ", child.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}")),
            })));
    }

    private static void DetectMissingIndexes(XDocument plan, List<PlanFinding> found)
    {
        foreach (var grp in plan.Descendants(N("MissingIndexGroup")))
        {
            var mi = grp.Descendants(N("MissingIndex")).FirstOrDefault();
            found.Add(new PlanFinding("missing_index", null, Json(new
            {
                impact = (string?)grp.Attribute("Impact"),
                table = Trim((string?)mi?.Attribute("Table")),
                columns = mi?.Descendants(N("Column")).Select(c => Trim((string?)c.Attribute("Name"))).ToArray() ?? [],
            })));
        }
    }

    /// <summary>
    /// Opérateurs bloquants : ils doivent consommer toute leur entrée avant de rendre une
    /// ligne, donc un TOP placé au-dessus ne peut pas leur épargner ce travail — et le grant
    /// mémoire reste dimensionné sur la cardinalité complète.
    /// </summary>
    private static readonly string[] BlockingOps =
        ["Sort", "Hash Match", "Table Spool", "Index Spool", "Window Spool", "Sort (Top N Sort)"];

    private static void DetectRowGoal(XElement op, PlanFindingThresholds t, List<PlanFinding> found)
    {
        var physical = (string?)op.Attribute("PhysicalOp") ?? "";
        if (!BlockingOps.Any(b => physical.StartsWith(b, StringComparison.Ordinal))) return;

        double? rows = D(op, "EstimateRows"), without = D(op, "EstimateRowsWithoutRowGoal");
        if (rows is null or <= 0 || without is null) return;
        if (without.Value / rows.Value <= t.RowGoalRatio) return;

        found.Add(new PlanFinding("row_goal_defeated", I(op, "NodeId"), Json(new
        {
            op = physical,
            rows,
            rows_without_row_goal = without,
            ratio = Math.Round(without.Value / rows.Value, 2),
        })));
    }

    private static void DetectCardinality(XElement op, PlanFindingThresholds t, List<PlanFinding> found)
    {
        double? estimate = D(op, "EstimateRows");
        long? actual = SumActualRows(op);
        if (estimate is null or <= 0 || actual is null) return;

        double ratio = actual.Value >= estimate.Value
            ? actual.Value / estimate.Value
            : estimate.Value / Math.Max(actual.Value, 1);
        if (ratio <= t.CardinalityRatio) return;

        found.Add(new PlanFinding("cardinality_misestimate", I(op, "NodeId"), Json(new
        {
            op = (string?)op.Attribute("PhysicalOp"),
            estimate_rows = estimate,
            actual_rows = actual,
            ratio = Math.Round(ratio, 2),
        })));
    }

    private static void DetectRebinds(XElement op, PlanFindingThresholds t, List<PlanFinding> found)
    {
        double? rebinds = D(op, "EstimateRebinds");
        if (rebinds is null || rebinds <= t.RebindsThreshold) return;

        found.Add(new PlanFinding("excessive_rebinds", I(op, "NodeId"), Json(new
        {
            op = (string?)op.Attribute("PhysicalOp"),
            estimate_rebinds = rebinds,
        })));
    }

    /// <summary>
    /// Somme ActualRows sur TOUS les threads de l'opérateur. Un plan parallèle rapporte un
    /// nœud RunTimeCountersPerThread par thread : comparer l'estimation au compteur d'un
    /// seul thread signalerait une fausse sous-estimation d'un facteur DOP.
    /// Renvoie null quand le plan ne porte aucun compteur d'exécution (plan estimé).
    /// </summary>
    public static long? SumActualRows(XElement relOp)
    {
        var rti = relOp.Element(N("RunTimeInformation"));
        if (rti is null) return null;
        long sum = 0; bool any = false;
        foreach (var th in rti.Elements(N("RunTimeCountersPerThread")))
        {
            var v = L(th, "ActualRows");
            if (v is null) continue;
            sum += v.Value; any = true;
        }
        return any ? sum : null;
    }

    internal static string Json(object o) => JsonSerializer.Serialize(o);

    internal static string? Trim(string? s) => s?.Trim('[', ']');

    internal static long? L(XElement? e, string n) =>
        long.TryParse((string?)e?.Attribute(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    internal static double? D(XElement? e, string n) =>
        double.TryParse((string?)e?.Attribute(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    internal static int? I(XElement? e, string n) =>
        int.TryParse((string?)e?.Attribute(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
