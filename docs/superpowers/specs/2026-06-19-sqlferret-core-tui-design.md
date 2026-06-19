# SQLFerret — Core engine + TUI (v1) design

**Date:** 2026-06-19
**Status:** Design — pending review
**Scope:** First build cycle only. Includes optional live-server **estimated** plan capture
(compile-only). Later cycles (each its own spec): Avalonia GUI host; structured replay +
**actual** post-execution plan capture; deadlock / blocked-process timeline + LLM explanation;
query-plan-profile LLM analysis; ERRORLOG parsing.

---

## 1. Summary

SQLFerret is a cross-platform SQL Server workload explorer. It ingests SQL Server Extended
Events (`.xel`) — a single file or an entire `logs/` folder — normalizes each captured query
into a stable signature, stores everything in an embedded DuckDB file, and lets an analyst
explore the workload (slowest / most frequent queries, per-session flow, parameter impact)
from an interactive terminal UI.

This cycle delivers the **Core engine** and a **Terminal.Gui v2 TUI** over it. A later cycle
adds an Avalonia GUI on the *same* Core with no engine changes.

### Why this stack (feasibility)

- `.xel` is an undocumented binary format. The only robust cross-platform reader is
  **XELite** (`Microsoft.SqlServer.XEvent.XELite`), which is .NET. There is no Go-native
  parser.
- **Avalonia is .NET-only**, and is a stated target for a later cycle.
- These two facts fix the stack at **C# / .NET 8+**, which also makes "TUI now, Avalonia
  later" natural: both are thin hosts over one Core library.
- **DuckDB.NET** gives embedded analytical SQL with no server.
- **ScriptDom** (`Microsoft.SqlServer.TransactSql.ScriptDom`, MIT, open source) is Microsoft's
  authoritative T-SQL parser; we use only its **token stream** for normalization (cheap) plus
  a minimal AST pass (statement-kind + primary table).

---

## 2. Design principles (KISS — read before implementing)

This project must stay **simple and idiomatic C#**. Explicitly:

- **No gratuitous layering.** No repository/unit-of-work abstractions, no DI-heavy onion/
  hexagonal architecture, no `IXxxService` interface for every class. One Core library with a
  handful of plain classes that each do one job.
- **Interfaces only where a real second implementation exists or is imminent.** (E.g. nothing
  needs an interface just to be "mockable" — the engine is tested with real fixtures and pure
  functions.)
- **Plain data:** `record` types / POCOs for DTOs. No AutoMapper, no mediator, no CQRS.
- **DuckDB is the query engine.** Analysis lives in SQL strings in one `WorkloadQueries`
  class, not in a hand-built C# object graph. Don't reimplement aggregation in C#.
- **Streaming + batch, not clever.** `IEnumerable`/`IAsyncEnumerable` of events, batched
  inserts via the DuckDB Appender. No actor frameworks, no channels unless a real bottleneck
  proves the need.
- **The TUI is thin.** View-models hold state and call Core; no business logic in the UI.
- Prefer a slightly longer obvious method over a clever generic abstraction. When a file grows
  past one clear responsibility, split it — but don't pre-split into ceremony.

If a proposed implementation introduces a layer, an interface, or a pattern that isn't *paying
for itself right now*, that's a smell — drop it.

---

## 3. Architecture & modules

