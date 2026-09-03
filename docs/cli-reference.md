# CLI reference

```text
sqlferret <command> [arguments]
```

Examples below use `sqlferret` as shorthand. The repo does not install a binary; alias it or
publish one, see [getting-started.md](getting-started.md#making-it-feel-like-a-command).

Running with no arguments prints a usage line to stderr and exits 1. There is no `--help` flag
yet; this page is the reference.

## Conventions

- **`--project <dir>` is a directory, not a file.** It is created on first use, together with the
  DuckDB database, `plans/`, `exports/`, `project.json` and a project `README.md`. Pointing at a
  path where a regular file already exists is an error.
- **stdout carries results, stderr carries progress and warnings.** Every command is pipe-safe:
  the live progress gauge is suppressed when stderr is redirected.
- **Exit codes**: `0` success, `1` any error (bad arguments, missing input, unreadable project,
  failed writes). `obfuscate-plan --in-dir` also exits 1 if any single file failed, even though
  the others were written.
- **Durations** are formatted using the project's `display.durationUnit` (default `ms`). The
  database always holds microseconds. See [configuration.md](configuration.md).
- **Time windows** accept either `--from`/`--to` (any invariant-culture datetime) or `--last N{h|d}`
  (`--last 24h`, `--last 7d`). The two forms are mutually exclusive. Omitting both means "no bound".

---

## `import`

Ingest Extended Events into the project.

```text
sqlferret import <path> --project <dir> [--redaction off|hash|masked|full]
                         [--sanitize-sql-text raw|literals]
```

| Argument | Default | Meaning |
|---|---|---|
| `<path>` | required, positional | A single `.xel` file, or a directory. A directory is expanded to its top-level `*.xel` files, sorted by name (which is rollover order). |
| `--project <dir>` | required | Project directory. |
| `--redaction <mode>` | project config, default `masked` | Parameter-value redaction policy for this run. See [privacy.md](privacy.md). |
| `--sanitize-sql-text <raw\|literals>` | project config, default `raw` | Statement text written to `sql_text_raw` and `normalized_sql`. `literals` collapses inlined literals to `?`; identifiers are kept, except a double-quoted token, which also collapses (ambiguous without `QUOTED_IDENTIFIER`; bracketed identifiers are unaffected — see [privacy.md](privacy.md)), including inside an unwrapped `sp_executesql` inner statement — the inner query is the only part of an `sp_executesql` call this keeps; every other argument collapses to `?` too. Only the positional literal form (`exec sp_executesql N'...', ...`) is unwrapped — a named `@stmt = N'...'` argument, a statement passed in a variable, or a bracketed `[sp_executesql]` call all fall back to a full collapse, safely. Reads `ingest.sqlTextSanitization` from the project config when not passed. An invalid value exits 1. Also governs the blocking input buffer and, with `--redaction`, whether the raw blocking/deadlock XML is retained at all — the stricter of the two policies wins. See [privacy.md](privacy.md). |

A run imported at `literals` cannot produce estimated plans later — the stored statement text is
not valid T-SQL. See [execution-plans.md](execution-plans.md#estimated-plans).

Recognized events:

| Event | Becomes |
|---|---|
| `rpc_completed`, `sql_batch_completed`, `*statement*` | rows in `executions` plus a query shape in `normalized_queries` |
| `blocked_process_report` | `blocking_reports` + two `blocking_processes` rows |
| `xml_deadlock_report` | `deadlock_reports` |
| `query_post_execution_plan_profile` | `plan_profiles` + `plan_findings` + `.sqlplan` artifacts |

Anything else is counted as `unmapped` and ignored.

**Output.** A live gauge on stderr while running. On completion, stdout gets a single summary
line whose counters are mutually exclusive and exhaustive across every event read:

```text
run 3: read=41827 mapped=41120 unmapped=612 cleaned=95 tokenizeFailures=0
       blocking=48 deadlocks=3 blockingParseFailures=0
       planProfiles=346 planParseFailures=0 planWriteFailures=0
```

If any plan profiles were ingested, a second line names the artifact folder:

```text
plans: 346 profiles, 62 distinct -> ./audits/prod/plans/profile/run_3
```

**Warnings on stderr.** If plan profiles were ingested but no execution in the run carries a
`query_hash`, import warns that plans cannot be correlated with queries, and tells you which
ACTION to add to the capture session. This is the single most common capture mistake; see
[capture-session.md](capture-session.md).

**Re-importing.** Importing again into the same project appends a new run rather than replacing
anything. Runs are numbered, plan artifacts are partitioned into `plans/profile/run_<id>/`, and
analysis spans all runs.

---

## `top-slow`

Print query shapes ranked by total duration.

```text
sqlferret top-slow --project <dir> [--limit <n>]
```

| Argument | Default | Meaning |
|---|---|---|
| `--project <dir>` | required | Project directory. |
| `--limit <n>` | 20 | Number of shapes to print. |

Output columns: statement kind, execution count, total duration, normalized SQL truncated to
80 characters.

```text
SELECT      1204  total=812304 ms   select o.id , o.total from dbo.Orders o where o.CustomerId = ?
```

Ordering is by `total_duration_us` and is not configurable from the CLI. The underlying
`WorkloadQueries.TopSlow` also supports `p95_duration_us`, `max_duration_us` and
`avg_duration_us`, and the terminal UI exposes them; from the CLI, use [`query`](#query) if you
need a different ranking. See [data-model.md](data-model.md#useful-queries).

---

## `query`

Run arbitrary SQL against the project and print the result.

```text
sqlferret query --project <dir> (--sql <text> | --file <path>)
                [--format table|csv|json|md] [--limit <n> | --no-limit] [--raw]
```

| Argument | Default | Meaning |
|---|---|---|
| `--project <dir>` | required | Project directory. Opened **read-only**. |
| `--sql <text>` | — | The statement to run. Exactly one of `--sql` or `--file` is required. |
| `--file <path>` | — | Read the statement from a file instead. |
| `--format <f>` | `table` | One of `table`, `csv`, `json`, `md`. Anything else is an error. |
| `--limit <n>` | 1000 | Maximum rows returned. Must be a strictly positive integer. |
| `--no-limit` | — | Return every row. Mutually exclusive with `--limit`. |
| `--raw` | off | Never apply duration formatting; print values as stored. |

**The database is opened read-only.** Write protection comes from the connection, not from
inspecting your SQL — parsing the statement to decide whether it writes would be a sieve.

### The default row limit

A result set is materialised entirely in managed memory, so an unbounded `SELECT * FROM
executions` — a perfectly ordinary query — would exhaust the heap on a real trace. Hence the
1000-row default.

Truncation is never silent: it is announced on `stderr`, and the `table` footer names the cause,
distinguishing the default limit from an explicit `--limit`. Do not discard `stderr` if you care
whether you saw the whole answer.

`--limit` with no value, or with a non-numeric or non-positive one, is an error rather than a
silent fallback.

### Duration formatting follows the column name

A column whose **output name** ends in `_us` is formatted using the project's display unit;
any other name is printed as stored.

```sql
SELECT sum(duration_us) AS total_us   -- formatted
SELECT sum(duration_us) AS total      -- raw microseconds
```

The suffix converts **any** numeric value in that column whatever its SQL type — `sum()` yields a
HUGEINT and `avg()` a DOUBLE — so two `_us` columns on the same row are always in the same unit.
`--raw` disables the whole mechanism.

### Choosing a format

`csv` and `json` are **faithful**: newlines inside a value are preserved, and `json` stays typed
down to a HUGEINT. `table` and `md` are **presentation** formats with one record per line, so a
newline inside a `sql_text_raw` is rendered as `\n` rather than breaking the grid. SQL you intend
to read back or replay should come out as `csv` or `json`.

`json` indexes rows by column name, so a `JOIN` that returns two columns with the same name would
overwrite one of them. That case is reported on `stderr` rather than passing unnoticed; alias your
columns.

If the project's classification is stale, the command says so on `stderr` — see
[`reclassify`](#reclassify).

```bash
sqlferret query --project ./audits/prod-2026-08 --format md \
    --sql "SELECT statement_kind, count(*) n FROM normalized_queries GROUP BY 1 ORDER BY n DESC"
```

---

## `reclassify`

Re-run normalization and classification over an existing project, in place, without re-importing.

```text
sqlferret reclassify --project <dir> [--force]
```

| Argument | Default | Meaning |
|---|---|---|
| `--project <dir>` | required | Project directory. Opened read-write. |
| `--force` | off | Replay rows that are already at the current version. |

Use it when a project was imported by an older `QueryNormalizer.Version`. The commands that read a
project detect the situation and mention it on `stderr`; this is the fix, and it costs no
re-import because the classification is recomputed from the statement text already stored.

`--force` is for the other case: the version has not moved, but the classifier has gained a visitor
for a construct it previously left as `OTHER`, and you want those rows replayed.

```text
reclassify: v1 -> v4 | examined=8421 changed=1180 unchanged=7241 unclassified=12 withoutSample=3 unusableSample=0
```

Three outcomes are reported separately on `stderr` because they are not the same problem:

- **unclassified** — the text was parsed but no visitor claimed it, or it could not be parsed at
  all. `--force` will replay these once the classifier learns the construct.
- **withoutSample** — no source text was retained for that signature, neither an execution nor a
  blocking input buffer, so there is nothing to reclassify. Its classification is left untouched;
  `--force` cannot help, only a re-import can.
- **unusableSample** — text was retained, but it is already normalized, so it is not T-SQL any
  more: `where c = ?` does not parse, and reclassifying from it would degrade the row to `OTHER`.
  Two provenances produce this, and only the provenance can tell — never the text itself: an
  execution from a run imported with `--sanitize-sql-text literals`, and a blocking input buffer
  from any run not imported with `--redaction off`. These rows are left untouched; `--force` will
  not touch them either. Only a re-import from the capture, at a policy that retains the statement
  text, makes them classifiable.

Already at the current version and without `--force`, the command reports `rien a faire` and
changes nothing.

---

## `export-blocking`

Emit the blocking digest.

```text
sqlferret export-blocking --project <dir> [--format json|md|both] [--samples <n>] [--out <file>] [--full]
```

| Argument | Default | Meaning |
|---|---|---|
| `--project <dir>` | required | Project directory. |
| `--format <fmt>` | `both` | `md` for Markdown, `json` for a versioned JSON envelope, `both` for Markdown with the JSON appended in a fenced block. |
| `--samples <n>` | 5 | Concrete sample incidents kept per contention pattern. |
| `--out <file>` | stdout | Destination file. Path-traversal segments (`..`) are rejected. |
| `--full` | off | Ignore the digest entirely and dump **every** blocking report as NDJSON, one JSON object per line. |

The digest contains: overview and time window, deadlock count, wait-time percentiles, locality by
wait-resource type, top contended objects, top blocker and top blocked query shapes, lock-mode and
isolation-level distributions, blocking chains, and the sample incidents. It is deliberately
bounded so it fits in a human's attention span or a model's context window.

`--full` is the opposite: unbounded, ordered, and meant for a file rather than a terminal.

See [blocking.md](blocking.md).

---

## `export-events`

Extract raw blocked-process and deadlock XML, one file per event, plus a manifest.

```text
sqlferret export-events --project <dir> --out <dir>
                        [--kind blocking|deadlock|both]
                        [--from <dt> --to <dt> | --last <N>{h|d}]
                        [--fingerprint <hash>] [--database <id>] [--limit <n>]
```

| Argument | Default | Meaning |
|---|---|---|
| `--project <dir>` | required | Project directory. |
| `--out <dir>` | required | Output directory, created if absent. `..` segments rejected. |
| `--kind <k>` | `both` | Which event class to export. |
| `--from` / `--to` / `--last` | unbounded | Time window. |
| `--fingerprint <hash>` | all | Restrict to one blocking contention pattern. Blocking only. |
| `--database <id>` | all | Restrict to one database id. Blocking only. |
| `--limit <n>` | 100 | Cap on files written, per kind. Must be positive. |

Prints a JSON summary on stdout:

```json
{"outDir":"./exports/events","indexPath":"./exports/events/index.json",
 "blocking":{"written":42,"skipped":0,"matched":57},
 "deadlock":{"written":3,"skipped":0,"matched":3}}
```

**`skipped` is the one to watch.** XML is only present for runs imported with `--redaction off`
**and** the default `--sanitize-sql-text raw`. The XML documents carry the input buffers verbatim,
so a run that asked for literals to be collapsed does not retain them either. Anything else and
the XML was never stored, so those events are skipped and stderr tells you to re-import. `matched` above `written` means `--limit` truncated the result, and stderr says by how
much.

`--fingerprint` and `--database` are ignored for deadlocks; passing them while deadlocks are in
scope produces a warning rather than an error.

---

## `query-store-import`

Snapshot a live database's Query Store into the project.

```text
sqlferret query-store-import --project <dir>
                             [--conn <connection-string>] [--database <db>] [--no-plans]
                             [--from <dt> --to <dt> | --last <N>{h|d}]
```

| Argument | Default | Meaning |
|---|---|---|
| `--project <dir>` | required | Project directory. |
| `--conn <s>` | `server.connectionString` from config | Connection string. Prefer putting it in config with the secret interpolated from `.env`. |
| `--database <db>` | connection default | Issues a `USE [db]` before reading. |
| `--no-plans` | off | Skip writing `.sqlplan` files. Plan metadata is still imported. |
| `--from` / `--to` / `--last` | everything | Restrict to a runtime-stats interval window. |

Read-only against `sys.query_store_*` under `READ UNCOMMITTED`. Fails cleanly if Query Store is
not enabled on the target database.

Prints:

```text
qds run 1: queries=8412 queryText=7980 plans=9134 runtimeRows=214880 waitRows=48210
           plansWritten=9134 planFailures=0
```

**Warning.** Writing plans emits raw showplan XML, which can carry literal values that your
redaction policy would otherwise have covered. When plans are requested and redaction is not
`off`, stderr says so. Use `--no-plans` if that matters, or obfuscate afterwards.

See [query-store.md](query-store.md).

---

## `obfuscate-plan`

Rewrite identifiers in `.sqlplan` files to stable tokens, and write the reverse map.

Three mutually exclusive modes, checked in this order:

### Folder

```text
sqlferret obfuscate-plan --in-dir <dir> --out-dir <dir> [--map <file>]
```

Recurses through `<in-dir>`, obfuscates every `*.sqlplan` under a **single shared map**, so the
same table keeps the same token across every file. Output mirrors the input tree with
`.anon.sqlplan` extensions.

By default the map is written *outside* `--out-dir`, as a sibling named `<out-dir-name>.map.json`.
That is deliberate: the de-anonymization key must not travel inside the folder you are about to
share. `--map` overrides the location.

Exits 1 if any file failed, after processing the rest. Failures are listed on stderr.

### Single file

```text
sqlferret obfuscate-plan --in <file> --out <file>
```

Standalone, fresh map per invocation. The map is written next to `--out`, with `.sqlplan` swapped
for `.map.json`.

### Project plan

```text
sqlferret obfuscate-plan --project <dir> --plan-id <bare-id>
```

Obfuscates `<project>/plans/<plan-id>.sqlplan` using the **project-wide** map stored in the
DuckDB file, then persists the map back. Tokens therefore stay stable across every plan in the
project and across invocations.

`--plan-id` must be a bare file-name component: slashes, backslashes and `..` are rejected, at
both the CLI boundary and inside the library.

Writes `<plan-id>.anon.sqlplan` and `<plan-id>.map.json` into `plans/`.

See [privacy.md](privacy.md#plan-obfuscation) for what is and is not obfuscated.

---

## Terminal UI

Not a subcommand; a separate host over the same library.

```text
dotnet run --project src/SqlFerret.Tui -- <project-dir>
```

See [tui.md](tui.md).
