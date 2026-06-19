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

    public IngestionResult Ingest(string sourcePath, IEnumerable<(IXeEventData ev, string fileName, long offset)> events)
    {
        long runId = project.BeginRun(sourcePath, filesCount: 1, bytesTotal: 0,
            redactionPolicy: options.Redaction.ToString().ToLowerInvariant());

        long read = 0, mapped = 0, unmapped = 0, cleaned = 0, tokenizeFailures = 0;
        var buffer = new List<PreparedRow>(options.BatchSize);

        foreach (var (ev, fileName, offset) in events)
        {
            read++;
            var e = EventMapper.Map(ev, fileName, offset);
            if (e.EventClass == EventClass.Unknown || string.IsNullOrEmpty(e.SqlTextRaw)) { unmapped++; continue; }
            if (!_ingestKeep(e)) { cleaned++; continue; }

            var nq = QueryNormalizer.Normalize(e.SqlTextRaw);
            if (nq.TokenizeFailed) tokenizeFailures++;

            buffer.Add(new PreparedRow(e, nq, RedactParams(e)));
            mapped++;

            if (buffer.Count >= options.BatchSize) { project.InsertBatch(runId, buffer); buffer.Clear(); }
        }
        if (buffer.Count > 0) project.InsertBatch(runId, buffer);

        project.FinishRun(runId, read, mapped, unmapped, cleaned, tokenizeFailures);
        return new IngestionResult(runId, read, mapped, unmapped, cleaned, tokenizeFailures);
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
