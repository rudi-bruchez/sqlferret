# Execution plans

SQLFerret handles plans from three sources:

| Source | How | What you get |
|---|---|---|
| **Actual plans** from `query_post_execution_plan_profile` | During `import` | Deduplicated `.sqlplan` files, per-plan digests, automatic problem detection |
| **Query Store plans** | `query-store-import` | Every cached plan as a `.sqlplan`, plus its metadata and runtime stats |
| **Estimated plans** | `EstimatedPlanService`, library only | A compile-only plan for a captured statement, replayed against a live server |

The first is the interesting one and the rest of this page is mostly about it.

---

## Actual plans, from the trace

Capturing `query_post_execution_plan_profile` gives you plans **with runtime counters**: actual
row counts, spill details, granted-versus-used memory. That is what makes automated triage
possible, since almost every real plan problem is an estimate-versus-reality mismatch, and an
estimated plan has no reality half.

A production trace typically contains thousands of plan events and a few dozen *distinct* plans.
SQLFerret writes files per distinct plan, not per event.

### Plan identity

Deduplication needs a stable key. It is computed in three tiers:

| Tier | When | Key | File prefix |
|---|---|---|---|
| `queryplanhash` | The plan contains exactly one `<StmtSimple QueryPlanHash>` | That hash, with `0x` stripped | `p_` |
| `multi` | Several statements, each with its own plan hash | SHA-256 of the joined hashes, truncated to 16 hex characters | `m_` |
| `content` | No plan hash at all | SHA-256 of the normalized plan XML, truncated to 16 | `c_` |

The `content` tier normalizes before hashing: `RunTimeInformation` and `QueryTimeStats` are
removed, and `GrantedMemory`, `MaxUsedMemory` and `GrantWaitTime` are stripped from
`MemoryGrantInfo`. Those vary per execution, and leaving them in would produce a different hash
every time, which would defeat deduplication entirely. `SerialRequiredMemory` and
`SerialDesiredMemory` are kept, because they are compile-time properties.

The prefix lives only in `file_stem`. `plan_profiles.plan_hash` stays bare so it remains joinable.

> **`<StmtSimple StatementText>` is never a key.** The engine truncates it. On the reference trace
> it is 1,518 characters for one complete `SELECT` and **2 characters** for another. It is stored
> for readability and nothing else.

### Which files get written

Per distinct plan, at most two `.sqlplan` files:

| File | Contents |
|---|---|
| `<stem>.sqlplan` | The **first** occurrence of this plan in the run |
| `<stem>.worst.sqlplan` | The **slowest** occurrence, if a later execution was strictly slower |

"Strictly slower" matters: on equal durations the first occurrence wins, so a plan whose
executions are all the same speed produces one file, not two. An execution with a null duration
never wins.

The writer streams. It holds two durations per plan hash and never a plan XML, so memory does not
grow with trace size. State is only updated **after** a successful write, so a failed write is
retried on the next occurrence and, if it keeps failing, shows up in
`ingestion_runs.plan_write_failures` instead of leaving the database pointing at a file that does
not exist.

`.sqlplan` files are written as **UTF-16 LE with a BOM**, because that is what SQL Server emits
and what the XML declaration inside the file claims. Writing those bytes as UTF-8 under a
declaration saying `utf-16` produces a file SSMS refuses to open.

### Run partitioning

Artifacts go to `plans/profile/run_<id>/`, one folder per ingestion run.

This is not decoration. Digests carry aggregates computed over a single run. Sharing one folder
would mean a second import silently overwriting the first import's triage.

### The artifacts

```text
plans/profile/run_1/
├── index.json                      triage entry point
├── p_A1B2C3D4E5F60718.sqlplan      first occurrence
├── p_A1B2C3D4E5F60718.worst.sqlplan
├── p_A1B2C3D4E5F60718.digest.json
└── …
```

#### `index.json`

One entry per distinct plan. Written to be scanned, by a person or a script, without opening
anything else:

