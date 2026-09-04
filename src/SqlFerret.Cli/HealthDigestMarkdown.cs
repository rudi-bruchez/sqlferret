// src/SqlFerret.Cli/HealthDigestMarkdown.cs
using System.Globalization;
using System.Text;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;

namespace SqlFerret.Cli;

/// <summary>
/// Rendu Markdown du digest de sante, dans l'HOTE et non dans Core, comme
/// <see cref="BlockingDigestMarkdown"/>. L'invariant est explicite : « Formatting to ms/s happens
/// only in hosts, via DisplayFormat. Never convert units inside Core. » Une premiere version
/// divisait par 1000 dans Core.
/// </summary>
public static class HealthDigestMarkdown
{
    /// <summary>
    /// Un nom qui se termine par « Us » porte des microsecondes — c'est la convention de Core, et
    /// l'hote est le seul endroit ou la conversion a le droit d'avoir lieu. Les autres valeurs
    /// sont des comptes, rendus en culture invariante comme tout le reste du fichier. Deux jauges
    /// sortaient en entiers nus dans le fichier meme qui avait ete deplace ici pour cet invariant.
    /// </summary>
    private static string Num(double? v, string name) =>
        v is null ? "-"
        : name.EndsWith("Us", StringComparison.Ordinal)
            ? DisplayFormat.Duration((long)v.Value, "ms")
            : v.Value.ToString("G15", CultureInfo.InvariantCulture);

    private static string Num(long v, string name) => Num((double)v, name);

    /// <summary>
    /// Tout texte venu de la capture est une donnee d'une AUTRE machine, et ce fichier l'ecrit
    /// dans un artefact destine a etre partage. Rendu tel quel entre backticks il sort de sa
    /// cellule : un backtick ferme le span, une barre ouvre une colonne, un saut de ligne termine
    /// le tableau, et la suite devient du Markdown a part entiere. Un chemin de fichier suffit —
    /// quiconque cree une base choisit le nom du fichier.
    /// <para>Markdown n'offre aucun echappement du backtick a l'interieur d'un span de code, donc
    /// il est remplace. C'est coherent avec la regle du projet : <c>table</c> et <c>md</c> sont
    /// des formats de PRESENTATION, <c>csv</c> et <c>json</c> sont les formats fideles. La valeur
    /// exacte reste en base et dans le JSON.</para>
    /// </summary>
    private static string Safe(string? v) => string.IsNullOrEmpty(v) ? "" : new StringBuilder(v)
        .Replace((char)13, ' ').Replace((char)10, ' ').Replace((char)9, ' ')
        .Replace("`", "'").Replace("|", @"\|").ToString();

