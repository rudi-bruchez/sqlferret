// src/SqlFerret.Core/Ingestion/IngestionService.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Model;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

namespace SqlFerret.Core.Ingestion;

public class IngestionService(DuckDbProject project, IngestionOptions options)
{
    private readonly RedactionPolicy _redaction = new(options.Redaction);
    private readonly Func<ExecutionEvent, bool> _ingestKeep = FilterCompiler.ToIngestPredicate(options.Filters);

    /// <summary>
    /// Le texte d'instruction n'est conservé verbatim que si les <b>deux</b> politiques
    /// l'autorisent. Gouverne les trois colonnes qui portent un input buffer en clair :
    /// <c>blocking_processes.inputbuf</c>, <c>blocking_reports.raw_xml</c> et
    /// <c>deadlock_reports.graph_xml</c>. Les deux XML ne sont pas sanitisables sans réécrire
    /// chaque nœud <c>inputbuf</c> du document ; tant que ce n'est pas fait, <c>literals</c> ne
    /// peut que refuser de les conserver — les garder contredirait la politique demandée depuis
    /// une colonne voisine.
    /// </summary>
    private bool VerbatimStatementTextAllowed =>
        options.Redaction == RedactionMode.Off && options.SqlText == SqlTextSanitization.Raw;

    public IngestionResult Ingest(string sourcePath,
        IEnumerable<(IXeEventData ev, string fileName, long offset)> events,
        int filesCount = 1, long bytesTotal = 0,
        IProgress<IngestionProgress>? progress = null)
    {
        long runId = project.BeginRun(sourcePath, filesCount, bytesTotal,
            redactionPolicy: options.Redaction.ToString().ToLowerInvariant(),
            sqlText: options.SqlText);

        long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
        long sqlTextSanitizeFailures = 0;
        long serverDiagnostics = 0, serverDiagnosticsUnhandled = 0, serverDiagnosticsParseFailures = 0;
        long embeddedBlocking = 0, embeddedBlockingFailures = 0;
        var diagSamples = new List<ServerDiagnosticsSample>();   // reaffecte a la vidange
        long blocking = 0, deadlocks = 0, blockingParseFailures = 0;
        long planProfiles = 0, planParseFailures = 0, planWriteFailures = 0;
        var planWriter = new SqlFerret.Core.Plans.PlanArtifactWriter(options.PlanProfileDir);
        var planBuffer = new List<PreparedPlanProfile>(options.BatchSize);
        var planThresholds = new SqlFerret.Core.Plans.PlanFindingThresholds();
        string currentFile = "";
        var buffer = new List<PreparedRow>(options.BatchSize);

        foreach (var (ev, fileName, offset) in events)
        {
            currentFile = fileName;
            read++;

            var bkind = EventMapper.ClassifyBlocking(ev.Name);
            if (bkind != BlockingEventKind.None)
            {
                if (bkind == BlockingEventKind.Blocked)
                {
                    var xml = EventMapper.ExtractBlockingXml(ev);
                    var rep = xml is null ? null : BlockingReportParser.Parse(xml, ev.Timestamp);
                    if (rep is null) { blockingParseFailures++; continue; }
                    // Le XML brut porte les input buffers en clair : meme porte que
                    // PrepareProc, donc meme condition.
                    var rawXml = VerbatimStatementTextAllowed ? xml : null;
                    project.InsertBlockingBatch(runId, [Prepare(rep, rawXml)]);
                    blocking++;
                }
                else
                {
                    var xml = EventMapper.ExtractDeadlockXml(ev);
                    var dl = xml is null ? null : DeadlockReportParser.Parse(xml, ev.Timestamp);
                    if (dl is null) { blockingParseFailures++; continue; }
                    project.InsertDeadlockBatch(runId, [dl with { GraphXmlRedacted = VerbatimStatementTextAllowed ? dl.GraphXmlRedacted : "<redacted/>" }]);
                    deadlocks++;
                }
                continue;
            }

            if (EventMapper.IsPlanProfile(ev.Name))
            {
                var planXml = EventMapper.ExtractShowplanXml(ev);
                var profile = planXml is null
                    ? null
                    : SqlFerret.Core.Plans.PlanProfileParser.TryParse(planXml, ev, planThresholds);
                if (profile is null) { planParseFailures++; continue; }

                var outcome = planWriter.Write(profile, planXml!);
                if (outcome == SqlFerret.Core.Plans.PlanWriteOutcome.Failed) planWriteFailures++;
                planBuffer.Add(new PreparedPlanProfile(profile, outcome));
                planProfiles++;

                if (planBuffer.Count >= options.BatchSize)
                {
                    project.InsertPlanProfileBatch(runId, planBuffer);
                    planBuffer.Clear();
                }
                continue;
            }

            if (EventMapper.IsServerDiagnostics(ev.Name))
            {
                var (comp, state, data) = EventMapper.ExtractDiagnostics(ev);
                var sample = ServerDiagnosticsParser.TryParse(comp, state, data, ev.Timestamp);

                switch (sample.Outcome)
                {
                    case DiagnosticsOutcome.Parsed: serverDiagnostics++; break;
                    case DiagnosticsOutcome.Unhandled: serverDiagnosticsUnhandled++; break;
                    default: serverDiagnosticsParseFailures++; break;
                }
                diagSamples.Add(sample);

                // Vidange par lots, mais jamais au milieu d'un cycle : les quatre composants d'un
                // cycle doivent etre groupes ensemble, et une coupure les repartirait sur deux
                // cycles. On attend donc que l'echantillon suivant soit eloigne du precedent —
                // meme seuil que HealthCycleGrouper — avant de vider.
                if (diagSamples.Count >= options.BatchSize)
                {
                    var cycles = HealthCycleGrouper.Group(diagSamples);
                    if (cycles.Count > 1)
                    {
                        // Le dernier cycle peut encore recevoir des echantillons : on le garde.
                        project.InsertHealthCycles(runId, [.. cycles.Take(cycles.Count - 1)]);
                        diagSamples = [.. cycles[cycles.Count - 1].Samples];
                    }
                }

                // Les rapports de blocage integres passent par PrepareProc comme les autres :
                // meme porte de confidentialite, meme empreinte d'inputbuf, aucun code nouveau.
                // Ils sont comptes a part : un rapport integre est un fragment de CET evenement,
                // pas un blocked_process_report, et l'ajouter a `blocking` ferait depasser
                // events_read a la somme de reconciliation.
                foreach (var reportXml in sample.EmbeddedBlockingXml)
                {
                    var rep = BlockingReportParser.Parse(reportXml, ev.Timestamp);
                    if (rep is null) { embeddedBlockingFailures++; continue; }
                    project.InsertBlockingBatch(runId, [Prepare(rep, null) with { Source = "diagnostics" }]);
                    embeddedBlocking++;
                }
                continue;
            }

            var e = EventMapper.Map(ev, fileName, offset);
            if (e.EventClass == EventClass.Unknown || string.IsNullOrEmpty(e.SqlTextRaw)) { unmapped++; continue; }
            if (!_ingestKeep(e)) { cleaned++; continue; }

            var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
            if (nq.TokenizeFailed) tokenizeFailures++;

            // Sanitize before the row is built, so unredacted literals never reach storage.
            // safeNq carries the substituted NormalizedSql: on tokenize failure the normalizer's
            // fallback leaves literals intact, and normalized_queries lives in the same file.
            var (sqlText, safeNq, sanitizeFailed) =
                SqlTextSanitizer.Apply(e.SqlTextRaw, nq, options.SqlText);
            if (sanitizeFailed) sqlTextSanitizeFailures++;

            buffer.Add(new PreparedRow(e with { SqlTextRaw = sqlText }, safeNq, RedactParams(e)));
            mapped++;

            if (buffer.Count >= options.BatchSize)
            {
                project.InsertBatch(runId, buffer); buffer.Clear();
                progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
            }
        }
        if (buffer.Count > 0) project.InsertBatch(runId, buffer);
        if (planBuffer.Count > 0) project.InsertPlanProfileBatch(runId, planBuffer);
        if (diagSamples.Count > 0)
            project.InsertHealthCycles(runId, HealthCycleGrouper.Group(diagSamples));

        progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
        project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures,
            blocking, deadlocks, blockingParseFailures, planProfiles, planParseFailures, planWriteFailures,
            sqlTextSanitizeFailures,
            serverDiagnostics, serverDiagnosticsUnhandled, serverDiagnosticsParseFailures,
            embeddedBlocking, embeddedBlockingFailures);
        return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures,
            blocking, deadlocks, blockingParseFailures, planProfiles, planParseFailures, planWriteFailures,
            sqlTextSanitizeFailures,
            serverDiagnostics, serverDiagnosticsUnhandled, serverDiagnosticsParseFailures,
            embeddedBlocking, embeddedBlockingFailures);
    }

    private PreparedBlockingReport Prepare(BlockingReport rep, string? rawXml)
    {
        return new PreparedBlockingReport(rep, PrepareProc(rep.Blocked), PrepareProc(rep.Blocking), rawXml);
    }

    private const string FallbackRedactedPlaceholder = "(unparseable inputbuf; redacted)";

    /// <summary>
    /// L'input buffer est du texte d'instruction : il relève de <c>--sanitize-sql-text</c> autant
    /// que de la rédaction. Les deux politiques se <b>composent</b> plutôt que de s'ignorer — le
    /// verbatim n'est conservé que si la rédaction est <c>off</c> <i>et</i> la politique de texte
    /// <c>raw</c>. Prises séparément, chacune laissait un trou : <c>--sanitize-sql-text literals</c>
    /// n'était pas consulté ici du tout, si bien qu'un import
    /// <c>--redaction off --sanitize-sql-text literals</c> écrivait l'input buffer littéral, à
    /// rebours de ce que la politique demandée annonçait.
    /// <para>Composer dans ce sens, et pas l'inverse, garantit que la sanitisation ne peut jamais
    /// relâcher ce que la rédaction retenait : le comportement par défaut ne bouge pas.</para>
    /// </summary>
    private PreparedBlockingProcess PrepareProc(BlockingProcess p)
    {
        if (string.IsNullOrEmpty(p.InputBufRaw))
            return new PreparedBlockingProcess(p, null, null);
        var nq = QueryNormalizer.Normalize(p.InputBufRaw);
        var keyed = p with { InputBufFingerprint = nq.NormalizedHash };

        if (VerbatimStatementTextAllowed)
        {
            // Les deux politiques autorisent le verbatim : stocker tel quel, sans masquage.
            return new PreparedBlockingProcess(keyed, nq, p.InputBufRaw);
        }
        if (nq.TokenizeFailed)
        {
            // FallbackCollapse a laissé les littéraux intacts. On remplace l'input buffer stocké
            // *et* le NormalizedSql par un substitut : les deux vivent dans le même fichier et se
            // rejoignent sur la même empreinte, corriger l'un seul déplacerait la fuite.
            // NormalizedHash, non réversible, est conservé pour les jointures d'empreinte.
            var failedNq = nq with { NormalizedSql = FallbackRedactedPlaceholder };
            return new PreparedBlockingProcess(keyed, failedNq, FallbackRedactedPlaceholder);
        }
        // Tokenisation réussie : ScriptDom a déjà retiré les littéraux. On stocke la variante
        // QI-collapsed et non NormalizedSql — sous SET QUOTED_IDENTIFIER OFF, "alice@example.com"
        // est une valeur que NormalizedSql laisserait passer. Voir NormalizedQuery.QiCollapsedSql.
        var safeNq = nq with { NormalizedSql = nq.QiCollapsedSql };
        return new PreparedBlockingProcess(keyed, safeNq, safeNq.NormalizedSql);
    }

    private List<PreparedParameter> RedactParams(ExecutionEvent e)
    {
        var list = new List<PreparedParameter>();
        foreach (var p in e.Parameters)
        {
            if (options.Redaction == RedactionMode.Off) continue; // off → no parameter rows
            var (stored, redacted) = _redaction.Apply(p.Name, p.ValueText);
            list.Add(new PreparedParameter(p.Ordinal, p.Name, p.SourceKind.ToString().ToLowerInvariant(),
                      p.SqlTypeGuess, stored, redacted, false, p.ParseConfidence));
        }
        return list;
    }
}