```
SqlFerret.Core            (class library — no UI dependency)
 ├─ Ingestion/
 │   ├─ XelSource          resolve path: file → that file; dir → all *.xel (non-recursive)
 │   └─ XelReader          XELite wrapper, STREAMS events (handles rollover), yields per event
 ├─ Mapping/
 │   └─ EventMapper        XEvent → ExecutionEvent DTO; picks SQL field by event type
 │                         (batch_text / statement / sql_text); reads metrics + actions;
 │                         captures object_name + is_system; classifies event_class
 ├─ Parameters/
 │   ├─ ParameterExtractor rpc_completed / sp_executesql → structured params
 │   └─ RedactionPolicy    off | hash | masked | full; per-name rules (password, token, email…)
 ├─ Normalization/
 │   ├─ TokenNormalizer    ScriptDom token stream → literals to ?, strip comments, collapse
 │   │                     IN(), lowercase keywords; raw-text fallback if tokenize fails
 │   ├─ AstClassifier      minimal AST: statement_kind + primary_table
 │   └─ Fingerprint        stable hash + normalizer_version
 ├─ Filtering/
 │   └─ FilterRule + FilterCompiler   one rule model → WHERE clause (view) or drop predicate
 │                                    (ingest); used by ingestion AND queries
 ├─ Storage/
 │   └─ DuckDbProject      .duckdb file; schema; batch Appender inserts; ingestion_runs;
 │                         file_offset
 ├─ Analysis/
 │   └─ WorkloadQueries    Top-Slow, Top-Frequent, cumulative cost, percentiles, occurrences,
 │                         session flow, param distribution, param impact, dimensions — all
 │                         parametrized DuckDB SQL
 └─ Server/                (optional — only active when a connection is configured)
     └─ PlanService        Microsoft.Data.SqlClient; SET SHOWPLAN_XML ON → estimated plan XML
                           for a chosen statement; saves <id>.sqlplan to the plans folder

SqlFerret.Tui             (Terminal.Gui v2 host — thin)
 ├─ import <path> [--project x.duckdb] [--redaction masked]   non-interactive ingest entry
 └─ interactive views + view-models calling Core (section 7)

SqlFerret.Core.Tests      xUnit — normalizer golden tests, mapper, extractor, filter, pipeline
```

**Data flow (one direction):**
`path → XelReader (stream) → EventMapper → {TokenNormalizer + AstClassifier + ParameterExtractor}
→ ingest filters → DuckDbProject (batched) → WorkloadQueries → TUI`.

**Approach chosen:** layered Core + thin TUI host, with the TUI exposing a non-interactive
`import <path>` switch so scripted ingestion works without a separate CLI project. (Rejected:
a monolith TUI — would force an engine extraction when Avalonia arrives; a third standalone CLI
project — unnecessary now.)

---

## 4. Data model (DuckDB)

Stored once per project `.duckdb` file. Durations/CPU stored in **microseconds** (XE units vary
by event); the host formats them per config.

```sql
-- Provenance: one row per import action
ingestion_runs(
  run_id             BIGINT PRIMARY KEY,
  source_path        TEXT,
  files_count        INTEGER,
  bytes_total        BIGINT,
  started_at         TIMESTAMP,
  finished_at        TIMESTAMP,        -- NULL = incomplete/crashed run
  events_read        BIGINT,
  events_mapped      BIGINT,
  events_unmapped    BIGINT,
  events_cleaned     BIGINT,           -- dropped by ingest-stage filter rules
  tokenize_failures  BIGINT,
  normalizer_version INTEGER,
  redaction_policy   TEXT
)

-- One row per observed execution (sql_text_raw preserves real literal values)
executions(
  execution_id    BIGINT PRIMARY KEY,
  run_id          BIGINT,
  captured_at     TIMESTAMP,
  event_name      TEXT,
  event_class     TEXT,               -- RpcCall | SqlBatch | Statement | Unknown
  object_name     TEXT,               -- rpc_completed object (e.g. sp_cursorclose), nullable
  is_system       BOOLEAN,
  database_name   TEXT,
  login_name      TEXT,
  client_hostname TEXT,
  client_app_name TEXT,
  session_id      INTEGER,
  duration_us     BIGINT,
  cpu_time_us     BIGINT,
  logical_reads   BIGINT,
  physical_reads  BIGINT,
  writes          BIGINT,
  row_count       BIGINT,
  query_hash      TEXT,
  query_plan_hash TEXT,
  sql_text_raw    TEXT,
  normalized_hash TEXT,               -- → normalized_queries
  xe_file_name    TEXT,
  file_offset     BIGINT
)

-- One row per distinct query signature
normalized_queries(
  normalized_hash    TEXT PRIMARY KEY,
  normalized_sql     TEXT,
  statement_kind     TEXT,            -- SELECT|INSERT|UPDATE|DELETE|EXEC|OTHER
  primary_table      TEXT,            -- best-effort, nullable
  normalizer_version INTEGER,
  first_seen_at      TIMESTAMP,
  last_seen_at       TIMESTAMP
)

-- One row per structured parameter (rpc_completed / sp_executesql)
execution_parameters(
  execution_id     BIGINT,
  ordinal          INTEGER,
  name             TEXT,              -- nullable (positional literals)
  source_kind      TEXT,              -- rpc_parameter | literal | output_parameter
  sql_type_guess   TEXT,
  value_text       TEXT,              -- AFTER redaction policy
  value_redacted   BOOLEAN,
  is_truncated     BOOLEAN,
  parse_confidence DOUBLE
)
```

