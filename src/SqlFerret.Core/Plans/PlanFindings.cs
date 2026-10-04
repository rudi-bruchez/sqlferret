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
        // Avertissements de niveau instruction : MemoryGrantWarning, PlanAffectingConvert,
        // Wait et UnmatchedIndexes sont écrits sous QueryPlan/Warnings, pas sous un opérateur.
        foreach (var qp in plan.Descendants(N("QueryPlan")))
        {
            DetectWarnings(qp, null, found);
            DetectNonParallelPlan(qp, t, found);
            DetectTraceFlags(qp, found);
        }
        DetectEarlyAbort(plan, found);
        foreach (var op in plan.Descendants(N("RelOp")))
        {
            DetectLargeScan(op, t, found);
            DetectSpill(op, found);
            DetectWarnings(op, I(op, "NodeId"), found);
            DetectRowGoal(op, t, found);
            DetectCardinality(op, t, found);
            DetectRebinds(op, t, found);
            DetectThreadSkew(op, t, found);
        }
        return found;
    }

    /// <summary>
    /// StatementOptmEarlyAbortReason porte sur tout type d'instruction (BaseStmtInfoType).
    /// Le schéma énumère TimeOut, MemoryLimitExceeded et GoodEnoughPlanFound ; la troisième
    /// est l'issue normale d'une optimisation et n'est pas signalée.
    /// </summary>
    private static void DetectEarlyAbort(XDocument plan, List<PlanFinding> found)
    {
        foreach (var stmt in plan.Descendants())
        {
            var reason = (string?)stmt.Attribute("StatementOptmEarlyAbortReason");
            if (reason is not ("TimeOut" or "MemoryLimitExceeded")) continue;
            found.Add(new PlanFinding("optimizer_early_abort", null, Json(new
            {
                reason,
                statement_subtree_cost = D(stmt, "StatementSubTreeCost"),
            })));
        }
    }

    /// <summary>
    /// Les valeurs de NonParallelPlanReason qui désignent un choix fait dans le code de la
    /// requête. Le schéma type l'attribut en chaîne libre ; la liste vient du guide
    /// d'architecture du traitement des requêtes (Microsoft Learn). Les raisons de
    /// configuration, d'édition ou de moteur (MaxDOPSetToOne, EstimatedDOPIsOne,
    /// CouldNotGenerateValidParallelPlan…) n'en sont pas : le développeur n'y peut rien.
    /// </summary>
    private static readonly string[] CodeNonParallelReasons =
    [
        "TSQLUserDefinedFunctionsNotParallelizable",
        "TableVariableTransactionsDoNotSupportParallelNestedTransaction",
        "CLRUserDefinedFunctionRequiresDataAccess",
        "NonParallelizableIntrinsicFunction",
        "DMLQueryReturnsOutputToClient",
        "NoParallelWithRemoteQuery",
        "NoParallelDynamicCursor",
        "NoParallelFastForwardCursor",
        "NoParallelCursorFetchByBookmark",
    ];

    /// <summary>
    /// Le moteur n'envisage un plan parallèle qu'au-delà du coût seuil de parallélisme :
    /// en dessous, la raison est exacte et sans conséquence. Le coût lu est celui de
    /// l'instruction qui porte ce QueryPlan.
    /// </summary>
    private static void DetectNonParallelPlan(XElement qp, PlanFindingThresholds t, List<PlanFinding> found)
    {
        var reason = (string?)qp.Attribute("NonParallelPlanReason");
        if (reason is null || !CodeNonParallelReasons.Contains(reason)) return;

        double? cost = D(qp.Parent, "StatementSubTreeCost");
        if (cost is null || cost <= t.NonParallelMinCost) return;

        found.Add(new PlanFinding("non_parallel_plan", null, Json(new
        {
            reason,
            statement_subtree_cost = cost,
        })));
    }

    /// <summary>
    /// QueryPlan porte jusqu'à deux listes TraceFlags : celle de la compilation
    /// (IsCompileTime vrai) et celle de l'exécution. Seule la première a façonné le plan.
    /// </summary>
    private static void DetectTraceFlags(XElement qp, List<PlanFinding> found)
    {
        foreach (var list in qp.Elements(N("TraceFlags")))
        {
            if ((string?)list.Attribute("IsCompileTime") is not ("1" or "true")) continue;
            foreach (var flag in list.Elements(N("TraceFlag")))
            {
                found.Add(new PlanFinding("trace_flag", null, Json(new
                {
                    value = L(flag, "Value"),
                    scope = (string?)flag.Attribute("Scope"),
                })));
            }
        }
    }

    /// <summary>
    /// Un opérateur parallèle dont un thread traite bien plus que sa part. Le thread 0 est
    /// le coordinateur : il est exclu du plancher, du maximum et de la moyenne. Sur un
    /// Gather Streams, il porte toutes les lignes reçues des producteurs, et le compter
    /// doublerait le total.
    /// </summary>
    private static void DetectThreadSkew(XElement op, PlanFindingThresholds t, List<PlanFinding> found)
    {
        if ((string?)op.Attribute("Parallel") is not ("1" or "true")) return;
        var rti = op.Element(N("RunTimeInformation"));
        if (rti is null) return;

        long[] workers = [.. rti.Elements(N("RunTimeCountersPerThread"))
            .Where(th => I(th, "Thread") is not (null or 0))
            .Select(th => L(th, "ActualRows") ?? 0)];
        if (workers.Length == 0) return;
        long total = workers.Sum();
        if (total < t.ThreadSkewMinRows) return;

        double avg = workers.Average();
        long max = workers.Max();
        if (avg <= 0 || max <= t.ThreadSkewRatio * avg) return;

        found.Add(new PlanFinding("parallel_thread_skew", I(op, "NodeId"), Json(new
        {
            op = (string?)op.Attribute("PhysicalOp"),
            threads = workers.Length,
            total_rows = total,
            max_thread_rows = max,
            avg_thread_rows = Math.Round(avg, 1),
            ratio = Math.Round(max / avg, 2),
        })));
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

    /// <summary>
    /// Lève un <c>plan_warning</c> par enfant du <c>&lt;Warnings&gt;</c> de <paramref name="owner"/>
    /// (un RelOp ou un QueryPlan) et par attribut booléen vrai de cet élément.
    /// NoJoinPredicate, SpatialGuess, UnmatchedIndexes et FullUpdateForOnlineIndexBuild sont
    /// des attributs de WarningsType, pas des enfants : les ignorer perd la jointure sans
    /// prédicat et l'index filtré inutilisable.
    /// </summary>
    private static void DetectWarnings(XElement owner, int? nodeId, List<PlanFinding> found)
    {
        var w = owner.Element(N("Warnings"));
        if (w is null) return;
        foreach (var flag in w.Attributes())
        {
            if (flag.Value is not ("1" or "true")) continue;
            // UnmatchedIndexes="1" nomme ses index dans l'élément frère QueryPlan/UnmatchedIndexes.
            found.Add(new PlanFinding("plan_warning", nodeId, Json(new
            {
                warning = flag.Name.LocalName,
                detail = References(owner.Element(N(flag.Name.LocalName))),
            })));
        }
        foreach (var child in w.Elements())
        {
            var attrs = child.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}");
            // ColumnsWithNoStatistics n'a aucun attribut : ses colonnes sont des ColumnReference.
            var refs = References(child);
            found.Add(new PlanFinding("plan_warning", nodeId, Json(new
            {
                warning = child.Name.LocalName,
                detail = string.Join(", ", refs.Length == 0 ? attrs : attrs.Append(refs)),
            })));
        }
    }

    /// <summary>Les objets et colonnes nommés sous <paramref name="e"/>, en <c>Schema.Table.Column</c>.</summary>
    private static string References(XElement? e)
    {
        if (e is null) return "";
        var names = e.Descendants()
            .Where(d => d.Name == N("ColumnReference") || d.Name == N("Object"))
            .Select(d => string.Join(".", ((string[])["Database", "Schema", "Table", "Index", "Column"])
                .Select(a => Trim((string?)d.Attribute(a)))
                .Where(v => !string.IsNullOrEmpty(v))));
        return string.Join(", ", names);
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

    /// <summary>
    /// EstimateRows est une estimation PAR EXÉCUTION, ActualRows un cumul sur toutes les
    /// exécutions (et sur tous les threads). Le nombre d'exécutions estimé est
    /// 1 + EstimateRebinds + EstimateRewinds : sans lui, le côté interne d'une boucle
    /// imbriquée sort en fausse sous-estimation d'un facteur égal au nombre de lignes externes.
    /// </summary>
    private static void DetectCardinality(XElement op, PlanFindingThresholds t, List<PlanFinding> found)
    {
        double? perExecution = D(op, "EstimateRows");
        long? actual = SumActualRows(op);
        if (perExecution is null or <= 0 || actual is null) return;

        double executions = 1 + (D(op, "EstimateRebinds") ?? 0) + (D(op, "EstimateRewinds") ?? 0);
        double estimate = perExecution.Value * executions;

        double ratio = actual.Value >= estimate
            ? actual.Value / estimate
            : estimate / Math.Max(actual.Value, 1);
        if (ratio <= t.CardinalityRatio) return;

        found.Add(new PlanFinding("cardinality_misestimate", I(op, "NodeId"), Json(new
        {
            op = (string?)op.Attribute("PhysicalOp"),
            estimate_rows = perExecution,
            estimate_executions = executions,
            estimate_rows_all_executions = estimate,
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
