using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuckDB.NET.Data;
using SqlFerret.Core.Server;

namespace SqlFerret.Core.Analysis;

public enum EventKind { Blocking, Deadlock, Both }

public sealed record EventExportOptions(
    string OutDir,
    EventKind Kind,
    QueryStoreWindow Window,
    string? Fingerprint,
    int? DatabaseId,
    int Limit);

public sealed record EventExportResult(
    int BlockingWritten, int BlockingSkipped, int BlockingMatched,
    int DeadlockWritten, int DeadlockSkipped, int DeadlockMatched,
    string OutDir, string IndexPath);

internal sealed record EventExportManifestEntry(
    long Id, string Kind, string CapturedAt, string File,
    int? DatabaseId = null, string? VictimSpids = null, string? ParticipantSpids = null);

/// <summary>
/// Extracts raw blocked-process / deadlock-graph XML to a directory (one file per event) plus an
/// index.json manifest. Selection runs in SQL with bound parameters; XML is only present for runs
/// imported with redaction=off (otherwise it is skipped and counted).
/// </summary>
public sealed class EventExportService(DuckDBConnection conn)
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public EventExportResult Export(EventExportOptions opts, IProgress<string>? progress = null)
    {
        if (opts.OutDir.Split('/', '\\').Any(seg => seg == ".."))
            throw new ArgumentException("--out must not contain a path-traversal segment ('..')");

        Directory.CreateDirectory(opts.OutDir);
        var manifest = new List<EventExportManifestEntry>();
        int bWritten = 0, bSkipped = 0, bMatched = 0, dWritten = 0, dSkipped = 0, dMatched = 0;

        if (opts.Kind is EventKind.Blocking or EventKind.Both)
            (bWritten, bSkipped, bMatched) = ExportBlocking(opts, manifest, progress);

        if (opts.Kind is EventKind.Deadlock or EventKind.Both)
            (dWritten, dSkipped, dMatched) = ExportDeadlock(opts, manifest, progress);

        var indexPath = Path.Combine(opts.OutDir, "index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(manifest, ManifestJson));

        return new EventExportResult(
            bWritten, bSkipped, bMatched, dWritten, dSkipped, dMatched, opts.OutDir, indexPath);
    }

    private (int written, int skipped, int matched) ExportBlocking(
        EventExportOptions opts, List<EventExportManifestEntry> manifest, IProgress<string>? progress)
    {
        var (where, binds) = BuildWindowWhere(opts.Window, "r.captured_at");
        var extra = "";
        if (opts.DatabaseId is { } db) { extra += " AND r.database_id = $db"; binds.Add(("$db", db)); }
        if (!string.IsNullOrWhiteSpace(opts.Fingerprint))
        {
            extra += " AND EXISTS (SELECT 1 FROM blocking_processes bp " +
                     "WHERE bp.report_id = r.report_id AND bp.inputbuf_fingerprint = $fp)";
            binds.Add(("$fp", opts.Fingerprint!));   // non-null inside the guard
        }

        // One pass for both counts: matched = exportable rows (ignores --limit, so it exposes
        // truncation); skipped = redacted/absent. The reader below is the only LIMIT-capped query.
        var (matched, skipped) = CountMatchedSkipped(
            "blocking_reports r", $"{where}{extra}", "r.raw_xml IS NOT NULL", "r.raw_xml IS NULL", binds);

        int written = 0;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
              SELECT r.report_id, r.captured_at, r.database_id, r.raw_xml
              FROM blocking_reports r
              WHERE {where}{extra} AND r.raw_xml IS NOT NULL
              ORDER BY r.captured_at, r.report_id LIMIT $limit
              """;
            foreach (var (n, v) in binds) Bind(c, n, v);
            Bind(c, "$limit", opts.Limit);
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                long id = rd.GetInt64(0);
                DateTime ts = rd.GetDateTime(1);
                int? dbid = rd.IsDBNull(2) ? null : rd.GetInt32(2);
                string xml = rd.GetString(3);
                string file = $"blocking_{FileStamp(ts)}_{id}.xml";
                File.WriteAllText(Path.Combine(opts.OutDir, file), xml);
                manifest.Add(new EventExportManifestEntry(id, "blocking", IsoStamp(ts), file, DatabaseId: dbid));
                written++;
                // NOTE: increment OUTSIDE the null-conditional (see Task 3 note) — `{++written}`
                // inside progress?.Report(...) is skipped entirely when progress is null.
                progress?.Report($"blocking {written}");
            }
        }
        return (written, skipped, matched);
    }

    // A deadlock graph is exportable when present and not the redaction placeholder.
    private const string DeadlockExportable = "graph_xml IS NOT NULL AND graph_xml <> '<redacted/>'";
    private const string DeadlockRedacted = "graph_xml IS NULL OR graph_xml = '<redacted/>'";

    private (int written, int skipped, int matched) ExportDeadlock(
        EventExportOptions opts, List<EventExportManifestEntry> manifest, IProgress<string>? progress)
    {
        var (where, binds) = BuildWindowWhere(opts.Window, "captured_at");

        var (matched, skipped) = CountMatchedSkipped(
            "deadlock_reports", where, DeadlockExportable, DeadlockRedacted, binds);

        int written = 0;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
              SELECT report_id, captured_at, victim_spids, participant_spids, graph_xml
              FROM deadlock_reports
              WHERE {where} AND {DeadlockExportable}
              ORDER BY captured_at, report_id LIMIT $limit
              """;
            foreach (var (n, v) in binds) Bind(c, n, v);
            Bind(c, "$limit", opts.Limit);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                DateTime ts = r.GetDateTime(1);
                string? victim = r.IsDBNull(2) ? null : r.GetString(2);
                string? parts = r.IsDBNull(3) ? null : r.GetString(3);
                string xml = r.GetString(4);
                string file = $"deadlock_{FileStamp(ts)}_{id}.xdl";
                File.WriteAllText(Path.Combine(opts.OutDir, file), xml);
                manifest.Add(new EventExportManifestEntry(
                    id, "deadlock", IsoStamp(ts), file, VictimSpids: victim, ParticipantSpids: parts));
                written++;
                // NOTE: increment OUTSIDE the null-conditional. `progress?.Report($"... {++written}")`
                // short-circuits the whole expression when progress is null, so ++written never runs.
                progress?.Report($"deadlock {written}");
            }
        }
        return (written, skipped, matched);
    }

    // Single-pass count of exportable (matched, ignores --limit so callers can detect truncation)
    // and redacted/absent (skipped) rows. The predicate fragments are fixed, code-controlled SQL
    // (never user input); the window/optional filters in `where` carry their values as bound params.
    private (int matched, int skipped) CountMatchedSkipped(
        string fromClause, string where, string matchedPredicate, string skippedPredicate,
        List<(string name, object value)> binds)
    {
        using var c = conn.CreateCommand();
        c.CommandText = $"""
          SELECT count(*) FILTER (WHERE {matchedPredicate}), count(*) FILTER (WHERE {skippedPredicate})
          FROM {fromClause}
          WHERE {where}
          """;
        foreach (var (n, v) in binds) Bind(c, n, v);
        using var r = c.ExecuteReader();
        r.Read();
        return ((int)r.GetInt64(0), (int)r.GetInt64(1));
    }

    // Builds a WHERE fragment for the optional time window. `col` is a fixed identifier supplied by
    // the caller (never user input); From/To are bound parameters appended only when present.
    private static (string where, List<(string name, object value)> binds) BuildWindowWhere(
        QueryStoreWindow w, string col)
    {
        var clauses = new List<string> { "1=1" };
        var binds = new List<(string, object)>();
        if (w.From is { } f) { clauses.Add($"{col} >= $from"); binds.Add(("$from", f)); }
        if (w.To is { } t) { clauses.Add($"{col} < $to"); binds.Add(("$to", t)); }
        return (string.Join(" AND ", clauses), binds);
    }

    private static void Bind(System.Data.IDbCommand c, string name, object value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name.TrimStart('$');
        p.Value = value;
        c.Parameters.Add(p);
    }

    private static string FileStamp(DateTime ts) =>
        ts.ToString("yyyyMMddTHHmmssfff'Z'", CultureInfo.InvariantCulture);
    private static string IsoStamp(DateTime ts) =>
        ts.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