Deliberate choices:
- `normalizer_version` on both run and signature → signatures can be regenerated when
  normalization improves, without losing old analyses.
- **Aggregates are DuckDB queries/views over `executions`, not materialized tables.** DuckDB is
  fast enough; pre-aggregation is premature. A later cycle may add `query_rollups` if needed.
- Redaction applied **before** `value_text` is written: `off` writes no parameter rows (signatures
  still work); `hash` stores only a fingerprint; `masked` keeps type/shape; `full` keeps the value.
- `query_hash` / `query_plan_hash` populated only when the XE session captured them.

---

## 5. Ingestion & normalization behavior

- **Streaming, never load-all.** `XelReader` pulls events one at a time via XELite and pushes
  them through the pipeline in **batches** (~5–10k) into DuckDB via the **Appender** API.
  Bounded memory on multi-GB folders.
- **Folder semantics:** path-is-file → that file; path-is-dir → **all `*.xel` in the folder
  (non-recursive)** as one combined workload (XELite reads rollover families natively).
- **SQL field selection** (`EventMapper`): `sql_batch_completed` → `batch_text`;
  `rpc_completed` → `statement`; `*_statement_completed` → `statement`. Missing/empty →
  `event_class = Unknown`, counted in `events_unmapped`. Never aborts the run.
- **Normalization per execution:** ScriptDom tokenize → rewrite literal tokens
  (`Integer, Numeric, Money, Real, HexLiteral, AsciiStringLiteral, UnicodeStringLiteral`) to `?`
  → drop comment/whitespace tokens → lowercase keywords → collapse `IN (?, ?, …)` → `IN (?)` →
  minimal AST for `statement_kind` + `primary_table` → hash. **Tokenize failure → raw-text
  fallback hash**, `tokenize_failures++`, signature flagged low-confidence. Nothing vanishes.
- **Parameter extraction:** `rpc_completed` / `sp_executesql` → `execution_parameters`, applying
  `RedactionPolicy` before write. Per-name rules (`%password%`, `%token%`, `%email%`) force
  redaction regardless of global policy.
- **Ingest-stage cleaning filters** applied here (section 6): each drop counted per-rule in
  `events_cleaned` and surfaced in the Quality view.
- **Idempotency / incremental:** each `(xe_file_name, file_offset)` recorded; re-importing the
  same folder into an existing project resumes past the last offset rather than duplicating.
- **Failure isolation:** corrupt/locked `.xel` → skip with warning, note in `ingestion_runs`;
  the rest of the folder still imports.

---

## 6. Filtering & cleaning

One rule model, applied at two stages. Rules live in an **app-managed file `sqlferret.ui.json`**
(see section 8) so the TUI can read/write them freely without clobbering the user-owned config.

```jsonc
// sqlferret.ui.json → "filters"
[
  { "id": "noise-cursor", "field": "object_name", "op": "in",
    "values": ["sp_cursorclose", "sp_cursorunprepare", "sp_unprepare"],
    "stage": "ingest", "action": "exclude", "enabled": true },
  { "id": "no-reset", "field": "object_name", "op": "eq",
    "value": "sp_reset_connection", "stage": "ingest", "action": "exclude", "enabled": true },
  { "id": "f-tempdb", "field": "database_name", "op": "eq",
    "value": "tempdb", "stage": "view", "action": "exclude", "enabled": false }
]
```

- **Fields:** `object_name, event_name, event_class, database_name, login_name,
  client_app_name, client_hostname, statement_kind, is_system, session_id, normalized_hash`,
  and numeric `duration_us, cpu_time_us, logical_reads, physical_reads, writes, row_count`.
- **Operators:** `eq (=)`, `neq (<>)`, `gt (>)`, `lt (<)`, `gte (>=)`, `lte (<=)`, `in`, `like`.
- **Actions:** `exclude` / `keep` (keep-only).
- **Stages:**
  - `ingest` → hard drop during ingestion (smaller DB, irreversible without re-import). For pure
    noise: `sp_reset_connection`, cursor housekeeping, `is_system = true`. Counted per-rule.
  - `view` → non-destructive `WHERE` in query views, instantly toggleable. The default for
    exploratory filtering. A view rule can be **promoted** to ingest (effective next import).
- **Combine logic:** active view filters are AND-combined; values within one `in` list are OR.
- **`FilterCompiler`** turns a rule into either a parametrized `WHERE` fragment (view) or a
  C# predicate / SQL drop (ingest). Single implementation shared by both stages.