    public static string Render(HealthDigestEnvelope e)
    {
        var d = e.Digest;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Health digest").AppendLine();

        sb.AppendLine("## Coverage").AppendLine();
        if (d.Coverage.Cycles == 0)
        {
            sb.AppendLine("No diagnostics samples.").AppendLine();
        }
        else
        {
            sb.AppendLine($"- Cycles: {d.Coverage.Cycles}");
            sb.AppendLine($"- Window: {d.Coverage.First:u} to {d.Coverage.Last:u} "
                        + $"({d.Coverage.SpanMinutes.ToString("F1", inv)} min)");
            sb.AppendLine($"- Median interval: {d.Coverage.MedianIntervalMin.ToString("F2", inv)} min; "
                        + $"largest gap: {d.Coverage.LargestGapMin.ToString("F2", inv)} min");
            sb.AppendLine($"- Events in the capture that are not diagnostics: "
                        + $"{(d.Coverage.NonDiagnosticsShare * 100).ToString("F1", inv)} %");
            sb.AppendLine($"- Sampling series: {d.Coverage.Series.Count}");
            foreach (var s in d.Coverage.Series)
                sb.AppendLine($"  - `{Safe(s.SeriesKey)}`: {s.Cycles} cycles, median "
                            + $"{s.MedianIntervalMin.ToString("F2", inv)} min");
            sb.AppendLine();
        }

        foreach (var n in d.Notes) sb.AppendLine($"> {n}").AppendLine();

        sb.AppendLine("## Non-clean states").AppendLine();
        if (d.NonCleanStates.Count == 0) sb.AppendLine("Every component reported CLEAN.");
        else foreach (var s in d.NonCleanStates)
            sb.AppendLine($"- `{Safe(s.Component)}` = {Safe(s.State)} in {s.Cycles} cycle(s)");
        sb.AppendLine();

        sb.AppendLine("## Waits").AppendLine();
        if (d.TopWaits.Count == 0)
        {
            sb.AppendLine("None recorded.");
        }
        else
        {
            sb.AppendLine("Ranked by count delta. Wait times are since instance start.").AppendLine();
            sb.AppendLine("| Wait type | delta waits | over (min) | per min | avg since instance start |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var w in d.TopWaits)
                sb.AppendLine($"| `{Safe(w.WaitType)}` | {w.WaitsDelta} | {w.SpanMinutes.ToString("F1", inv)} "
                            + $"| {w.PerMinute.ToString("F1", inv)} "
                            + $"| {DisplayFormat.Duration(w.LifetimeAvgWaitUs, "ms")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Memory").AppendLine();
        foreach (var s in d.MemoryPressure) sb.AppendLine($"- `{Safe(s.Name)}`: +{Num(s.Delta, s.Name)}");
        // Un drapeau 0/1 et une duree de semantique non documentee : des distributions, pas des
        // deltas. Bloque a 1, isAnyPoolOutOfMemory rendait un delta nul et disparaissait.
        foreach (var g in d.MemoryGauges)
            sb.AppendLine($"- `{Safe(g.Name)}`: min {Num(g.Min, g.Name)}, median {Num(g.Median, g.Name)}, "
                        + $"p95 {Num(g.P95, g.Name)}, max {Num(g.Max, g.Name)} ({g.Samples} samples)");
        if (d.MemoryPressure.Count == 0 && d.MemoryGauges.Count == 0)
            sb.AppendLine("No memory pressure counter moved.");
        sb.AppendLine();
        if (d.MemoryMovers.Count == 0) sb.AppendLine("No memory report entries.");
        else foreach (var m in d.MemoryMovers)
            sb.AppendLine($"- `{Safe(m.Description)}` ({Safe(m.ReportName)}): {m.First.ToString("F0", inv)} to "
                        + $"{m.Last.ToString("F0", inv)} ({m.Change.ToString("+#;-#;0", inv)} {m.Unit})");
        sb.AppendLine();

        sb.AppendLine("## Worker pressure").AppendLine();
        if (d.WorkerPressure.Count == 0) sb.AppendLine("Not recorded.");
        else foreach (var s in d.WorkerPressure)
            sb.AppendLine($"- `{Safe(s.Name)}`: min {Num(s.Min, s.Name)}, median {Num(s.Median, s.Name)}, "
                        + $"p95 {Num(s.P95, s.Name)}, max {Num(s.Max, s.Name)} ({s.Samples} samples)");
        sb.AppendLine();

        sb.AppendLine("## Stability signals").AppendLine();
        if (d.StabilitySignals.Count == 0) sb.AppendLine("Nothing moved.");
        else foreach (var (name, delta) in d.StabilitySignals)
            sb.AppendLine($"- `{Safe(name)}`: +{Num(delta, name)}");
        sb.AppendLine();

        sb.AppendLine("## I/O").AppendLine();
        if (d.IoCounters.Count == 0) sb.AppendLine("No I/O counter moved.");
        else foreach (var s in d.IoCounters) sb.AppendLine($"- `{Safe(s.Name)}`: +{Num(s.Delta, s.Name)}");
        sb.AppendLine();

        sb.AppendLine("## Worst pending I/O").AppendLine();
        if (d.WorstPendingIo.Count == 0) sb.AppendLine("None recorded.");
        else foreach (var i in d.WorstPendingIo)
            sb.AppendLine($"- {DisplayFormat.Duration(i.DurationUs ?? 0, "ms")} on "
                        + $"`{Safe(i.FilePath)}` at {i.CapturedAt:u}");
        sb.AppendLine();

        sb.AppendLine("## Blocking seen in diagnostics cycles").AppendLine();
        if (d.DiagnosticsBlocking.Count == 0) sb.AppendLine("None.");
        else foreach (var b in d.DiagnosticsBlocking)
            sb.AppendLine($"- spid {b.BlockedSpid} blocked by {b.BlockingSpid} for "
                        + $"{DisplayFormat.Duration(b.WaitTimeUs ?? 0, "ms")} "
                        + $"({Safe(b.WaitResourceType)}) at {b.CapturedAt:u}");

        return sb.ToString();
    }
}
