# Design: comparing two projects (`sqlferret compare`)

Status: proposed, revision 1
Date: 2026-10-06
Scope: `SqlFerret.Core` (Analysis) and the CLI host. No schema change, no ingestion change.

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
| `--database` | all | Restrict executions on both sides to one `database_name`. Does not apply to plans (§10). |
| `--format` | `md` | `json`, `md` or `both`, with the same rules as `export-health`: any other value exits 1. |
| `--out` | stdout | With `both`, writes `<stem>.md` and `<stem>.json`. Rejects `..`, like `export-health`. |
| `--limit` | 10 | Rows per ranked section. Must be a positive integer. |

`--base` and `--target` are project directories, like `--project` everywhere else, but
`compare` never creates one: a directory without `sqlferret.duckdb` is an error (exit 1). The two
may be the same directory.

Exit codes follow `docs/cli-reference.md`: `0` success, `1` any error, including a refusal under
§5.

Durations are formatted with the base project's `display.durationUnit`, through `DisplayFormat`,
in the host. Core returns microseconds only.

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
    int MinExecutions = 5,          // per side, for §7 and §8 rankings
    long MinAvgDurationUs = 1_000,  // §7: below this on both sides, a ratio is noise
    double MinSpanMinutes = 10);    // §6 and §8: a shorter window is flagged, per-hour is not computed