```json
{
  "schema_version": 1,
  "plans": [
    {
      "plan_hash": "A1B2C3D4E5F60718",
      "query_hash": "0F3A22B1C9D45E60",
      "statement_type": "SELECT",
      "executions": 412,
      "duration_us": { "min": 18422, "max": 3944180 },
      "captured_at_utc": { "first": "2026-08-04T14:20:11.914486Z",
                           "worst": "2026-08-04T15:02:47.331209Z" },
      "files": {
        "first":  "p_A1B2C3D4E5F60718.sqlplan",
        "worst":  "p_A1B2C3D4E5F60718.worst.sqlplan",
        "digest": "p_A1B2C3D4E5F60718.digest.json"
      },
      "finding_kinds": ["cardinality_misestimate", "spill_to_tempdb"]
    }
  ]
}
```

`finding_kinds` is the triage signal. Sort by `duration_us.max`, look at what has findings, open
those.

#### `<stem>.digest.json`

Everything known about one distinct plan: identity, execution count and duration/CPU ranges,
capture window, memory grant, both file references with their exact timestamps, and the full
findings list with per-finding detail.

```json
{
  "schema_version": 1,
  "plan_hash": "A1B2C3D4E5F60718",
  "plan_hash_source": "queryplanhash",
  "statement_count": 1,
  "query_hash": "0F3A22B1C9D45E60",
  "statement_type": "SELECT",
  "statement_text": "SELECT o.Id, o.Total FROM dbo.Orders o WHERE …",
  "statement_text_length": 1518,
  "executions": {
    "count": 412,
    "duration_us": { "min": 18422, "max": 3944180 },
    "cpu_time_us": { "min": 15000, "max": 3100000 },
    "captured_at_utc": { "min": "2026-08-04T14:20:11.914486Z",
                         "max": "2026-08-04T15:44:02.008731Z" }
  },
  "grant": { "serial_desired_kb": 1048576, "granted_kb": 524288,
             "max_used_kb": 8192, "dop": 8 },
  "files": { "first": { "path": "…", "captured_at_utc": "…",
                        "duration_us": 18422, "cpu_time_us": 15000 },
             "worst": { … } },
  "findings": [
    { "kind": "spill_to_tempdb", "node_id": 4,
      "detail": { "kind": "HashSpillDetails", "granted_kb": 524288,
                  "used_kb": 8192, "writes_to_tempdb": 4096 } }
  ]
}
```

Timestamps are ISO 8601 UTC with microsecond precision, chosen so that manual correlation against
the `executions` table is possible when the capture omitted `sqlserver.query_hash`. JSON is
UTF-8 without a BOM.

---

## Findings

Eight detection rules run over every ingested plan. They are pure functions over an `XDocument`
with no I/O, and they are all in `PlanFindings.cs`.

| Kind | Fires when | Detail payload |
|---|---|---|
| `memory_grant_oversized` | Granted / used memory exceeds the ratio threshold **above a floor**, or the serial desired memory exceeds an absolute threshold | `serial_desired_kb`, `granted_kb`, `max_used_kb`, `ratio` |
| `spill_to_tempdb` | A `SortSpillDetails` or `HashSpillDetails` element is present under the operator's `<Warnings>`, which is where the engine writes it | `kind`, `granted_kb`, `used_kb`, `writes_to_tempdb` |
| `cardinality_misestimate` | Estimated rows over all estimated executions versus actual rows differ by more than the ratio threshold, in either direction | `op`, `estimate_rows` (per execution), `estimate_executions`, `estimate_rows_all_executions`, `actual_rows`, `ratio` |
| `row_goal_defeated` | A **blocking** operator has `EstimateRowsWithoutRowGoal` far above `EstimateRows` | `op`, `rows`, `rows_without_row_goal`, `ratio` |
| `large_scan` | A `*Scan` operator over a table whose cardinality exceeds the threshold | `op`, `table`, `index`, `table_cardinality` |
| `excessive_rebinds` | `EstimateRebinds` above the threshold | `op`, `estimate_rebinds` |
| `missing_index` | A `MissingIndexGroup` is present | `impact`, `table`, `columns` |
| `plan_warning` | Any child of a `<Warnings>` element, and any of its boolean attributes set (`NoJoinPredicate`, `UnmatchedIndexes`, `SpatialGuess`, `FullUpdateForOnlineIndexBuild`), on an operator or on the statement's `QueryPlan` | `warning`, `detail` |

