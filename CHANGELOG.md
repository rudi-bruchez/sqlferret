# Changelog

Notable changes to SQLFerret. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html) with the caveat that it is
pre-1.0: the minor version moves for features and for behaviour changes alike. The CLI
surface is still moving, and 1.0 would promise a command and flag stability this tool is
not ready to make.

This file starts at 0.2.0, the first *published* release. `v0.1.0` is a real tag, and the
version it declares is recorded in the `ToolVersion` of any project directory created by a
build from early September — but no release was ever published for it and no binaries were
ever built, because the release workflow did not exist yet. Earlier work is recorded in the
git history, which is the honest record of it; reconstructing per-release entries after the
fact would mean inventing boundaries the repository never had.

The version is written into every project directory's `project.json` as `ToolVersion`, so
an audit can always name the build that produced it.

## [Unreleased]

## [0.4.0] - 2026-09-27

Plan findings that match what the engine actually writes. Three rules were checked against
showplans produced on a real instance and published plans: `spill_to_tempdb` looked for spill
details in a place the engine never puts them, `plan_warning` read only operator-level warnings,
and `cardinality_misestimate` compared a per-execution estimate with a total over all
executions. Each rule now reads the real shape, and each fixture was rebuilt from it.

### Fixed

- `spill_to_tempdb` never fired on a real plan. It looked for `SortSpillDetails` and
  `HashSpillDetails` as direct children of the operator, and the engine writes them under
  the operator's `<Warnings>`: measured on 158 real plans, and reproduced with a sort and a
  hash join forced to spill on SQL Server 2025. Spills were only visible as `plan_warning`.
- `plan_warning` never read statement-level warnings. `MemoryGrantWarning`,
  `PlanAffectingConvert`, `Wait` and `UnmatchedIndexes` live under `QueryPlan/Warnings`,
  which no rule visited, and the boolean attributes of `<Warnings>` (`NoJoinPredicate`
  among them) were ignored because only child elements were read. Both are now reported,
  statement-level ones with a null `node_id`. `ColumnsWithNoStatistics` now names its
  column instead of carrying an empty `detail`.
- `cardinality_misestimate` compared an estimate per execution with actual rows cumulated
  over all executions, so the inner side of every nested loops join read as an
  underestimate. The estimate is now `EstimateRows × (1 + EstimateRebinds +
  EstimateRewinds)`, and the detail carries `estimate_executions` and
  `estimate_rows_all_executions`. On PlanInspector's published plans, 25 of 97 verdicts
  change, in both directions.

## [0.3.0] - 2026-09-04

System Health. SQL Server's own `system_health` session runs on every instance, unasked,
and writes `sp_server_diagnostics_component_result` events nobody reads. This release reads
them: worker and memory pressure, wait deltas, pending I/O, and the blocking the server
happened to be doing at a sampling instant — from a capture you already have.

### Added

- **`sqlferret export-health`** — a digest of a `system_health` capture, in `md`, `json` or
  `both`. Coverage first, then non-clean component states, wait deltas, memory movers,
  worker pressure, stability signals, I/O counters, worst pending I/O, and blocking seen
  inside diagnostics cycles.
- **Ingestion of `sp_server_diagnostics_component_result`** into eight `health_*` tables,
  with three outcomes counted separately: parsed, *unhandled* (a component this build does
  not model — `events`, and one per Always On availability group), and failed. Unhandled is
  not failure: a component set of five plus one per availability group would otherwise fire
  a parse error forever on any clustered instance.
- **Blocked-process reports lifted out of diagnostics cycles**, stored with
  `blocking_reports.source = 'diagnostics'` and counted as sub-documents rather than events.
  Every existing blocking query filters them out, so `export-blocking` still reports only
  threshold-triggered contention — the two are not comparable and are no longer mixed.

### The numbers this release refuses to print

