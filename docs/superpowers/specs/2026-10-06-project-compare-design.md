# Design: comparing two projects (`sqlferret compare`)

Status: proposed, revision 3
Date: 2026-10-06
Scope: `SqlFerret.Core` (Analysis) and the CLI host. No schema change, no ingestion change.

Revision 3 follows the review of the implementation plan, whose five readers executed the code
and found that the report-time sanitization of revision 2 destroys almost every text:
`QueryNormalizer.Normalize` parses with ScriptDom and flags a failure on any parse error, and a
stored normalized text carries `?` for every literal, which does not parse. Every such text came
out as the placeholder (measured by all five readers). §7 now decides per hash which stored text
may be printed, from the policy of the run that wrote it, without parsing anything. Smaller
changes are marked [R3]: executions without a duration (§6), `--out` inside a project (§3), a
bare `--database` (§3), the generation check reads the runs that stored executions (§5), the
plan median and the unlinked count (§10), and the markdown carries every coverage field (§11).

Revision 2 followed a review panel of five readers (agy and codex, each with a directive and a
neutral prompt, and a Claude subagent). Revision 1 is in the git history. What changed, marked
[R2] where it lands:

- the text-choice rule of revision 1 could print a literal from a project imported with
  `literals`: `normalized_queries` keeps the first text written for a hash, so a project's policy
  says nothing about a given row. Printed text is now sanitized at report time (§7, §12);
- the plan fallback printed `plan_profiles.statement_text`, which no policy sanitizes. Removed
  (§10);
- the per-hour denominator spanned the gaps between imports. It is now the sum of per-run spans,
  and the largest gap inside a run is reported (§6, §8);
- the version check read `normalized_queries.normalizer_version`, which `reclassify` rewrites
  without recomputing any hash. It now reads `ingestion_runs.normalizer_version`, and
  `reclassify` is no longer offered as the remedy (§5);
- `ATTACH` refuses a bound parameter on the embedded engine (measured by three readers). The path
  is interpolated, which needs an explicit exception to the SQL safety rule of `CLAUDE.md` (§4);
- a project held open by another process, a running TUI included, cannot be attached (§4);
- a project that predates the schema migrations is refused before any section runs (§5);
- the decimal-to-hex link must pad to sixteen digits, and the test pair revision 1 promised does
  not exist (§10);
- multi-statement plans are excluded from §10 rather than keyed on their first statement;
- the minimum-duration heuristic for capture predicates is dropped (§6);
- a zero base average no longer produces an infinite ratio (§7);
- the host must not call `AuditProject.OpenOrCreate`, which writes into the project (§3).

---

## 0. Evidence base

The idea comes from SQL Nexus 8.26.09.24, whose MCP server exposes a `compare_nexus_databases`
tool: a side-by-side comparison of two imported collections, including query performance when
both carry ReadTrace data (documented: `SqlNexus.McpServer/README.md` in `microsoft/SqlNexus`).
Nothing below depends on how SQL Nexus implements it.

Claims in this document are of three kinds, and each says which:

- read in this repository, with the file named;
- measured, with what was run;
- unverified, said plainly.

No claim here concerns SQL Server behavior beyond what `docs/execution-plans.md` and
`docs/data-model.md` already document.

Measured on 2026-10-06 with the DuckDB CLI v1.5.4 (the project embeds DuckDB.NET 1.5.3, so this
is close but not identical, and a test must confirm it on the embedded engine, see §13):

- an in-memory connection can `ATTACH '<file>' AS base (READ_ONLY)` and `ATTACH '<file>' AS
  target (READ_ONLY)`, and a `FULL OUTER JOIN` between `base.t` and `target.t` returns the rows
  present on either side;
- an `INSERT INTO base.t` on such an attachment fails with `Cannot execute statement of type
  "INSERT" on database "base" which is attached in read-only mode`;
- the same file can be attached twice under two aliases, so a project can be compared with
  itself.

[R2] Confirmed on the embedded DuckDB.NET 1.5.3 by the codex and Claude readers, who also
measured what revision 1 did not:

- `ATTACH $p AS base (READ_ONLY)` fails with `Parser Error: syntax error at or near "$"`: the path
  cannot be a bound parameter;
