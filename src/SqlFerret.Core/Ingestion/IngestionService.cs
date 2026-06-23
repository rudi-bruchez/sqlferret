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

    public IngestionResult Ingest(string sourcePath,
        IEnumerable<(IXeEventData ev, string fileName, long offset)> events,
        IProgress<IngestionProgress>? progress = null)
    {
        long runId = project.BeginRun(sourcePath, filesCount: 1, bytesTotal: 0,
            redactionPolicy: options.Redaction.ToString().ToLowerInvariant());

        long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
        long blocking = 0, deadlocks = 0, blockingParseFailures = 0;
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
                    project.InsertBlockingBatch(runId, [Prepare(rep)]);
                    blocking++;
                }
                else
                {
                    var xml = EventMapper.ExtractDeadlockXml(ev);
                    var dl = xml is null ? null : DeadlockReportParser.Parse(xml, ev.Timestamp);
                    if (dl is null) { blockingParseFailures++; continue; }
                    project.InsertDeadlockBatch(runId, [dl with { GraphXmlRedacted = options.Redaction == RedactionMode.Off ? dl.GraphXmlRedacted : "<redacted/>" }]);
                    deadlocks++;
                }
                continue;
            }

            var e = EventMapper.Map(ev, fileName, offset);
            if (e.EventClass == EventClass.Unknown || string.IsNullOrEmpty(e.SqlTextRaw)) { unmapped++; continue; }
            if (!_ingestKeep(e)) { cleaned++; continue; }

            var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
            if (nq.TokenizeFailed) tokenizeFailures++;

            buffer.Add(new PreparedRow(e, nq, RedactParams(e)));
            mapped++;

            if (buffer.Count >= options.BatchSize)
            {
                project.InsertBatch(runId, buffer); buffer.Clear();
                progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
            }
        }
        if (buffer.Count > 0) project.InsertBatch(runId, buffer);

        progress?.Report(new IngestionProgress(read, mapped, unmapped, cleaned, tokenizeFailures, currentFile));
        project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures, blocking, deadlocks, blockingParseFailures);
        return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures, blocking, deadlocks, blockingParseFailures);
    }

    private PreparedBlockingReport Prepare(BlockingReport rep)
    {
        return new PreparedBlockingReport(rep, PrepareProc(rep.Blocked), PrepareProc(rep.Blocking));
    }

    private const string FallbackRedactedPlaceholder = "(unparseable inputbuf; redacted)";

    private PreparedBlockingProcess PrepareProc(BlockingProcess p)
    {
        if (string.IsNullOrEmpty(p.InputBufRaw))
            return new PreparedBlockingProcess(p, null, null);
        var nq = QueryNormalizer.Normalize(p.InputBufRaw);
        if (options.Redaction == RedactionMode.Off)
        {
            // Off: store raw, no masking
            return new PreparedBlockingProcess(p with { InputBufFingerprint = nq.NormalizedHash }, nq, p.InputBufRaw);
        }
        if (nq.TokenizeFailed)
        {
            // Tokenize failed under non-Off redaction: FallbackCollapse left literals unmasked.
            // Replace both the stored inputbuf and the NormalizedSql with a safe placeholder.
            // NormalizedHash (a non-reversible hash) is safe to persist — keep it for fingerprint joins.
            var safeNq = nq with { NormalizedSql = FallbackRedactedPlaceholder };
            return new PreparedBlockingProcess(p with { InputBufFingerprint = nq.NormalizedHash }, safeNq, FallbackRedactedPlaceholder);
        }
        // Successful tokenization: ScriptDom already stripped literals from nq.NormalizedSql
        return new PreparedBlockingProcess(p with { InputBufFingerprint = nq.NormalizedHash }, nq, nq.NormalizedSql);
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