public record CompareDigestEnvelope(int SchemaVersion, DateTime GeneratedAt, CompareDigestResult Digest);
```

`Run` opens one in-memory `DuckDBConnection` (`Data Source=:memory:`), attaches the two files as
`base` and `target` with `(READ_ONLY)`, runs every section as SQL, and disposes the connection.
It does not go through `DuckDbProject.OpenReadOnly`, which opens a single file.

All aggregation is DuckDB SQL, per the KISS rule in `CLAUDE.md`. C# reads rows into records and
does no reduction.

### SQL safety

`ATTACH` takes the path as a string literal. Whether DuckDB.NET binds a parameter in that position
is unverified; the implementation tries a bound parameter first, and if the engine refuses it,
interpolates the path with single quotes doubled, the same escaping `FilterCompiler` uses. Either
way the path is the full path resolved by the host from an existing directory plus
`sqlferret.duckdb`, never raw user text. The aliases `base` and `target` are constants.
`--database` is a bound parameter. `--limit` and the thresholds are integers.

### Read-only guarantee

Both attachments are `READ_ONLY`, so the engine refuses any write (measured, §0). `compare` does
not touch `project.json`, does not write provenance, and does not create `exports/` in either
project. A test checks that both database files are byte-identical before and after a run (§13).

## 5. Preconditions and refusals

Checked before any section runs, in this order:

| Check | Source | On failure |
|---|---|---|
| Both directories contain `sqlferret.duckdb` | file system | exit 1, names the missing one |
| Each project has exactly one `normalizer_version` | `SELECT DISTINCT normalizer_version FROM normalized_queries` | exit 1, suggests `sqlferret reclassify --project <dir>` |
| The two versions are equal | same | exit 1, prints both versions, suggests `reclassify` on the older one |
| Each side has at least one execution after the `--database` filter | `executions` | exit 1, names the empty side |

The version check is the important one. `CLAUDE.md` states that fingerprints across normalizer
versions are not comparable, and `normalized_queries.normalizer_version` is documented in
`docs/data-model.md` as the column whose bump "invalidates comparability across projects". A
comparison across versions would report every statement as both disappeared and appeared.

`--sanitize-sql-text` does not affect comparability of the hash. In `IngestionService.cs` the
normalizer runs first, then `SqlTextSanitizer.Apply` returns `nq with { NormalizedSql = ... }`
(`SqlTextSanitizer.cs`, lines 59 to 78): only `NormalizedSql` is replaced, and `NormalizedHash`
is kept. Two projects imported under different redaction or sanitization policies are therefore
comparable.

But the stored `normalized_queries.normalized_sql` does differ: at `literals`, double-quoted
tokens are collapsed too (`QiCollapsedSql`), and a tokenize failure stores a placeholder. The same
hash can carry two different texts, one per side. §7 handles which text is printed.

## 6. Coverage

Printed first, like `export-health`, because every number after it depends on it. For each side:

- project directory;
- window: first and last `executions.captured_at`, and the span in minutes;
- executions and distinct `normalized_hash`, after the `--database` filter;
- databases seen (distinct `database_name`, capped at the limit, with a count of the rest);
- `normalizer_version`;
- redaction and SQL text policies, from `ingestion_runs.redaction_policy` and `sql_text_policy`;
- smallest `duration_us` observed;
- share of executions with a non-null `query_hash`, and number of `plan_profiles` rows.

Then the notes, each emitted only when it applies:

- a span under `MinSpanMinutes` on either side: per-hour figures are not computed (§8);
- the smallest observed durations differ by more than a factor of ten: the two capture sessions
  probably used different duration predicates, so per-hour load is not comparable. SQLFerret does
  not know the predicates of the session that produced a `.xel`; this heuristic is the only signal
  it has, and the note says so;
- no `plan_profiles` on one side: §10 is skipped;
- `--database` given: plans are not filtered (§10).

## 7. Cost per execution

For each `normalized_hash` present on both sides with at least `MinExecutions` executions on each:
count, average and p95 of `duration_us`, average `cpu_time_us`, average `logical_reads`, per
side, and the ratio target over base of the average duration.

Eligible for ranking only if the average duration on at least one side reaches
`MinAvgDurationUs`. Two rankings of `--limit` rows each:

- regressions: ratio descending, ratio above 1;
- gains: ratio ascending, ratio below 1.

The p95 is `quantile_cont(duration_us, 0.95)`, as in `WorkloadQueries.QueryStats`. Each row
carries `normalized_hash`, `statement_kind` and `primary_table` from the target side, and one
`normalized_sql` chosen as follows (§5 explains why the two can differ): if either side's
`ingestion_runs.sql_text_policy` is `literals`, the text of that side; otherwise the target's.
The rule applies to every section that prints a statement (§8, §9). It keeps a report that
involves a sanitized project from printing the less sanitized text of the other one. A project
with several runs under different policies counts as `literals` if any of its runs is.

## 8. Load per hour of capture

Computed only when both spans reach `MinSpanMinutes`. For each `normalized_hash` on both sides:
executions per hour and total `duration_us` per hour, each divided by that side's span in hours.

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
capture omitted the `sqlserver.query_hash` action (`docs/data-model.md`). `PlanProfileParser`
takes it from the first `<StmtSimple>` of the plan (`PlanProfileParser.cs`, line 38), so a
multi-statement plan is keyed on its first statement; the section says so in its header.

Skipped, with a note, when either side has no `plan_profiles` row.

For each `query_hash` present in the plan profiles of both sides:

- the set of distinct `plan_hash` per side, and whether the sets differ;
- the set of distinct finding `kind` per side, from `plan_findings` joined on `plan_profile_id`,
  and the kinds that appeared (target only) and disappeared (base only);
- the median `duration_us` of the profiles per side, for context.

Listed: the `query_hash` values whose plan set changed or whose finding kinds changed, ordered by
the target median duration descending, `--limit` rows, plus the full count.

A plan row links to a §7 or §8 row only when the executions on that side carry a `query_hash`.
`docs/data-model.md` documents that `executions.query_hash` is typically a decimal `UInt64` while
`plan_profiles.query_hash` is bare hex. The link converts the decimal to hex in SQL; this
conversion is the part of the design most likely to be wrong, and its test uses a real pair of
representations from the existing plan tests rather than an invented one. When no execution
carries `query_hash`, the plan rows print the plan `statement_text` (truncated by the engine, so
display only) and no link.

`--database` does not filter plans: `plan_profiles` has no database column.

## 11. Output

`--format md` renders through a new `src/SqlFerret.Cli/CompareDigestMarkdown.cs`, built like
`HealthDigestMarkdown.cs`, with `MarkdownText` for escaping. `json` serializes
`CompareDigestEnvelope`. `both` behaves as in `export-health`.

The markdown order is §6 to §10. An empty ranking prints a sentence that says it is empty (no
regression above the thresholds, for instance), so a quiet result does not look like a failure,
as in `export-health`.

## 12. Privacy

The digest prints `normalized_sql`, which `top-slow` already prints, and never `sql_text_raw` or
parameter values. When the two projects were imported under different SQL text policies, the
more sanitized text is printed (§7). Comparing two customers' projects puts both in one report; the coverage block
names both directories so the reader knows what the file contains before sharing it.

## 13. Testing

Tests build small projects through the existing insert paths (as `DuckDbProjectInsertTests` does),
with fixture names from the anonymous vocabulary in `CLAUDE.md` (`AppDb`, `AppSchema`,
`WidgetRecalc`). No `.xel` and no SQL Server needed. New class `ProjectComparisonTests`, plus
`CompareDigestMarkdownTests` and `CliCompareTests`.

1. A project compared with itself: no regression, no gain, no appeared, no disappeared, no plan
   change. This also confirms the double attach on the embedded engine.
2. Refusals: missing database file; two normalizer versions on one side; different versions
   across sides. Each exits 1 with its message.
3. Read-only: SHA-256 of both database files equal before and after `Run`.
4. A path containing a single quote is attached correctly.
5. §7: a statement three times slower in target ranks first in regressions; one under
   `MinExecutions` or under `MinAvgDurationUs` on both sides does not rank.
6. §8: a statement at the same speed but ten times more frequent ranks in load increases and not
   in §7.
7. §9: appeared and disappeared lists and their counts.
8. §6: a span under `MinSpanMinutes` suppresses §8 with a note; a factor-ten gap in smallest
   durations emits the predicate note.
9. Text choice: same hash, base imported `raw`, target `literals`: every section prints the
   target text, and the same with the sides swapped prints the base text.
10. §10: no plan profiles on one side skips the section; a changed `plan_hash` and an appeared
   `spill_to_tempdb` kind are listed; the decimal to hex link.
11. `--database` filters executions on both sides and leaves plans untouched.
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

1. Are the default thresholds (`MinExecutions` 5, `MinAvgDurationUs` 1 ms, `MinSpanMinutes` 10)
   right? They are guesses, to be checked on two real projects before release, and exposed as
   flags only if that check shows a need.
2. Should §7 also rank on p95 rather than only on the average? The average is chosen because it
   is stable at low counts; p95 is displayed.
3. The factor of ten in §6 for the predicate heuristic is a guess, like the thresholds.