- a path containing a single quote attaches when the quote is doubled;
- a file held open by another process cannot be attached, even read-only: `IO Error: Could not set
  lock on file ...: Conflicting lock is held`. Reproduced here with two DuckDB CLI processes; the
  attach succeeds again once the other process exits. An open connection in the same process does
  not block it;
- the attachments are read-only, the in-memory database is not: `CREATE TABLE` on the main
  database succeeds. The guarantee is about the project files, which is what matters;
- after a read-only attach both files are byte-identical and no `.wal` is created.

---

## 1. Problem

An audit regularly needs the answer to "what changed between these two captures". Four
situations were named as real uses:

1. before and after a fix on the same server (an index, a rewrite, a setting);
2. before and after a migration (SQL Server version, compatibility level, hardware, Azure);
3. a normal period and a slow period of the same server;
4. the same application on two servers (production and test, or two customers).

Today the only way is to run `top-slow` or `query` on each project and compare by eye or by
hand-written SQL. Nothing checks that the two projects are comparable, and the totals of two
captures of different lengths are not comparable at all.

## 2. Intent and success criteria

`sqlferret compare` reads two existing projects, never modifies either, and emits a digest that
answers, in this order:

1. are these two projects comparable, and over what windows (coverage, §6);
2. which statements cost more, or less, per execution (§7);
3. where the server spends more, or less, of its time per hour of capture (§8);
4. which statements appear in only one project (§9);
5. for statements with captured plans, did the plan change, and which findings appeared or
   disappeared (§10).

Success means: a reader who knows `top-slow` and `export-health` can read the digest without
documentation; two identical projects produce a digest with no regression, no gain, no
appearance and no plan change; and a comparison that cannot be trusted (§5) is refused or flagged,
never silently printed.

## 3. Command surface

```text
sqlferret compare --base <dir> --target <dir>
                  [--database <name>]
                  [--format json|md|both] [--out <file>] [--limit <n>]
```

| Flag | Default | Meaning |
|---|---|---|
| `--base` | required | The reference project (before, the normal period, the reference server). |
| `--target` | required | The project compared against it. Deltas and ratios read as target relative to base. |
| `--database` | all | Restrict executions on both sides to one `database_name`; plans through their link to executions (§10). |
| `--format` | `md` | `json`, `md` or `both`, with the same rules as `export-health`: any other value exits 1. |
| `--out` | stdout | With `both`, writes `<stem>.md` and `<stem>.json`. Rejects `..`, like `export-health`, and [R3] any path inside either project directory, which would break the promise of §4. Paths are compared after resolving symbolic links. A write failure (missing directory, no permission) exits 1 with its message. |
| `--limit` | 10 | Rows per ranked section. Must be a positive integer. |

[R3] A flag given without a value (`--database` last on the line, or followed by another flag) is
an error, not "all databases".

`--base` and `--target` are project directories, like `--project` everywhere else, but
`compare` never creates one: a directory without `sqlferret.duckdb` is an error (exit 1). The two
may be the same directory.

Exit codes follow `docs/cli-reference.md`: `0` success, `1` any error, including a refusal under
§5.

Durations are formatted with the base project's `display.durationUnit`, through `DisplayFormat`,
in the host. Core returns microseconds only.

[R2] The host reads that setting with `SqlFerretConfig.Load` on the base project's
`sqlferret.config.json`, and nothing else. It must not call `AuditProject.OpenOrCreate`, which
every other command uses: that call writes `project.json` and creates `README.md`, `plans/` and
`exports/` when absent (`AuditProject.cs`), which would break the promise that `compare` writes
nothing into either project. A missing config file means the defaults.

The usage line in `src/SqlFerret.Cli/Program.cs` gains the command, and `docs/cli-reference.md`
gains a section. The CLI count in `CLAUDE.md` goes from nine to ten.

## 4. Core

One new class, `SqlFerret.Core.Analysis.ProjectComparison`, and its result records in a new
`CompareResults.cs`, following `HealthDigest` / `HealthResults.cs`.