Everything in the digest is either a delta over a printed span or a distribution, never a
total, because the counters behind them are **cumulative since instance start**. Measured on
a real capture: 147 cycles of one series, 146 rising transitions and zero falling. Summing
them would rank uptime, not activity.

For the same reason nothing is ranked on `maxWaitTime` (a frozen running maximum) or on
`averageWaitTime` (a millisecond-rounded running mean). They are carried and labelled as
instance-lifetime figures, and no ordering touches them.

A counter that goes backwards means the instance restarted inside the window. That interval
is dropped from the delta and counted, never netted off: a restart must not read as a quiet
period.

Counters scoped to a sampling interval — `intervalLongIos`, `intervalDumpRequests` — are
summed, not differenced, and suppressed entirely when the capture's cadence is irregular.

### Two things the digest tells you it cannot do

- **A capture folder can hold more than one session recording the same instance.** Observed:
  two, offset by 35 min 32 s on a 5-minute period. Nothing in the capture says which cycle
  belongs to which session, and every heuristic tried for it was wrong in a way that
  mattered. The digest reports that the cadence is irregular and suppresses the two
  interval-scoped counters rather than guessing.
- **`health_pending_io.file_path` is stored verbatim under every redaction mode**, `full`
  included, and no flag removes it. This is deliberate — a pending-I/O row without its file
  is not a finding — and the digest says so in a note whenever those rows are present.

### Changed

- `QueryNormalizer.Version` stays at 4; no reclassification is needed for this release.
- `docs/privacy.md` now enumerates every column stored outside the redaction policy, not
  a subset. That includes `blocking_processes.client_app`, `.host_name` and `.login_name`,
  which no redaction mode touches and never did — the System Health path makes those rows
  routine, since a diagnostics cycle records blocking without `blocked process threshold`
  being configured at all.

### Fixed

- **Markdown digests escape capture-derived text.** Both `export-health` and
  `export-blocking` interpolated strings taken from the capture — file paths, availability
  group names, wait types, and statement text — straight into Markdown. A backtick closed
  the code span, a pipe opened a column, a newline ended the table, and the rest rendered as
  Markdown: forged headings, fabricated metric rows, raw HTML, in the artifact meant to be
  shared. The ordinary case was enough on its own: a multi-line SQL statement broke its own
  bullet. Both renderers now share one escaper.
- **Diagnostics ingestion is bounded.** A capture whose events arrive less than a second
  apart closed no cycle, so nothing was flushed and the whole capture was held in memory
  while each new event re-sorted the buffer. Cycle boundaries are now detected in constant
  time against the latest timestamp seen, and a ceiling flushes the buffer even when no
  boundary appears.
- **The diagnostics XML parser refuses DTDs.** External entities already failed safe, but
  internal entities were expanded and the result stored — 300 bytes yielded 139 264
  characters, and the reader's cap bounds one document while a capture holds thousands.

### Security

The branch was reviewed by four independent external readers across two panels, plus a
fifth reading of the security diff. Twenty-one defects were found and fixed, five of them in
the fixes for earlier findings — which is the part worth keeping: a correction written under
review pressure gets less scrutiny than the original and carries a persuasive commit message.

## [0.2.0] - 2026-09-03

First published release, with binaries. Five platforms, self-contained: nothing to install,
no .NET runtime, no agent on the SQL Server.

### Added

- **`--sanitize-sql-text raw|literals`** (and `ingest.sqlTextSanitization` in the project
  config) — governs the statement text written to `executions.sql_text_raw` and
  `normalized_queries.normalized_sql`. `literals` collapses inlined literals to `?` and
  keeps identifiers, so a project can be analyzed, and shared, without carrying the values
  that were in the queries. The inner statement of a positional `sp_executesql` call is
  unwrapped first, so the query survives instead of collapsing into one opaque `?`.
  A double-quoted token collapses too: the capture never records the session's
  `QUOTED_IDENTIFIER` setting, so `"alice@example.com"` is ambiguous and fails safe.
  Bracketed identifiers are unaffected. See [docs/privacy.md](docs/privacy.md).
