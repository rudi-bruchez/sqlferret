# Review — implementation plan `2026-08-28-sql-text-sanitization.md`

Reviewer: Claude · Date: 2026-08-28
Target: `docs/superpowers/plans/2026-08-28-sql-text-sanitization.md`
Against: `docs/superpowers/specs/2026-08-28-sanitize-sql-text-raw-design.md` (revision 2)
Method: every API shape, line reference, test-helper signature and call-site count in the plan was
checked against the tree as it stands. No code was modified. No build and no test was run.

**Verdict: ready to execute.** No blocking defect. The plan is faithful to revision 2, the six
tasks are correctly ordered by dependency, and the code it dictates compiles against the real
signatures as far as static reading can establish. Five items below are worth folding in before
Task 1 — one is a genuine privacy hole in the documentation task (C2), the rest are small.

---

## A. Verified against the code

Spot-checked every concrete claim, not a sample. All of these are correct:

| Plan claim | Checked |
|---|---|
| Migration block at `DuckDbProject.cs:73-76` | ✓ four `ADD COLUMN IF NOT EXISTS` lines, inside the same `CreateSchema` raw literal |
| `BeginRun` at `:103-121`, `FinishRun` at `:184-202`, `InsertExecution` positional | ✓ all three, verbatim |
| `Open` runs the migration on every open | ✓ `Open` → `CreateSchema` unconditionally, so the drop-and-reopen test in Task 2 is valid |
| 21 `BeginRun` call sites in tests, one `FinishRun` at `DuckDbProjectInsertTests.cs:33` | ✓ exactly |
| `IngestionService.cs:20-21` / `:23` / `:86-89` / `:101-105` | ✓ all four edit anchors land where the plan says |
| `IngestionService.cs:4` already imports `Normalization` | ✓ — no new using needed there |
| `DuckDbProject.cs:3` already imports `Normalization` | ✓ |
| `Program.cs` does **not** import `Normalization` | ✓ — the plan's "add if absent" is required, not optional |
| `Program.cs:59-65` redaction block, `Arg` helper, `:67-68` options | ✓ |
| `ImportPresenter.cs:16-17` builds the options; `project.Config` reachable | ✓ |
| `SqlFerretConfig` five-member positional record, `Load(string?)` | ✓ — a trailing defaulted sixth member is non-breaking, as the plan says and revision 2 asserts |
| `WorkloadQueries.LoadExecution` projection, indices 0-12, reader named `r` | ✓ index 13 is the next free slot |
| `ExecutionEvent` is an `init`-only record with no run identity | ✓ |
| `NormalizedQuery(NormalizedSql, NormalizedHash, StatementKind, PrimaryTable, TokenizeFailed)` | ✓ a record **class**, so `Assert.Same` in Task 1 is meaningful |
| `TokenNormalizer` fallback keeps literals | ✓ `FallbackCollapse` is whitespace-collapse + `ToLowerInvariant` |
| The unterminated-quote fixture actually fails tokenization | ✓ already proven by `BlockingIngestionTests.cs:122-160`, which uses the same `exec dbo.X @Code='…` shape for the same reason |
| The `Batch(...)` test helper | ✓ byte-for-byte the pattern at `IngestionServiceTests.cs:10-13`; `FakeEvent` is `internal` in the same test assembly |
| No positional `SELECT *` read of `ingestion_runs` anywhere | ✓ only named projections (`WorkloadQueries.cs:127`, four test queries), so three added columns break nothing |
| Test arithmetic | ✓ 6+4+5 = 15 after Task 3; 6+4+5+3+4 = 22 total |

Two design decisions the plan gets right and the spec does not:

- **`SqlFerretConfig.SqlTextPolicy`, not `SqlTextSanitization`.** The spec (§3) names the member
  after the enum type. Inside the record that name would shadow the type, and `Enum.TryParse<…>`
  in the same file would stop resolving. The plan's rename is correct — see C4.