```csharp
public sealed class ProjectComparison(string baseDbPath, string targetDbPath)
{
    public const int SchemaVersion = 1;
    public CompareDigestResult Run(CompareOptions options);
}

public record CompareOptions(int Limit, string? Database, CompareThresholds Thresholds);

public record CompareThresholds(
    int MinExecutions = 5,               // per side, for §7 and §8 rankings
    long MinAvgDurationUs = 1_000,       // §7: below this on both sides, a ratio is noise
    long MinActiveSpanUs = 600_000_000); // §6 and §8: below 10 minutes, per-hour is not computed

public record CompareDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, CompareDigestResult Digest);
```

`Run` opens one in-memory `DuckDBConnection` (`Data Source=:memory:`), attaches the two files as
`base` and `target` with `(READ_ONLY)`, runs every section as SQL, and disposes the connection.
It does not go through `DuckDbProject.OpenReadOnly`, which opens a single file.

All aggregation is DuckDB SQL, per the KISS rule in `CLAUDE.md`. C# reads rows into records and
does no reduction.

### SQL safety [R2]

`ATTACH` cannot take a bound parameter (measured, §0). The path is interpolated as a string
literal with single quotes doubled, the escaping `FilterCompiler` already uses; DuckDB string
literals do not treat a backslash as an escape, so doubling the quote is the whole escaping
(measured by the agy reader with a Windows-style path, and by codex with a backslash and a
newline on Linux; macOS and Windows themselves are unverified).

This is user-supplied text interpolated into SQL, which the SQL safety invariant of `CLAUDE.md`
does not allow today: it permits interpolation of allow-listed identifiers only. Implementing this
design therefore requires an explicit, narrow exception added to that invariant, worded as: "the
path of an `ATTACH` in `ProjectComparison`, resolved by the host to an existing file, escaped by
doubling single quotes". That amendment is a decision for the maintainer, and this spec does not
make it.

The aliases `base` and `target` are constants. `--database` is a bound parameter. `--limit` and
the thresholds are integers.

### Locking [R2]

A project held open by another process cannot be attached (§0), and the TUI keeps its project
open read-write for its whole session (`src/SqlFerret.Tui/Program.cs`, `ap.OpenDb()`). The same
is already true of every other command, which opens the project read-write. `Run` catches the
lock error and reports, naming the project, that another SQLFerret process (a TUI, an import) has
it open and must be closed first. Exit 1.

### Read-only guarantee

Both attachments are `READ_ONLY`, so the engine refuses any write to either project file
(measured, §0); the in-memory main database stays writable and is never used for writes.
`compare` does not touch `project.json`, does not write provenance, and does not create anything
in either project directory (§3). A test checks that both project directories, every file and its
SHA-256, are identical before and after a CLI run (§13).

## 5. Preconditions and refusals

Checked before any section runs, in this order:

| Check | Source | On failure |
|---|---|---|
| Both directories contain `sqlferret.duckdb` | file system | exit 1, names the missing one |
| Both files can be attached | `ATTACH` | exit 1, the lock message of §4 |
| Both schemas carry every table and column the sections read [R2] | `duckdb_columns()` filtered on `database_name` | exit 1, names the project and the missing column, and says that running any other command once on it (`top-slow`) migrates it |
| Each side has at least one execution after the `--database` filter | `executions` | exit 1, names the empty side |
| The fingerprints of the two sides are comparable [R2] | `SELECT DISTINCT normalizer_version FROM ingestion_runs` on each side | exit 1, prints the versions found, says the older capture must be re-imported |

The column check runs before the others because a project imported before a migration fails with
a binder or catalog error otherwise (measured by the Claude reader on a file built to the old
shape: `Referenced column "sql_text_policy" not found`, `Table with name plan_profiles does not
exist`). `compare` attaches read-only, so the `ADD COLUMN IF NOT EXISTS` migrations of
`DuckDbProject.Open` never run. Whether such a project exists in practice is unverified.

[R2] The fingerprint check reads `ingestion_runs.normalizer_version`, the version that computed
the hashes stored by each run. Revision 1 read `normalized_queries.normalizer_version`, which
`Reclassifier` rewrites to the current version without recomputing any hash (measured by the codex
and Claude readers: after a reclassify, `normalized_queries` says 4 and `ingestion_runs` still
says 3). `reclassify` therefore cannot be the remedy for a fingerprint mismatch, and is not
offered.

