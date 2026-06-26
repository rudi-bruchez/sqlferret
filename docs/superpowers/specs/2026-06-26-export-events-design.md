# Export blocking / deadlock event XML — design

**Date:** 2026-06-26
**Status:** Approved (brainstorming)
**Component:** `SqlFerret.Core` (Ingestion + new `Analysis`/export path) + `SqlFerret.Cli`

## Problem

A project's DuckDB store holds ingested `blocked_process_report` and `xml_deadlock_report`
events, but there is no way to get the underlying **XML back out** on demand. Concretely:

- **Deadlock graph XML** *is* persisted (`deadlock_reports.graph_xml`), but is replaced by
  `<redacted/>` whenever the import ran with `redaction != off`.
- **Blocking process XML** is **not** persisted at all: the original
  `<blocked-process-report>` is parsed into structured `blocking_processes` rows and the raw
  text is discarded.
- No CLI command exports these as files, and none filters by a time window.

Goal: a command-line tool — directly shell-callable by an MCP agent — that extracts one or
several blocking-process / deadlock-graph XML documents on demand (over a time range, or a
targeted blocking pattern), writing one file per event into a directory plus a machine-readable
manifest.

## Decisions (locked during brainstorming)

1. **Blocking XML = original raw, persisted.** Add a `raw_xml` column to `blocking_reports` and
   store the captured `<blocked-process-report>` XML, mirroring how `deadlock_reports.graph_xml`
   already works. (Not a reconstruction from structured fields.)
2. **Redaction gate.** Raw XML is stored **only when the import ran with `redaction = off`**
   (blocking → `raw_xml = NULL` otherwise; deadlock → `graph_xml = '<redacted/>'` otherwise).
   This preserves the hard invariant "no un-redacted value written to disk". A faithful XML
   export therefore requires the source trace to be imported with `--redaction off`.
3. **Missing-XML behavior = export available, skip the rest, report counts.** Events in the
   requested window whose XML is redacted/absent are skipped; the summary reports how many were
   written vs skipped. Partial success is supported (e.g. a project mixing `off` and `masked`
   runs).
4. **Selectors:** time window (`--from`/`--to`/`--last`) + `--fingerprint` (blocking pattern) +
   `--database` + `--limit`, on top of `--kind blocking|deadlock|both`.
5. **Output contract:** one file per event (`blocking_<utc>_<id>.xml`,
   `deadlock_<utc>_<id>.xdl`) + an `index.json` manifest, and a JSON summary on stdout for MCP
   consumption.
6. **Architecture = new `export-events` command + a dedicated Core service** (`EventExportService`),
   keeping `export-blocking` focused on the digest and making the export reusable by the future
   TUI/MCP hosts.

## Section 1 — Schema & ingestion (persist raw blocking XML)

### Schema (`DuckDbProject`)

- `blocking_reports` gains a nullable column `raw_xml TEXT`.
- Because tables are created with `CREATE TABLE IF NOT EXISTS`, existing project files would not
  pick up the new column. Add an idempotent migration at schema init, after the `CREATE`
  statements:
  ```sql
  ALTER TABLE blocking_reports ADD COLUMN IF NOT EXISTS raw_xml TEXT;
  ```
  (DuckDB supports `ADD COLUMN IF NOT EXISTS`.) Existing blocking rows keep `raw_xml = NULL`.

### Ingestion (`IngestionService`, `Blocked` branch)

The raw XML is already extracted via `EventMapper.ExtractBlockingXml(ev)`. Carry it to the
insert, gated on redaction:

```csharp
var xml = EventMapper.ExtractBlockingXml(ev);
var rep = xml is null ? null : BlockingReportParser.Parse(xml, ev.Timestamp);
if (rep is null) { blockingParseFailures++; continue; }
var rawXml = options.Redaction == RedactionMode.Off ? xml : null;   // mirror deadlock gating
project.InsertBlockingBatch(runId, [Prepare(rep, rawXml)]);
```