- **`LEFT JOIN`, not `JOIN`, in `LoadExecution`.** The spec says "joins"; a missing
  `ingestion_runs` row would make the execution unloadable. The plan catches it.

Also correct and worth keeping: the constraint block forbidding the `Ingestion → Obfuscation`
edge, the explicit "do not add a column to `executions`" warning at Task 2 Step 3, and the
placeholder string being distinct from the blocking one.

---

## B. Nothing blocking

There is no finding in this section. Stated explicitly so the absence is not read as an omission:
I looked for a repeat of the revision-1 class of defect (a path that writes unsanitized text to
disk) and found none. `SqlTextSanitizer.Apply` is total over its three inputs, the
`normalized_queries` guard is present in the implementation *and* has a dedicated regression test
at both the unit (Task 1) and integration (Task 3) level, and the fingerprint is preserved by
construction rather than by care.

---

## C. Fold in before starting

### C1 — The new counter is never shown to anyone

`Program.cs:96-101` prints every existing counter, `planWriteFailures` included. The plan adds
`IngestionResult.SqlTextSanitizeFailures` (Task 3) and the `ingestion_runs` column (Task 2), then
never touches that summary line. `ImportProgress` / `ImportProgressText` are likewise untouched.

A counter nobody sees does not answer "how much of this project went through a path I should not
trust". Add one fragment to the CLI summary in Task 4, beside `tokenizeFailures`:

```
sqlTextSanitizeFailures={result.SqlTextSanitizeFailures}
```

and state deliberately, in the plan, that the live gauge is *not* extended (it is a progress
readout, not a provenance record). `CliSmokeTests` is sample-gated, so a changed summary line will
not be caught by a default `dotnet test` — check it by hand if it asserts on that string.

### C2 — `qds_query_text.query_sql_text` is raw SQL text this option never touches

`DuckDbProject.QueryStore.cs:30` declares `qds_query_text(run_id, query_text_id, query_sql_text …)`
and `QueryStoreImportService.cs:106-114` reads `sys.query_store_query_text` and stores it
verbatim. It lands in the **same `sqlferret.duckdb`** as `executions`.

So a project that ran `query-store-import` carries real, literal-bearing statement text regardless
of `--sanitize-sql-text`. Task 6 Step 1's point 4 lists `plans/**/*.digest.json` and `.sqlplan`
files as the remaining exposures and omits this one — the largest of the three, since Query Store
text is untruncated.

Add it to point 4, and add it to the spec's §2 out-of-scope list so it is a recorded decision
rather than an oversight. Same rationale the plan already gives for point 4: a feature that
creates false confidence is worse than the documented gap it replaces.

### C3 — `--redaction off` plus `--sanitize-sql-text literals` produces a misleading project

`PrepareProc` (`IngestionService.cs:120-123`) returns `p.InputBufRaw` unchanged when
`Redaction == Off`, and `Ingest` keeps the raw blocking XML under the same condition
(`IngestionService.cs:45`). Neither is affected by the new option — correctly, they are orthogonal
knobs, which the spec insists on.

The result is a combination a user can reach in one command: `executions.sql_text_raw` sanitized,
`blocking_processes.inputbuf` and `blocking_reports.raw_xml` still holding real statement text and
literals. Nothing in Task 6 warns about it, and `docs/privacy.md` already flags `off` as its one
sharp edge. One sentence in Task 6 Step 1, in the same list as point 4.

### C4 — Task 4 Step 2's expected-failure message contradicts the plan's own naming

