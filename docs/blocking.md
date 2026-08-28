# Blocking and deadlocks

Blocking analysis answers a question that timing data alone cannot: *which query shape is holding
locks that everyone else is waiting on.* Not which query is slow, but which query makes other
queries slow.

## Capturing blocked-process reports

The blocked-process report is not emitted unless you turn on the monitor. It is off by default.

```sql
-- report anything blocked for more than 5 seconds
EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
EXEC sp_configure 'blocked process threshold (s)', 5; RECONFIGURE;
```

Then add the events to your session:

```sql
ALTER EVENT SESSION [sqlferret_capture] ON SERVER
  ADD EVENT sqlserver.blocked_process_report,
  ADD EVENT sqlserver.xml_deadlock_report;
```

The threshold is a trade-off. Five seconds catches real incidents on an OLTP system. One second
catches lock convoys but produces a lot of reports. The monitor runs on a background task at the
threshold interval, so a very low value has a measurable cost.

Deadlock graphs (`xml_deadlock_report`) need no configuration; the engine always produces them.

## What ingestion does with them

A `blocked_process_report` is parsed into three rows:

- One `blocking_reports` row: timestamp, `monitor_loop`, `database_id`, and the raw XML if the
  redaction policy permits it.
- Two `blocking_processes` rows, one `blocked` and one `blocking`, each with SPID, status, lock
  mode, isolation level, transaction count, client application, host, login, wait time, the raw
  wait resource, and the input buffer.

The wait resource string is parsed rather than stored as text:

| Type | Example raw form | Extracted |
|---|---|---|
| `Key` | `KEY: 5:72057594043039744 (61a06abd401c)` | `hobt_id` |
| `Object` | `OBJECT: 5:1234567890:0` | `object_id` |
| `Page` | `PAGE: 5:1:12345` | |
| `Rid` | `RID: 5:1:12345:3` | |
| `Database`, `PageLatch`, `AppLock`, `Other` | | |

Locality — the distribution of these types — is the first thing to look at. Overwhelmingly `Key`
means row-level contention on specific index entries, which is usually a transaction-duration or
index-design problem. `Object` means table-level locks, which usually means lock escalation or an
explicit hint. `Page` and `PageLatch` are different animals entirely and point at allocation or
latch contention rather than at your queries.

### The join that makes it useful

Each process's **input buffer** goes through the same normalizer as the rest of the workload, and
its fingerprint is stored as `blocking_processes.inputbuf_fingerprint`. That column joins straight
to `normalized_queries.normalized_hash`.

Consequence: "which query shape blocks the most" is a `GROUP BY`, not a manual SPID hunt. The
digest's top-blocker and top-blocked lists are exactly that grouping.

Input buffers are truncated by the engine, so a very long batch may normalize to a shape that does
not exist in `normalized_queries`. The digest falls back to the raw input buffer text when the
join finds nothing.

## The digest

```bash
sqlferret export-blocking --project ./audits/prod --format md --out blocking.md
```

`BlockingDigest.Build(samplesPerPattern, topK)` produces one structured result. The CLI renders it
as Markdown, JSON, or both; `BlockingDigest` itself returns pure data with no formatting, so a
future host can render it differently.

| Section | Content |
|---|---|
| **Overview** | Report count, deadlock count, first and last timestamp |
| **Wait times** | p50, p95 and max blocked wait, in microseconds |
| **Locality** | Blocked wait-resource type distribution, with percentages |
| **Top objects** | Most contended `object_id` values |
| **Top blockers** | Query shapes appearing as the blocker, ranked by incident count |
| **Top blocked** | Query shapes appearing as the victim |
| **Lock modes** | Distribution over the blocked side |
| **Isolation levels** | Distribution over the blocked side |
| **Chains** | Per monitor loop: chain depth, head SPID, edge count |
| **Sample contention** | Up to `--samples` concrete incidents per top-blocker pattern, showing both sides |

Everything except the samples is an aggregate, so the digest stays a fixed size regardless of how
many reports the trace holds. That is the point: it is meant to be read in full, by a person or by
a model.

### Chains

Chain reconstruction uses `monitor_loop` as the grouping key, since reports emitted by the same
monitor pass describe the same instant.

Within a loop, each report is an edge: `blocked.spid` waits on `blocking.spid`. A **head** is a
blocking SPID that is never itself blocked in that loop. A recursive CTE walks forward from every
head to compute depth, capped at 64 to guarantee termination even on a cyclic edge set.

Depth 2 is ordinary: one blocker, one victim. Depth 4 or more is a convoy, and the head SPID is
the one transaction whose behavior is worth changing.

### Reading the output

Start with **locality** to learn what kind of problem you have, then **top blockers** to learn
whose fault it is, then **chains** to learn whether it is one session or a pile-up, and only then
open the **samples** to see actual statements.

Going straight to the samples is tempting and usually misleading: the first incident you look at
is rarely the representative one.

## Raw XML export

The digest is a summary. When you need the original report — to open in SSMS, to attach to a
ticket, or to feed to a plan-analysis tool — export the XML:

```bash
sqlferret export-events --project ./audits/prod --out ./exports/events \
    --kind both --last 24h --limit 100
```

One file per event, plus an `index.json` manifest with id, kind, timestamp, file name, and (for
deadlocks) victim and participant SPIDs.

Narrow it with `--fingerprint <hash>` to a single contention pattern taken from the digest, or
with `--database <id>`. Both apply to blocking only, and are ignored with a warning when deadlocks
are in scope.

**This only works on runs imported with `--redaction off`.** The XML embeds full input buffers, so
it is only retained when nothing is being redacted. Against any other project the command writes
nothing and stderr tells you to re-import. See [privacy.md](privacy.md#the-sharp-edge-in-off).

## Deadlocks

Deadlock graphs are stored whole, in `deadlock_reports.graph_xml`, with victim and participant
SPIDs extracted into their own columns. Under any policy other than `off`, the graph is replaced
by the literal `<redacted/>` — the row still records that a deadlock happened, and when, and who
was involved.

There is no deadlock digest yet. Export the graphs and open them in SSMS or Plan Explorer, which
render the graph visually:

```bash
sqlferret export-events --project ./audits/prod --out ./exports/deadlocks --kind deadlock
```

## Querying directly

```sql
-- top blockers with total blocked time, not just incident count
SELECT COALESCE(n.normalized_sql, p.inputbuf) AS blocker,
       count(*) AS incidents,
       sum(v.wait_time_us) / 1e6 AS victim_wait_s
FROM blocking_processes p
JOIN blocking_processes v ON v.report_id = p.report_id AND v.role = 'blocked'
LEFT JOIN normalized_queries n ON n.normalized_hash = p.inputbuf_fingerprint
WHERE p.role = 'blocking'
GROUP BY 1 ORDER BY victim_wait_s DESC LIMIT 20;
```

```sql
-- does blocking cluster in time?
SELECT date_trunc('hour', captured_at) AS hour, count(*) AS reports
FROM blocking_reports GROUP BY 1 ORDER BY 1;
```

```sql
-- which isolation level shows up on the blocked side?
SELECT isolation_level, count(*)
FROM blocking_processes WHERE role = 'blocked'
GROUP BY 1 ORDER BY 2 DESC;
```

A large `read committed` share on the blocked side, in a workload where snapshot isolation is
available, is often the cheapest fix on the list.