- `PreparedBlockingReport` gains a `string? RawXml` field.
- `DuckDbProject.InsertBlockingBatch` / the `INSERT INTO blocking_reports VALUES (...)` statement
  gains a bound `$raw` parameter (`(object?)report.RawXml ?? DBNull.Value`).
- **Deadlock path is unchanged** — `graph_xml` already stores the graph under the same gate.

Invariant check: raw blocking XML can contain literal SQL / PII. Gating storage on
`redaction == Off` keeps it consistent with the existing deadlock handling and the
"redaction before any value is written to disk" rule.

## Section 2 — Core service `EventExportService`

```csharp
public enum EventKind { Blocking, Deadlock, Both }

public sealed record EventExportOptions(
    string OutDir,
    EventKind Kind,
    QueryStoreWindow Window,        // reused from Query Store feature (--from/--to/--last)
    string? Fingerprint,            // blocking only
    int? DatabaseId,                // blocking only
    int Limit);

public sealed record EventExportResult(
    int BlockingWritten, int BlockingSkipped,
    int DeadlockWritten, int DeadlockSkipped,
    string OutDir, string IndexPath);

public sealed class EventExportService(DuckDBConnection conn)
{
    public EventExportResult Export(EventExportOptions opts, IProgress<string>? progress = null);
}
```

### Selection (SQL, bound parameters)

Aggregation/selection lives in SQL per the KISS invariant. All user-supplied values
(`$from`, `$to`, `$db`, `$fp`, `$limit`) are **bound parameters**; only fixed column names and
the `kind` discriminator are interpolated/branched in code.

`QueryStoreWindow.From`/`.To` are **nullable** (an unbounded window is allowed). Each
`captured_at` predicate is appended **only when its bound is non-null** — exactly like the other
optional filters below. An empty window (`--from`/`--to`/`--last` all omitted) exports the whole
history (still subject to `--limit`).

**Blocking — exportable rows:**
```sql
SELECT r.report_id, r.captured_at, r.database_id, r.raw_xml
FROM blocking_reports r
WHERE r.captured_at >= $from AND r.captured_at < $to
  AND r.raw_xml IS NOT NULL
  -- optional, appended only when provided:
  AND r.database_id = $db
  AND EXISTS (SELECT 1 FROM blocking_processes bp
              WHERE bp.report_id = r.report_id AND bp.inputbuf_fingerprint = $fp)
ORDER BY r.captured_at
LIMIT $limit;
```
`raw_xml IS NOT NULL` already excludes redacted/legacy runs (blocking stores `NULL`, never
`<redacted/>`).

**Deadlock — exportable rows:**
```sql
SELECT report_id, captured_at, victim_spids, participant_spids, graph_xml
FROM deadlock_reports
WHERE captured_at >= $from AND captured_at < $to
  AND graph_xml IS NOT NULL AND graph_xml <> '<redacted/>'
ORDER BY captured_at
LIMIT $limit;
```
`--fingerprint` / `--database` do **not** apply to deadlocks (`deadlock_reports` has neither
column); they are ignored with a warning when `--kind` includes deadlock.

**Skipped counts** (one cheap `COUNT(*)` per kind, **same time + optional filters** as the
exportable query, complementary XML predicate):
- Blocking: `COUNT(*) … WHERE captured_at in window [AND database_id=$db] [AND EXISTS(... inputbuf_fingerprint=$fp)] AND raw_xml IS NULL`.
  (The `inputbuf_fingerprint` is stored regardless of redaction, so the fingerprint filter is
  valid on redacted reports too.)
- Deadlock: `COUNT(*) … WHERE captured_at in window AND (graph_xml IS NULL OR graph_xml = '<redacted/>')`.

`--limit` caps the number of **files written** (exportable events), ordered by `captured_at`. It
applies **per kind** — with `--kind both`, up to `--limit` blocking files *and* up to `--limit`
deadlock files.

### File writing

- `Directory.CreateDirectory(OutDir)` (created if absent).
- One file per exportable event:
  - Blocking → `OutDir/blocking_<utc>_<id>.xml`
  - Deadlock → `OutDir/deadlock_<utc>_<id>.xdl`
  - `<utc>` = `captured_at` formatted `yyyyMMddTHHmmssfffZ` (filesystem-safe, sortable);
    `<id>` = `report_id`.
