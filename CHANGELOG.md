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

[0.2.0]: https://github.com/rudi-bruchez/sqlferret/releases/tag/v0.2.0