- **`sqlferret query`** — arbitrary SQL against a project, in `table`, `csv`, `json` or
  `md`. Non-writing is guaranteed by opening the connection read-only, never by inspecting
  the statement. 1000-row limit by default, `--no-limit` to lift it, truncation announced
  on `stderr`.
- **`sqlferret reclassify`** — replays the classifier over a project imported by an older
  normalizer version, in place, without re-importing the trace.
- **DDL classification** — roughly fifty statement kinds (`CREATE TABLE`,
  `ALTER TABLE ADD COLUMN`, `CREATE INDEX`, `ALTER PROCEDURE`, `DROP …`, `MERGE`,
  `FETCH`, …) and a new `normalized_queries.target_object` naming the sub-object a
  statement targets: column, constraint, index, procedure, function, trigger, cursor.
- **Estimated-plan capture refuses a sanitized run** — `SET SHOWPLAN_XML ON` against
  collapsed text would compile a different statement than the one that ran.
- **Release archives for five platforms** — `linux-x64`, `linux-arm64`, `osx-x64`,
  `osx-arm64`, `win-x64`, each carrying both the CLI and the TUI, cross-published from one
  runner and verified by unpacking and running the linux-x64 archive.
- **CI on push and pull request** — format, build with warnings as errors, tests.

### Changed

- **`QueryNormalizer.Version` is 4** (was 1). It versions normalization and classification
  together, so an existing project is reported as stale on `stderr` and `reclassify` is
  offered. Fingerprints from different normalizer versions are not comparable.
  The bump between 3 and 4 is deliberately conservative: neither the normalized text, nor
  the classifier's answer, nor the fingerprint changed. It forces the upgrade so projects
  pick up the new column, not because the shapes moved.
- **Statement text now obeys the stricter of the two privacy policies.** An input buffer is
  statement text, so `--sanitize-sql-text` governs it as much as `--redaction` does.
  `blocking_processes.inputbuf`, `blocking_reports.raw_xml` and
  `deadlock_reports.graph_xml` are kept verbatim only under `--redaction off` **and**
  `--sanitize-sql-text raw`. Composing in that direction means sanitization can never
  release what redaction was holding, so the default behaviour does not move.
- **`--redaction off --sanitize-sql-text literals` no longer retains the blocking or
  deadlock XML.** Those columns are whole XML documents whose `inputbuf` nodes carry the
  literals, and nothing here rewrites XML; retaining them would contradict the requested
  policy from the next column over. `export-events` therefore has nothing to export from
  such a run — import at `raw` if you need the XML.

### Fixed

- **`reclassify` no longer degrades a signature whose only retained sample is already
  normalized text.** `where c = ?` is not T-SQL: it does not parse, and reclassifying from
  it turned rows into `OTHER`. Two provenances produce such a sample, and only the
  provenance can tell — never the text itself: an execution from a run imported at
  `literals`, and a blocking input buffer from any run not imported with `--redaction off`.
  The second predates the sanitization feature and affected every normally-imported
  project. A usable sample now wins over a normalized one for the same signature, and what
  is left is counted apart, as `unusableSample`, and left untouched.
- **A double-quoted value in a blocking input buffer survived into storage.** The blocking
  path stored the plain normalized text rather than the variant that also collapses a
  double-quoted token, so `"alice@example.com"` under `SET QUOTED_IDENTIFIER OFF` reached
  both `blocking_processes.inputbuf` and `normalized_queries.normalized_sql`, under any
  policy.
- **`project.json` recorded `ToolVersion: 1.0.0.0`** — an SDK default nobody chose — for
  every project ever created, so the provenance field said nothing.

[0.3.0]: https://github.com/rudi-bruchez/sqlferret/releases/tag/v0.3.0
[0.2.0]: https://github.com/rudi-bruchez/sqlferret/releases/tag/v0.2.0