**Default shipped ingest-cleaning rules** (conservative, all toggleable): exclude
`object_name in (sp_reset_connection, sp_cursorclose, sp_cursorunprepare, sp_unprepare)`. Other
common-noise rules ship **disabled** as suggestions.

### Filter UX (TUI)

- **Quick-add from a focused cell:** `x` = exclude this value, `o` = keep-only this value.
  Re-pressing `x` on a currently-excluded value toggles it back. Quick `exclude` rules default
  to **global-persisted** (saved to `sqlferret.ui.json`, apply to every project).
- **Filter Builder (`F`):** field → operator → value popup. Low-cardinality dimensions offer a
  **distinct-value pick list**; numeric fields are typed in the **configured display unit**
  (`> 10ms`) and converted to µs by Core. Builder filters default **session-scoped**
  (exploratory); `p` pins one to `sqlferret.ui.json`.
- **Active filters chip bar** atop every data view shows every enabled filter
  (`[object_name∉{…}] [db≠tempdb] [duration>10ms]`); Space toggles `enabled`, `Del` removes.
  You always see what is hiding rows.

---

## 7. TUI (Terminal.Gui v2)

Master/detail shell: left rail of views, central data table, bottom detail/status. Keyboard-first
(arrows/Tab move, Enter drills, `/` text-filter on normalized SQL, `s` cycle sort, `F` filter
builder, `x`/`o` quick filter, `C` choose columns, `c` copy SQL, `Esc` back); mouse supported.

```
┌─ SQLFerret ── project: workload.duckdb ── 1.2M execs / 3.4k signatures ──┐
│ Views      │ [object_name∉{sp_cursorclose}] [duration>10ms]   (chips)    │
│ ▸ Top Slow │  ┌──────────────────────────────────────────────────────┐  │
│   Top Freq │  │ kind  signature (normalized)      cnt   avg   p95  tot│  │
│   Queries  │  │ SELECT select * from dbo.users…  12k  4ms  18ms  52s │  │
│   Session  │  │ EXEC   exec dbo.getorder @id=?…   8k   6ms  22ms  48s │  │
│   Dimensions│ │ …                                                    │  │
│   Quality  │  └──────────────────────────────────────────────────────┘  │
│   Import   │  Enter = occurrences · c = copy SQL · F = filter · C = cols │
└────────────┴────────────────────────────────────────────────────────────┘
```

**Views:**
1. **Import / Open** — pick `.xel` file or `logs/` folder, choose/create project, pick redaction
   policy; live progress from `ingestion_runs` (files, read/mapped/unmapped/cleaned/failures).
2. **Top Slow** — grouped by signature; `kind, signature, count, avg, p95, max, total_duration`;
   sortable.
3. **Top Frequent** — same grid sorted by `count`, plus **cumulative cost** = `count × avg_duration`
   (the "chatty query" finder).
4. **Queries (browse/search)** — all signatures; full-text filter on normalized SQL; chips by
   `statement_kind` / database / app.
5. **Signature drill-down** (Enter on a row) — raw + normalized SQL side by side; metric summary
   (percentiles, reads vs CPU); sub-panels:
   - **Occurrences** — individual executions with timestamp + their actual parameter values.
   - **Parameter distribution** — value → count.
   - **Parameter-impact comparison** — group occurrences by parameter value(s), show
     `count, avg, p95, max, avg_reads` per value-set; sort by slowest set (parameter-sniffing).
   - **Build-for-SSMS** — reconstruct runnable T-SQL from an occurrence or chosen parameter set,
     copy to clipboard (see below).
   - **Get estimated plan** (`g`, only when a connection is configured) — reconstruct the
     statement, fetch its estimated plan from the server, save it, and confirm with the path
     (see §9).
6. **Session Flow** — pick a `session_id` (`f` on any execution row, or a session picker) →
   chronological operations for that session ordered by `captured_at`, with elapsed gaps.
   **Session boundary handling:** SQL Server reuses `session_id` (SPID) over time, so a "session"
   is scoped as `session_id` within a contiguous window — the timeline breaks on a large idle gap
   and/or a change of `login`/`app`/`database`, and those boundaries are shown. Never splices
   unrelated connections silently.