The version number covers classification as well as normalization (`CLAUDE.md`), so two versions
can produce identical fingerprints. The comment on `QueryNormalizer.Version` states that v4 left
`NormalizedSql`, and so the fingerprint, unchanged from v3. The check therefore compares
fingerprint generations rather than raw versions: a constant in `ProjectComparison` maps each
version to its generation, with v3 and v4 in the same one and the comment cited next to it. A
future version that changes the token rewriting starts a new generation. Every run that stored
executions, on both sides, must belong to one generation. [R3] A run without executions (a
`system_health` import, a capture of blocking reports only) stored no execution hash, so its
version does not bear on what §7 to §10 compare, and it is not checked. Versions below 3 are their own generations, since nothing in the
repository says otherwise.

Classification can still differ between two comparable sides. `statement_kind` and
`primary_table` are taken from the target.

`--sanitize-sql-text` does not affect comparability of the hash. In `IngestionService.cs` the
normalizer runs first, then `SqlTextSanitizer.Apply` returns `nq with { NormalizedSql = ... }`
(`SqlTextSanitizer.cs`, lines 59 to 78): only `NormalizedSql` is replaced, and `NormalizedHash`
is kept. Two projects imported under different redaction or sanitization policies are therefore
comparable.

But the stored `normalized_queries.normalized_sql` does differ: at `literals`, double-quoted
tokens are collapsed too (`QiCollapsedSql`), and a tokenize failure stores a placeholder. The same
hash can carry two different texts, one per side, and within one project the text of a hash is
the one written first (`ON CONFLICT (normalized_hash) DO UPDATE SET last_seen_at`, in
`DuckDbProject`), whatever the policy of later runs. §7 handles what is printed.

## 6. Coverage

Printed first, like `export-health`, because every number after it depends on it. For each side:

- project directory;
- runs: one line per `ingestion_runs` row with executions, giving its first and last
  `captured_at` and its span [R2];
- active span: the sum of the per-run spans, in microseconds [R2];
- largest gap between two consecutive executions inside one run, with the run it belongs to [R2];
- executions and distinct `normalized_hash`, after the `--database` filter;
- databases seen (distinct `database_name`, capped at the limit, with a count of the rest);
- `normalizer_version`;
- redaction and SQL text policies, from `ingestion_runs.redaction_policy` and `sql_text_policy`;
- smallest `duration_us` observed, displayed for the reader without any conclusion drawn [R2];
- share of executions with a non-null `query_hash`, and number of `plan_profiles` rows.

Then the notes, each emitted only when it applies:

- an active span under `MinActiveSpanUs` on either side: per-hour figures are not computed (§8);
- a run whose own largest gap exceeds a quarter of its own span: the run probably holds several
  disjoint captures (a folder import), so its per-hour figures understate the load; one note per
  such run, naming it [R2]. Each run is measured against itself: the run holding the largest
  absolute gap is not necessarily the one split [R3];
- always: SQLFerret does not know the predicates of the sessions that produced the captures. Two
  captures with different duration thresholds have per-hour loads that cannot be compared, and
  nothing in a project says so [R2];
- no eligible `plan_profiles` on one side: §10 is skipped.

[R2] Revision 1 inferred different predicates from a factor of ten between the smallest observed
durations. Two readers showed that the minimum is a property of the workload as much as of the
filter, and undefined at zero. Dropped.

All spans and gaps are computed after the `--database` filter.

[R3] Every section reads executions with a non-null `normalized_hash` and a non-null
`duration_us`. An execution without a duration (the Claude reader found that `*_starting` events
map with a NULL duration, which made a `GetInt64` throw) cannot be ranked by cost or load, so it
is left out everywhere, and the coverage block counts those left out on each side. Whether real
captures contain such rows is unverified.

## 7. Cost per execution

For each `normalized_hash` present on both sides with at least `MinExecutions` executions on each:
count, average and p95 of `duration_us`, average `cpu_time_us`, average `logical_reads`, per
side, and the ratio target over base of the average duration.

Eligible for ranking only if the average duration on at least one side reaches
`MinAvgDurationUs`. Two rankings of `--limit` rows each:

- regressions: ratio descending, ratio above 1;
- gains: ratio ascending, ratio below 1.

