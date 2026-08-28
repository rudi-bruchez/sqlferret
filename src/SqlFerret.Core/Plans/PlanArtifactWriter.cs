// src/SqlFerret.Core/Plans/PlanArtifactWriter.cs
using System.Text;

namespace SqlFerret.Core.Plans;

/// <summary>
/// Seule unité de la fonctionnalité qui touche au disque. Écrit les .sqlplan en flux, et
/// dédoublonne par plan_hash : un fichier pour la première occurrence, un pour l'exécution
/// la plus lente. Ne conserve jamais un XML en mémoire — seulement deux durées par hash.
/// <paramref name="runDir"/> null désactive l'écriture tout en gardant l'état de
/// dédoublonnage, ce qui permet d'ingérer hors projet.
/// </summary>
public sealed class PlanArtifactWriter(string? runDir)
{
    private readonly Dictionary<string, (long? First, long? Worst)> _seen = new(StringComparer.Ordinal);

    // SQL Server émet les .sqlplan en UTF-16 et leur déclaration XML l'annonce. Écrire les
    // octets en UTF-8 sous une déclaration disant utf-16 produit un fichier incohérent.
    // UTF-16 LE avec BOM : aucune transformation du contenu, encodage conforme.
    private static readonly UnicodeEncoding Utf16Bom = new(bigEndian: false, byteOrderMark: true);

    public PlanWriteOutcome Write(PlanProfile profile, string showplanXml)
    {
        if (!_seen.TryGetValue(profile.PlanHash, out var state))
        {
            // Même règle : rien n'est mémorisé si l'écriture échoue, de sorte que
            // l'occurrence suivante retente. Un échec permanent se voit alors dans
            // plan_write_failures plutôt que de disparaître en silence.
            if (!Persist(FileName(profile.FileStem, worst: false), showplanXml))
                return PlanWriteOutcome.Failed;
            _seen[profile.PlanHash] = (profile.DurationUs, null);
            return PlanWriteOutcome.WroteFirst;
        }

        // Une durée nulle ne peut départager personne : elle ne gagne jamais.
        if (profile.DurationUs is null) return PlanWriteOutcome.Skipped;

        long best = Math.Max(state.First ?? long.MinValue, state.Worst ?? long.MinValue);
        if (state.First is null && state.Worst is null) best = long.MinValue;

        // Strictement supérieur : à durées égales, la première occurrence l'emporte.
        // La requête de la passe finale doit trancher pareil (ORDER BY ... , plan_profile_id ASC).
        if (profile.DurationUs.Value <= best) return PlanWriteOutcome.Skipped;

        // L'état n'est mis à jour QU'APRÈS une écriture réussie. L'inverse laisserait
        // _seen croire que cette durée est le maximum alors qu'aucun fichier ne la porte :
        // une exécution ultérieure, plus lente que le réel mais moins que l'échec, serait
        // écartée et le disque ne correspondrait plus à ce que la base désigne.
        if (!Persist(FileName(profile.FileStem, worst: true), showplanXml))
            return PlanWriteOutcome.Failed;

        _seen[profile.PlanHash] = (state.First, profile.DurationUs);
        return PlanWriteOutcome.WroteWorst;
    }

    public static string FileName(string fileStem, bool worst) =>
        worst ? $"{fileStem}.worst.sqlplan" : $"{fileStem}.sqlplan";

    private bool Persist(string fileName, string xml)
    {
        if (runDir is null) return true;
        try
        {
            Directory.CreateDirectory(runDir);
            File.WriteAllText(Path.Combine(runDir, fileName), xml, Utf16Bom);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Format d'horodatage du contrat de corrélation manuelle : ISO 8601, UTC, suffixe Z,
    /// précision microseconde. La troncature depuis les ticks 100 ns du .xel est assumée.
    /// </summary>
    private const string Iso8601Micro = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    // Les .sqlplan partent en UTF-16 (contrainte SSMS) ; les artefacts JSON en UTF-8 sans
    // BOM, format attendu par tout lecteur JSON et par les agents qui les consomment.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString(Iso8601Micro, System.Globalization.CultureInfo.InvariantCulture);

    public void WriteDigests(IReadOnlyList<PlanDigestRow> rows)
    {
        if (runDir is null) return;
        Directory.CreateDirectory(runDir);
        foreach (var r in rows)
        {
            var doc = new
            {
                schema_version = 1,
                plan_hash = r.PlanHash,
                plan_hash_source = r.PlanHashSource,
                statement_count = r.StatementCount,
                query_hash = r.QueryHash,
                statement_type = r.StatementType,
                statement_text = r.StatementText,
                statement_text_length = r.StatementTextLength,
                executions = new
                {
                    count = r.ExecutionCount,
                    duration_us = new { min = r.DurationMinUs, max = r.DurationMaxUs },
                    cpu_time_us = new { min = r.CpuMinUs, max = r.CpuMaxUs },
                    captured_at_utc = new { min = Stamp(r.CapturedMinUtc), max = Stamp(r.CapturedMaxUtc) },
                },
                grant = new
                {
                    serial_desired_kb = r.SerialDesiredMemoryKb,
                    granted_kb = r.GrantedMemoryKb,
                    max_used_kb = r.MaxUsedMemoryKb,
                    dop = r.Dop,
                },
                files = new
                {
                    first = Ref(r.First),
                    worst = Ref(r.Worst),
                },
                findings = r.Findings.Select(f => new
                {
                    kind = f.Kind,
                    node_id = f.NodeId,
                    detail = System.Text.Json.JsonDocument.Parse(f.DetailJson).RootElement,
                }).ToArray(),
            };
            File.WriteAllText(Path.Combine(runDir, $"{r.FileStem}.digest.json"),
                System.Text.Json.JsonSerializer.Serialize(doc, JsonOpts), Utf8NoBom);
        }
    }

    public void WriteIndex(IReadOnlyList<PlanDigestRow> rows)
    {
        if (runDir is null) return;
        Directory.CreateDirectory(runDir);
        var doc = new
        {
            schema_version = 1,
            plans = rows.Select(r => new
            {
                plan_hash = r.PlanHash,
                query_hash = r.QueryHash,
                statement_type = r.StatementType,
                executions = r.ExecutionCount,
                duration_us = new { min = r.DurationMinUs, max = r.DurationMaxUs },
                captured_at_utc = new
                {
                    first = r.First is null ? null : Stamp(r.First.CapturedAtUtc),
                    worst = r.Worst is null ? null : Stamp(r.Worst.CapturedAtUtc)
                },
                files = new
                {
                    first = r.First?.Path,
                    worst = r.Worst?.Path,
                    digest = $"{r.FileStem}.digest.json"
                },
                finding_kinds = r.Findings.Select(f => f.Kind).Distinct().OrderBy(k => k).ToArray(),
            }).ToArray(),
        };
        File.WriteAllText(Path.Combine(runDir, "index.json"),
            System.Text.Json.JsonSerializer.Serialize(doc, JsonOpts), Utf8NoBom);
    }

    private static object? Ref(PlanFileRef? f) => f is null ? null : new
    {
        path = f.Path,
        captured_at_utc = Stamp(f.CapturedAtUtc),
        duration_us = f.DurationUs,
        cpu_time_us = f.CpuTimeUs,
    };
}