7. **Dimensions** — breakdown by database / login / host / app (count + total cost).
8. **Quality** — un-mapped events, tokenize fallbacks, per-rule cleaned counts, redaction summary.
9. **Plans** (only when a connection is configured) — lists saved `.sqlplan` files with their
   statement id, source signature, and capture time. `o` = **open** the plan in the OS default
   handler (Windows: SSMS / SentryOne Plan Explorer); elsewhere reveals/opens the path via
   `xdg-open` if a handler exists, otherwise shows the file path.

**Column chooser (`C`, per view):** SSMS-style "choose columns" — show/hide, reorder, optional
width, "reset to defaults". Each view exposes its own column catalog (aggregate columns for
grouped views; `executions` fields for row-level views). Persisted per view in `sqlferret.ui.json`.

**Build-for-SSMS (clipboard):**
- `rpc_completed` → `EXEC dbo.GetOrder @OrderId = 123, @Culture = N'fr-FR';` (high confidence —
  structured params).
- `sql_batch` → copy `sql_text_raw` verbatim (literals already inline).
- `sp_executesql` → reconstruct `sp_executesql N'…', N'@p int…', @p = …` (medium confidence).
- Each carries a **confidence flag**. Text-only — no execution, no plan capture (later cycle).
- If redaction removed the values, the build uses the redacted form and warns; full fidelity
  needs `redaction = full` at ingest.
- **Clipboard:** native on Windows/macOS; on Linux needs `xclip` / `wl-clipboard`. Fallback when
  absent: write the script to a `.sql` file and show the path.

The TUI holds no analysis logic — every grid is a parametrized `WorkloadQueries` call; durations
render via the config `durationUnit`.

---

## 8. Configuration

Two files, clear ownership:

- **`sqlferret.config.json` — user-owned** (hand-edited; the app does not rewrite it):
  ```jsonc
  {
    "display": { "durationUnit": "ms", "cpuUnit": "ms" },  // "us" | "ms" | "s"
    "ingest":  { "redactionPolicy": "masked" },             // off | hash | masked | full
    "server": {
      // Optional. When absent, SQLFerret is fully offline; the plan feature is disabled.
      // Credentials should NOT be written here — reference an env var (from .env) instead.
      "connectionString": "Server=tcp:myhost,1433;Database=master;${SQLFERRET_AUTH}",
      "plansFolder": "./plans"                              // where <id>.sqlplan files are saved
    }
  }
  ```
  `${ENV_VAR}` tokens in `connectionString` are interpolated from the environment (loaded from
  `.env`), so the secret part (e.g. `User ID=…;Password=…` or `Authentication=…`) lives in
  `.env`, never in this committed file.
  Resolved via `Microsoft.Extensions.Configuration`: baked-in defaults → config file in working
  dir → CLI flags. Core stays unit-agnostic (always µs); a small `DisplayFormat` helper in the
  host applies `durationUnit`.
- **`sqlferret.ui.json` — app-managed** (read/written by the TUI; Avalonia later shares it):
  ```jsonc
  {
    "filters": [ /* section 6 */ ],
    "views": {
      "topSlow": { "columns": ["kind","signature","count","avg","p95","total"], "sort": "total_desc" }
      // … per view: column visibility/order + default sort
    }
  }
  ```
- Malformed config/ui file → warn, fall back to defaults, never crash the host.
- Secrets — DB connection credentials (now) and LLM API keys (future cycles) — go in **`.env`**,
  referenced from config via `${ENV_VAR}`, never written into these committed files.

---

## 9. Live server connection & estimated execution plans

**Optional and opt-in.** All ingestion and analysis work fully offline; this feature activates
only when `server.connectionString` is configured. It is a *safe subset* of plan analysis:
estimated plans compile but **do not execute** the query.

- **Connection** — `PlanService` uses `Microsoft.Data.SqlClient` with the configured
  connection string (credentials resolved from `.env` via `${ENV_VAR}`). One short-lived
  connection per plan request; nothing is kept open.
- **Estimated plan request** — for a chosen statement (an occurrence with its real parameter
  values, or a signature with a chosen parameter set):
  1. `USE [<database_name>]` from the execution so objects resolve in the right database.
  2. `SET SHOWPLAN_XML ON;` then submit the reconstructed batch. The server returns the
     **estimated** Showplan XML **without running** the statement.
  3. Capture the XML.