[R2] A zero base average makes the ratio infinite (`ieee_floating_point_ops` is on in the
embedded engine, measured by the Claude reader), which `System.Text.Json` refuses to serialize.
The ratio is NULL when the base average is zero, and such rows rank first among regressions, in
a separate ordering on the target average.

The p95 is `quantile_cont(duration_us, 0.95)`, as in `WorkloadQueries.QueryStats`. Each row
carries `normalized_hash`, and `statement_kind` and `primary_table` from the target side (§5).

### The printed text [R3]

Revision 1 chose a side's text by the policy of its project. Three readers showed it leaks: a
project whose first run was `raw` keeps the raw-run text of every hash it saw then, because both
writers of `normalized_queries` (the execution upsert and the blocking-process insert,
`DuckDbProject.cs`) keep the first text and only move `last_seen_at`. A policy is per run; the
text is per hash, written once.

Revision 2 re-normalized every printed text. Five readers showed that this destroys it: a stored
text carries `?` for its literals, ScriptDom does not parse `?`, and `TokenNormalizer` reports any
parse error as a tokenize failure, so the result was the placeholder (measured, see the header).

The rule is now decided per hash, from where its stored text came from, and parses nothing:

1. If every run on both sides is `raw` (a NULL `sql_text_policy`, from a run older than the
   column, counts as `raw`), every text is printed as stored, from the target, or from the base
   for a hash the target lacks.
2. Otherwise, for each side, the origin run of a hash is the smallest `run_id` among the runs of
   that side that hold the hash, in `executions.normalized_hash` or in
   `blocking_processes.inputbuf_fingerprint` (joined to `blocking_reports.run_id`). Runs are
   numbered in import order (`DuckDbProject.BeginRun`), and the first import that met a hash is the
   one whose text the upsert kept.
3. A side's text for that hash is trusted when its origin run's `sql_text_policy` is `literals`.
   The printed text is the target's if trusted, else the base's if trusted, else the literal
   `(text withheld: first imported under raw)`.

The rule is SQL over both attachments, computed once per printed hash. It never prints a text
whose origin run stored literals, under one assumption: that `normalized_queries` is only ever
written by those two inserts, both first-wins. A future writer that updates `normalized_sql` must
revisit it. [R3] It is conservative rather than exact: the blocking writer stores the collapsed
text whenever redaction is not `off`, whatever the text policy, so a hash first met in a `raw`
run's blocking report under `masked` holds a safe text that the rule still withholds (measured by
the codex directive reader through `IngestionService`). Reading the redaction policy as well
would recover it; the gain was judged not worth a second rule.

Two consequences, stated in the coverage block when they apply: a project imported `raw` and then
`literals` shows the withheld text for the statements its first import saw; and comparing an
all-`raw` project with a `literals` one shows the `literals` side's text where it exists and
withholds the rest.

## 8. Load per hour of capture

Computed only when both active spans reach `MinActiveSpanUs`. For each `normalized_hash` on both
sides: executions per hour and total `duration_us` per hour, each divided by that side's active
span (§6). [R2] Revision 1 divided by the span from the first to the last execution of the
project, which counts the idle time between two imports: codex measured two one-minute runs a day
apart, where that denominator gave 1,441 minutes and the active span 2. Per-hour figures are
rates in microseconds per hour, not a unit conversion; the host formats them.

Two rankings of `--limit` rows each, on the difference of total duration per hour (target minus
base): the largest increases and the largest decreases. Rows require `MinExecutions` on at least
one side. A statement can appear here and not in §7, for example when it became ten times more
frequent at the same speed. That is the reason the two sections are kept apart.

## 9. Appeared and disappeared

`normalized_hash` present on one side only, after the `--database` filter. Two lists of `--limit`
rows: appeared (target only) and disappeared (base only), each ranked by total duration per hour
on the side where it exists, or by total duration when §8 is not computed. Each list also prints
its full count, so a truncated list is visible as such.

## 10. Plans

Keyed on `plan_profiles.query_hash`, which comes from the plan XML and is present even when the
capture omitted the `sqlserver.query_hash` action (`docs/data-model.md`).