Step 2 says the build will fail because "`SqlFerretConfig` has no `SqlTextSanitization` member",
but the member the plan creates is `SqlTextPolicy` (Step 3, and the global constraint "never name
a member the same as the enum type"). Leftover from the spec's wording.

Fix the sentence, and — more important — **update spec §3**, which still reads
`string SqlTextSanitization = "raw"`. An implementer reading the spec instead of the plan would
write the shadowing name and then fight `Enum.TryParse<SqlTextSanitization>` inside that file.

### C5 — Task 4 Step 7's manual check creates a project directory as a side effect

`dotnet run … import nope.xel --project /tmp/sfplan --sanitize-sql-text bogus` opens the project
*before* validating the flag (`Program.cs:56-58` → `OpenProject()`, then the redaction block at
`:59`). `AuditProject` creates the directory on first use, so the check leaves a project tree
behind — under `C:\tmp\sfplan` on this machine, since the plan's path is POSIX. The ordering claim
itself is right: the flag error does fire before the missing-file error, which `ImportRunner`
raises later.

Use an explicit disposable path and say the directory is expected to appear, so the final
`git status` step is not read against a surprise.

---

## D. Nits — take or leave

1. **Task 3, `Raw_keeps_the_statement_verbatim`** should also assert
   `Assert.Equal(0, result.SqlTextSanitizeFailures)`. It is the only cheap guard against the two
   counters double-counting one event.
2. **NULL → `raw` is never normalized in code.** Task 2's migration test asserts NULL, and Task 5
   treats `null` as "do not refuse". That is consistent and fine, but the mapping the docs promise
   ("NULL reads as `raw`") exists only as a convention. If a host ever displays the policy, it must
   apply the default itself — worth one line in `docs/data-model.md` (Task 6 Step 4 already says
   it; keep that wording exact).
3. **Task 4 Step 3** shows `string sqlTextPolicy = "raw";` twice — once appended to the locals
   line, once as "declare beside the other locals". Keep one.
4. **Task 6 misses two small doc surfaces**: `docs/privacy.md:201-206` carries a
   "review `sql_text_raw` before sharing" SQL recipe that reads oddly once sanitization exists, and
   `docs/normalization.md` documents the `QueryNormalizer.Version` rule that the new constant is
   explicitly coupled to (spec §5.1). Both are a sentence each.
5. **Task 2's migration test** proves the ALTERs run on reopen, which is the thing worth proving.
   Its name (`…_migrates_and_reads_null`) is more accurate than the spec's
   `LegacyProject_MigratesAndReadsAsRaw`. Keep the plan's.

---

## E. One expectation to correct, not a defect

`EstimatedPlanService.CaptureAsync` has **no production caller**. The only invocation in the
repository is `EstimatedPlanServiceTests.cs:46`; neither host reaches it. The chain the spec §7
describes — `LoadExecution` → `ReplayBuilder` → `CaptureAsync` — exists as a library capability
that nothing wires up yet.

Task 5 is still worth doing exactly as written: it is the contract for whoever wires it, and the
gate belongs with the field that motivates it. But two things follow:

- The refusal message will not be seen by a CLI or TUI user today. `docs/execution-plans.md`
  (Task 6 Step 5) should describe it as the behavior of the estimated-plan capture path, not as
  something a user will hit from a command.
- Task 5's `dotnet test --filter "…|EstimatedPlanServiceTests"` will skip without
  `SQLFERRET_TEST_CONN`, as the plan says. The two new `CaptureAsync` tests are *not* gated —
  they rely on the refusal (and, for the null-policy case, on a connection failure) — so they will
  run in the default gate. The second one, `…does_not_refuse_when_the_policy_is_raw_or_unknown`,
  depends on `SqlConnection` failing fast against `Server=(invalid);Connect Timeout=1`. That is a
  network-dependent assertion in an otherwise offline suite; if it proves slow or flaky, assert on
  the exception type instead of driving a real connection.

---

## F. Summary

| | |
|---|---|
| Blocking | none |
| Fold in first | C1 (surface the counter), C2 (Query Store text — the real gap), C3 (`off` + `literals`), C4 (naming, and fix spec §3), C5 (manual-check side effect) |
| Nits | D1-D5 |
| Expectation to correct | E — `CaptureAsync` has no caller yet |

The task decomposition, the TDD ordering, the trailing-defaulted-parameter discipline and the
per-task validation commands are all sound. Execute in order; Tasks 1-3 are the ones that carry
the privacy guarantee, Task 6 is the one that keeps it honest.