`plan_warning` reads two levels. An operator's `<Warnings>` carries spills, columns without
statistics and `NoJoinPredicate`; the statement's `QueryPlan/Warnings` carries
`MemoryGrantWarning`, `PlanAffectingConvert`, `Wait` and `UnmatchedIndexes`. That placement is
measured: 158 real plans, no exception, and reproduced on SQL Server 2025. A statement-level
warning has a null `node_id`. `detail` lists the element's attributes followed by the objects and
columns it names, as `Database.Schema.Table.Column`: that is how `ColumnsWithNoStatistics`, which
has no attribute, names its column, and how `UnmatchedIndexes` names the filtered index that
parameterization made unusable (listed in the sibling `QueryPlan/UnmatchedIndexes`).

### Thresholds

Defaults, from `PlanFindingThresholds`:

| Threshold | Default | Applies to |
|---|---|---|
| `RowGoalRatio` | 100 | `row_goal_defeated` |
| `GrantOversizeRatio` | 2.0 | `memory_grant_oversized`, granted/used |
| `GrantRatioFloorKb` | 16,384 (16 MB) | Floor below which the ratio rule is silent |
| `GrantAbsoluteKb` | 1,048,576 (1 GB) | `memory_grant_oversized`, on serial desired memory |
| `CardinalityRatio` | 10 | `cardinality_misestimate` |
| `LargeScanRows` | 1,000,000 | `large_scan` |
| `RebindsThreshold` | 1000 | `excessive_rebinds` |

There is no configuration plumbing for these yet. The record is public and has `init` setters, so
library callers can override them; the CLI always uses defaults.

### Rules with non-obvious reasoning

**The memory-grant floor.** A 1 MB grant oversized by a factor of 1000 is arithmetically true and
completely harmless. Reporting it drowns the handful of cases that actually cost the server
memory. So the *ratio* rule stays quiet below 16 MB. The *absolute* rule has no floor, because a
grant request so large the engine clipped it is a real defect that the ratio would never see, and
the granted figure can be modest precisely because of the clipping.

**Actual rows are summed across threads.** A parallel plan emits one
`RunTimeCountersPerThread` per thread. Comparing an estimate against a single thread's counter
would report a false underestimate by a factor of DOP on every parallel plan. `SumActualRows`
sums them, and returns null when the plan carries no runtime counters at all, which is how an
estimated plan is distinguished from a plan that genuinely returned zero rows.