- `OutDir/index.json` — a JSON array of manifest entries (written with `WriteIndented = true`):
  ```json
  {
    "id": 1234,
    "kind": "blocking",
    "captured_at": "2026-06-16T04:30:47.006Z",
    "file": "blocking_20260616T043047006Z_1234.xml",
    "database_id": 7                      // blocking only
  }
  ```
  Deadlock entries carry `victim_spids` / `participant_spids` instead of `database_id`.
- Empty result → `index.json` = `[]`, all counts zero, exit 0.

`progress` (optional `IProgress<string>`) emits a short line as files are written, matching the
Query Store import gauge style.

## Section 3 — CLI command, safety, testing

### Command surface

```
export-events --project <dir> --out <dir>
              [--kind blocking|deadlock|both]      (default: both)
              [--from <dt> --to <dt> | --last <N>{h|d}]
              [--fingerprint <hash>]               (blocking only)
              [--database <id>]                    (blocking only)
              [--limit <n>]                        (default: 100)
```

- `--project` and `--out` are required.
- Window parsed via `QueryStoreWindow.Parse(--from, --to, --last, DateTime.UtcNow)`; on
  `ArgumentException`, print `export-events: <message>` and exit 1.
- `--kind` parsed case-insensitively into `EventKind`; invalid value → clear error + exit 1.
- **stdout = JSON** (single line, MCP-consumable):
  ```json
  {"outDir":"…","indexPath":"…",
   "blocking":{"written":12,"skipped":3},
   "deadlock":{"written":1,"skipped":0}}
  ```
- If `written == 0 && skipped > 0`, also print to **stderr**: a hint that those runs were
  imported with `redaction != off` and should be re-imported with `--redaction off` to export
  XML.
- If `--fingerprint`/`--database` are supplied together with `--kind deadlock`, print a stderr
  note that they are ignored for deadlocks.

### Safety (invariants enforced)

- `--out` rejects path traversal (reuse the existing `HasTraversal` guard used by
  `export-blocking`); directory created if missing.
- `--fingerprint`, `--database`, and the time window are **bound parameters**; `kind` and column
  names are fixed allow-listed identifiers.
- Raw XML (`raw_xml` / `graph_xml`) is only ever present for `redaction = off` runs.

### Testing (TDD: red → green → commit per change)

**Core**
- Schema migration: `ADD COLUMN IF NOT EXISTS raw_xml` is idempotent (re-open existing DB, column
  present, no error).
- Ingestion: a fake `blocked_process_report` event stores `raw_xml` when `redaction = off` and
  `NULL` otherwise (extend `BlockingIngestionTests`).
- `EventExportService`: seed `blocking_reports` + `deadlock_reports` with a mix of real-XML and
  redacted/NULL rows, then assert:
  - files written to `--out`, correct names/extensions, content equals stored XML;
  - `index.json` shape and entries;
  - `written` / `skipped` counts per kind;
  - `--fingerprint`, `--database`, `--limit`, `--kind` filters and the time window;
  - empty result → `[]` manifest, zero counts;
  - path-traversal `--out` rejected.

**CLI**
- Smoke test (`CliSmokeTests` style): `export-events` against a seeded project writes files and
  prints the JSON summary; exit code 0.

### Edge cases

- Project mixing `off` and `masked` runs → partial export, accurate skipped counts.
- Legacy projects (pre-migration blocking rows) → `raw_xml = NULL`, counted as skipped.
- `deadlock_reports` has no `database_id`/fingerprint → those selectors are deadlock-no-ops
  (warned).

## Out of scope (YAGNI)

- No MCP server implementation here — the command is the contract; an MCP host shells out to it
  (or, later, calls `EventExportService` directly). MCP server remains a separate deferred spec.
- No reconstruction of blocking XML from structured fields (rejected in favor of raw persistence).
- No additional selectors (spid, min-wait, victim, …) beyond those listed.
- No change to `export-blocking` (digest) behavior.