[R2] Only single-statement plans take part: `plan_hash_source = 'queryplanhash'`.
`PlanProfileParser` takes `query_hash` from the first `<StmtSimple>` (`PlanProfileParser.cs`,
line 38), while `PlanIdentity` hashes every statement of a multi-statement plan and
`PlanFindings` scans the whole XML. Keyed on the first statement, a change in the second would be
reported against the first. The coverage block counts the multi-statement and content-hashed
profiles left out on each side.

Skipped, with a note, when either side has no eligible `plan_profiles` row.

For each `query_hash` present in the plan profiles of both sides:

- the set of distinct `plan_hash` per side, and whether the sets differ;
- the set of distinct finding `kind` per side, from `plan_findings` joined on `plan_profile_id`,
  and the kinds that appeared (target only) and disappeared (base only);
- the median `duration_us` of the profiles per side, for context. [R3] Computed over the profiles
  before the join to `plan_findings`; joined after it, a profile with three findings counted three
  times (measured by the codex and Claude readers).

Listed: the `query_hash` values whose plan set changed or whose finding kinds changed, ordered by
the target median duration descending, `--limit` rows, plus the full count.

A plan row links to a §7 or §8 row only when the executions on that side carry a `query_hash`.
`docs/data-model.md` documents that `executions.query_hash` is typically a decimal `UInt64` while
`plan_profiles.query_hash` is bare hex.

[R2] The conversion is `printf('%016X', TRY_CAST(query_hash AS UBIGINT))`, compared with
`lpad(upper(plan_profiles.query_hash), 16, '0')`. Plain `hex()` drops leading zeros, so one hash
in sixteen would silently fail to join (measured on the embedded engine by the Claude reader with
`0A1B2C3D4E5F6071`, and by agy and codex on the CLI). `TRY_CAST` because a plain cast of a value
that does not parse as `UBIGINT` aborts the whole query. Whether the showplan `QueryHash`
attribute is itself always sixteen digits is unverified; padding the plan side covers it either
way. Revision 1 promised a test on "a real pair from the existing plan tests"; no such pair exists
(the plan tests use one hex hash, the execution-side fixture uses `0xAABB`). The test builds its
own pair, one with a leading zero.

When no execution carries `query_hash`, a plan row shows its `query_hash`, its plan hashes and its
finding kinds, and no statement text. [R2] Revision 1 printed `plan_profiles.statement_text`
here, which `docs/privacy.md` lists as not covered by `--sanitize-sql-text`: a project imported
with `literals` would have leaked literals through it.

[R2] With `--database`, only plans linked through `query_hash` to an execution of that database
are kept; unlinked plans are left out and counted, since `plan_profiles` has no database column.
[R3] The count is its own query: computed inside the changed-row query, it read zero whenever no
row changed, which is the case it exists to explain.
Without the action on the completion events, `--database` therefore empties §10, and the note
says why.

## 11. Output

`--format md` renders through a new `src/SqlFerret.Cli/CompareDigestMarkdown.cs`, built like
`HealthDigestMarkdown.cs`, with `MarkdownText` for escaping. `json` serializes
`CompareDigestEnvelope`. `both` behaves as in `export-health`.

The markdown order is §6 to §10. [R3] It carries every field §6 lists, the per-run lines and the
largest gap included, and every measure of a §7 row, CPU and logical reads included; revision 2's
renderer dropped them. An empty ranking prints a sentence that says it is empty (no
regression above the thresholds, for instance), so a quiet result does not look like a failure,
as in `export-health`.

## 12. Privacy

The digest prints `normalized_sql`, which `top-slow` already prints, and never `sql_text_raw` or
parameter values, and never `plan_profiles.statement_text`. Unless every run on both sides is
`raw`, a text is printed only when the run that wrote it was `literals`, and withheld otherwise
(§7). Comparing two customers' projects puts
both in one report; the coverage block names both directories so the reader knows what the file
contains before sharing it.

## 13. Testing

Tests build small projects through the existing insert paths (as `DuckDbProjectInsertTests` does),
with fixture names from the anonymous vocabulary in `CLAUDE.md` (`AppDb`, `AppSchema`,
`WidgetRecalc`). No `.xel` and no SQL Server needed. New class `ProjectComparisonTests`, plus
`CompareDigestMarkdownTests` and `CliCompareTests`.