**The estimate is scaled by the estimated number of executions.** `EstimateRows` is an estimate
*per execution*; `ActualRows` is cumulative over every execution. On the inner side of a nested
loops join, a seek estimated at one row and run once per outer row would otherwise read as an
underestimate by a factor equal to the outer row count. The rule compares `ActualRows` (summed
over threads) with `EstimateRows × (1 + EstimateRebinds + EstimateRewinds)`. The basis for that
number of executions is documented: `SET SHOWPLAN_ALL` exposes it as `EstimateExecutions`, "estimated
number of times this operator will be executed"
([SET SHOWPLAN_ALL](https://learn.microsoft.com/sql/t-sql/statements/set-showplan-all-transact-sql)),
and rebinds plus rewinds on the inner side of a loop join add up to the outer rows processed
([showplan operator reference](https://learn.microsoft.com/sql/relational-databases/showplan-logical-and-physical-operators-reference#rebinds-rewinds-and-end-of-scans)).
Measured on SQL Server 2025 (Standard Developer edition): a loop join over 1000 outer rows gave the
inner seek `EstimateRows="1"`, `EstimateRebinds="999"` and 1000 actual rows over 1000
executions. On the plans PlanInspector publishes, 25 of 97 verdicts change with this correction,
in both directions.

Batch mode, measured on the same instance: every batch-mode operator observed (columnstore scan,
hash join, hash aggregate, sort) carried `EstimateRebinds="0"`, `EstimateRewinds="0"` and one
execution, so the correction leaves them unchanged and their rows compare as before. Batch mode
on a parallel plan was not observed: Standard edition caps batch-mode DOP at 2 and these plans
ran on one thread. Unverified.

Adaptive joins could not be measured: they are an Enterprise feature
([editions and supported features of SQL Server 2025](https://learn.microsoft.com/sql/sql-server/editions-and-components-of-sql-server-2025)),
and the lab instance is Standard. Documented: an actual plan keeps both branches, and the branch
not taken shows zero actual rows ([adaptive joins](https://learn.microsoft.com/sql/relational-databases/performance/joins#adaptive-joins)).
That branch will read as an overestimate whatever the execution scaling, so a
`cardinality_misestimate` under an `Adaptive Join` operator should be checked against
`ActualJoinType` before it is believed. Whether the nested loops branch carries estimated rebinds
is unverified.

**Row goals are only checked on blocking operators.** Sort, Hash Match, Table Spool, Index Spool
and Window Spool must consume their entire input before yielding a row, so a `TOP` above them
cannot spare them the work, and their memory grant is still sized for the full cardinality. On a
non-blocking operator a defeated row goal is much less interesting.

---

## Working with the findings in SQL

The findings are also in the database, so you are not limited to the JSON:

```sql
-- how common is each problem?
SELECT kind, count(*) AS occurrences, count(DISTINCT plan_profile_id) AS plans
FROM plan_findings GROUP BY 1 ORDER BY 2 DESC;
```

```sql
-- the worst spills, with the plan file to open
SELECT pp.sqlplan_path,
       pp.duration_us / 1e3 AS ms,
       json_extract(f.detail_json, '$.writes_to_tempdb') AS tempdb_writes
FROM plan_findings f
JOIN plan_profiles pp USING (plan_profile_id)
WHERE f.kind = 'spill_to_tempdb'
ORDER BY ms DESC LIMIT 20;
```

```sql
-- plans where the estimate was off by the most
SELECT pp.plan_hash, f.node_id,
       json_extract(f.detail_json, '$.ratio') AS ratio,
       json_extract(f.detail_json, '$.op')    AS operator
FROM plan_findings f JOIN plan_profiles pp USING (plan_profile_id)
WHERE f.kind = 'cardinality_misestimate'
ORDER BY ratio DESC LIMIT 20;
```

Do **not** take `missing_index` DDL at face value. It is the engine's per-query suggestion with no
knowledge of your other queries, your write volume, or the indexes you already have. It tells you
where the optimizer felt underserved, not what to create.

---

## Query Store plans

`query-store-import` writes every plan it finds as a `.sqlplan` into `plans/`, with the path
recorded in `qds_plans.sqlplan_path`. `--no-plans` skips the files while keeping the metadata.

These are cached plans, not runtime observations, so they have no actual row counts and the
findings rules are not applied to them. Their value is coverage: Query Store holds plans for
queries that never appeared in your trace window.

See [query-store.md](query-store.md).

---

## Estimated plans

`EstimatedPlanService` reconstructs a runnable batch from a captured execution via
`ReplayBuilder`, opens a connection, issues `SET SHOWPLAN_XML ON`, and saves the resulting plan.

`SET SHOWPLAN_XML ON` is **compile-only**: the statement is never executed against the target.
No data is read or modified. This is the deliberate design choice that makes plan capture safe to
point at production.

`ReplayBuilder` reports its own confidence:

| Case | Kind | Confidence |
|---|---|---|
| Raw batch, replayed as-is | `RawBatch` | 1.0 |
| RPC call with a known procedure and parsed parameters | `ExecProc` | the minimum parameter parse confidence |
| `sp_executesql`, replayed as captured | `SpExecuteSql` | 0.7 |

Note that redaction interacts badly with replay: a batch whose parameters were masked or hashed
cannot be replayed with meaningful values. Estimated-plan capture on a redacted project will
compile a plan for `'****'`, which is not the plan production got.

`--sanitize-sql-text` interacts worse: `EstimatedPlanService.CaptureAsync` refuses outright,
before opening a connection, when the execution's `sql_text_policy` is `literals` rather than
`raw`. The stored text is `"… WHERE Email = ?"`, not valid T-SQL, so there is no plan to compile.
This mirrors the `export-events` refusal precedent (see [privacy.md](privacy.md)): fail loudly
with an explicit message rather than send something that will not parse to the server. Re-import
with `--sanitize-sql-text raw` if estimated plans are needed for that data.

There is no CLI command for this yet; it is a library entry point, exercised by an
environment-gated integration test that needs `SQLFERRET_TEST_CONN`. `CaptureAsync` has no
production caller today — the only invocations in the repository are in tests, and neither host
reaches it — so the refusal above is the contract for whoever wires it up next, not yet a
user-visible behavior.

---

## Sharing plans

`.sqlplan` files contain your entire data model: schema, table, column and index names, plus
statement text. Before sending one anywhere, obfuscate it:

```bash
sqlferret obfuscate-plan --in-dir ./audits/prod/plans --out-dir ./share/plans
```

See [privacy.md](privacy.md#plan-obfuscation).