- **Saving** — write the XML to `server.plansFolder` (default `./plans`) as
  `<id>.sqlplan` — `id` = `execution_id` when generated from a specific occurrence, else the
  short `normalized_hash`. The `.sqlplan` extension makes it open directly in **SSMS** or
  **SentryOne Plan Explorer**. A small `plans` index row (id, normalized_hash, source, captured_at,
  file path) is kept so the **Plans** view can list them.
- **Opening** — the Plans view `o` command launches the `.sqlplan` in the OS default handler
  (Windows → SSMS/Plan Explorer); on Linux/macOS, opens via `xdg-open`/`open` if a handler
  exists, otherwise shows the path.
- **Caveats made explicit in the UI:**
  - A meaningful plan needs the real parameter values — if `redaction` removed them, warn that
    the plan may differ; full fidelity needs `redaction = full` at ingest.
  - The plan is compiled on **whatever server the connection string points to** — schema/stats
    there may differ from where the workload was captured. The UI states the target server.
  - Estimated (compile-only), so safe to run, but it still consumes a connection and a compile;
    not run in a tight loop.

---

## 10. Error handling

Principle: isolate failures, count everything, never silently drop.

- **Bad input file** — corrupt/locked/truncated `.xel` → skip + warn, record in `ingestion_runs`;
  rest of folder imports.
- **Unmappable event** — `event_class = Unknown`, counted in `events_unmapped`, shown in Quality.
- **Tokenize failure** — raw-text fallback hash, `tokenize_failures++`, signature low-confidence.
- **Ingest-cleaning drops** — counted per rule (`events_cleaned`) and shown in Quality.
- **Redaction** — enforced before write; per-name rules override global policy.
- **Crash mid-import** — each batch is one DuckDB transaction; the `ingestion_run` row keeps
  `finished_at = NULL`; `file_offset` lets a re-run resume without duplicating.
- **Malformed config / ui file** — warn, fall back to defaults, never crash.
- **Clipboard unavailable** — fall back to writing a `.sql` file and showing the path.
- **Server connection / plan errors** — unreachable server, auth failure, or a statement that
  won't compile (e.g. redacted values, missing objects) → surface the SQL error in the TUI and
  skip; never crash. The offline app keeps working.

---

## 11. Testing

- **Normalizer — golden tests (the TDD core).** Pure `raw SQL → normalized signature` pairs:
  literals (`N'…'`, escaped `''`, numbers, dates, GUIDs, hex), `IN (…)` collapse, comments,
  `[bracketed]` identifiers, raw-text fallback. Trivially unit-testable; the product's heart.
- **EventMapper / ParameterExtractor** — table-driven over sample event payloads per event type
  (`sql_batch_completed`, `rpc_completed`, `sp_executesql`, `*_statement_completed`): field
  selection, `object_name`/`is_system` capture, parameter parsing + redaction.
- **Filter engine** — a rule compiles to the right `WHERE` (view) and the right drop predicate
  (ingest); `exclude`/`keep`; toggle round-trips through `sqlferret.ui.json`.
- **Storage + Analysis — integration.** Ingest small **committed `.xel` fixtures** into a temp
  `.duckdb`; assert row counts, Top-Slow/Top-Frequent ordering, session-flow ordering. Fixtures
  generated once from an **Azure SQL Edge / SQL Server container** (runs on Linux via
  Podman/Docker), generation script committed and documented → reproducible.
- **PlanService** — unit-test statement reconstruction + `SHOWPLAN_XML` batch shaping and the
  `<id>.sqlplan` save/index logic with a faked SqlClient boundary; one optional integration test
  against the Azure SQL Edge container asserting a non-empty Showplan XML is returned and saved.
- **TUI** — kept thin so logic lives in testable Core; smoke-test view-model wiring, not
  Terminal.Gui rendering.

---

## 12. Out of scope (later cycles, each its own spec)

- Avalonia GUI host over the same Core (reads the same `sqlferret.config.json` / `sqlferret.ui.json`).
- Structured replay engine (executing reconstructed workloads), and **actual / post-execution**
  plan capture (`query_post_execution_showplan`, `query_plan_profile`). Note: **estimated**
  on-demand plans (`SHOWPLAN_XML`, compile-only) ARE in scope this cycle — see §9.
- Deadlock (`xml_deadlock_report`) + blocked-process-report timeline reconstruction and LLM
  explanation over time.
- Lightweight-profiling query-plan (`query_plan_profile`) LLM analysis.
- ERRORLOG parsing (boot info + errors).
- `query_rollups` materialization, capture-vs-capture comparison, saved views.