1. A project compared with itself: no regression, no gain, no appeared, no disappeared, no plan
   change. This also confirms the double attach on the embedded engine.
2. Refusals: missing database file; a project missing a migrated column; fingerprints of two
   generations on one side; different generations across sides; a project reclassified from an
   older generation still refused; v3 against v4 accepted. Each refusal exits 1 with its message.
3. Read-only: every file and its SHA-256, in both project directories, identical before and after
   a CLI run, which also proves the host never calls `OpenOrCreate`.
4. A path containing a single quote is attached correctly; a project open read-write in another
   process gives the lock message.
5. §7: a statement three times slower in target ranks first in regressions; one under
   `MinExecutions` or under `MinAvgDurationUs` on both sides does not rank; a zero base average
   yields a NULL ratio and serializes to JSON.
6. §8: a statement at the same speed but ten times more frequent ranks in load increases and not
   in §7.
7. §9: appeared and disappeared lists and their counts.
8. §6 and §8: an active span under `MinActiveSpanUs` suppresses §8 with a note; two runs a day
   apart use the sum of their spans; a run with a large internal gap gets its note.
9. Printed text, with fixtures stored through `SqlTextSanitizer.Apply` as ingestion does: a
   project whose first run is `raw` and second `literals`, with a double-quoted value, compared
   with a `literals` project: no section, in md or json, contains the value, and the `literals`
   side's text is printed; the same hash present only in the raw-first project is withheld; two
   all-`raw` projects print the stored text; a hash first met in a blocking report of a `raw` run
   is withheld.
10. §10: no eligible plan profiles on one side skips the section; a multi-statement plan is
   excluded and counted; a changed `plan_hash` and an appeared `spill_to_tempdb` kind are listed;
   the decimal to hex link with a hash that has a leading zero; no statement text printed.
11. `--database` filters executions on both sides, and plans through the `query_hash` link.
12. CLI: missing `--base` or `--target`, bad `--format`, bad `--limit`, `--out` with `..`.

Each test is written failing first.

## 14. Out of scope

- The TUI. A view can come later on top of the same Core result.
- Blocking, deadlocks, health and Query Store snapshots.
- An operator by operator diff of two plans.
- Time windows inside a project (`--from`, `--to`): one capture is one window.
- Matching across normalizer versions, or fuzzy matching of statements whose hash differs.
- A `query` recipe with `ATTACH`: whether `sqlferret query`, opened read-only, accepts an
  `ATTACH` is unverified, and is not needed for this command.

## 15. Open questions

1. The SQL safety exception of §4 must be approved and written into `CLAUDE.md` before any code.
2. Are the default thresholds (`MinExecutions` 5, `MinAvgDurationUs` 1 ms, `MinActiveSpanUs` 10
   minutes, the quarter-of-span gap note) right? They are guesses, to be checked on two real
   projects before release, and exposed as flags only if that check shows a need.
3. Should §7 also rank on p95 rather than only on the average? The average is chosen because it
   is stable at low counts; p95 is displayed.

## 16. Review record

Findings of the revision 1 panel that were not adopted, and why:

- "`MinAvgDurationUs` defaults to 1 µs, not 1 ms": the signature in §4 says `1_000`; the reader
  misread the open question.
- "Per-hour rates and minute thresholds break the microseconds invariant": thresholds are now in
  microseconds; a rate per hour is an analytical measure, not a unit conversion, and is formatted
  by the host (§8).
- "Attaching a file open elsewhere works": true only in the same process; another process blocks
  it (§0, §4). The reader that reported it as not a problem had tested one process.
- "Text from blocking input buffers is QI-collapsed whatever the policy": true, and covered by the
  report-time sanitization of §7 without a rule of its own.

Findings of the second plan panel (on plan revision 2) that were not adopted, and why:

- "The active span is summed in C#, against the rule that aggregation lives in SQL": the sum runs
  over the per-run rows that SQL already aggregated, a handful of values kept for display; the
  rule targets reductions over event data.
- "`epoch_us` is not in the evidence base": every span and gap test exercises it on the embedded
  engine, and passes.
- "The display unit comes only from the base project's config": deliberate, the command opens no
  project through `AuditProject` (§3); the reference page says so.
