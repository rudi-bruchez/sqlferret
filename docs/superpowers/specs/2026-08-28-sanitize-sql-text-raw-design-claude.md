# Review — `sanitizeSqlTextRaw` design (2026-08-28)

Reviewer: Claude · Date: 2026-08-28
Target: `docs/superpowers/specs/2026-08-28-sanitize-sql-text-raw-design.md`
Method: every claim below is checked against the current tree (`src/SqlFerret.Core`), read at
review time. No code was modified. No build and no test was run — this is a document review.

**Verdict: do not implement as written.** The problem statement, the scope boundaries, the
`Literals` level, the versioning rule and the schema/migration section are sound and can be built
as specified. Three items are wrong against the code as it stands: what `Obfuscated` actually
produces (B1), a literal leak through `normalized_queries` that the design does not close (B2),
and the cost of the estimated-plan refusal (B3). B1 and B2 defeat the stated privacy goal; B3 is
a scope error, not a safety one.

---

## A. What holds up

Verified true, no action needed:

- **The gap is real.** `DuckDbProject.InsertExecution` (`Storage/DuckDbProject.cs:146`) binds
  `$raw` to `e.SqlTextRaw` unconditionally; nothing between `IngestionService` and the insert
  touches it. `docs/privacy.md`'s "Always stored" is accurate.
- **The `PrepareProc` precedent is exactly as described.** `Ingestion/IngestionService.cs:113-134`:
  placeholder `"(unparseable inputbuf; redacted)"` on `nq.TokenizeFailed`, `nq.NormalizedSql`
  otherwise, `NormalizedHash` preserved. Reusing that shape for `sql_text_raw` is consistent.
- **`Literals` costs nothing.** `IngestionService.cs:86` already computes
  `QueryNormalizer.Normalize(e.SqlTextRaw)` for every mapped event; the design reuses it.
- **The fingerprint claim is structurally guaranteed, not merely hoped for.** `nq` is computed
  from the original text before the rewrite, and both `InsertExecution` (`$nh`) and
  `UpsertSignature` (`$h`) read `r.Normalized.NormalizedHash`. Asserting it in a test is still the
  right call.
- **`IngestionOptions` is a positional record with trailing defaults**
  (`Ingestion/IngestionOptions.cs:7-8`) — appending `SqlText` breaks no call site.
- **`BeginRun` has exactly one caller** (`IngestionService.cs:20`), so the added parameter is
  cheap; it need not even carry a default.
- **The `executions` warning is correct.** `InsertExecution` uses
  `INSERT INTO executions VALUES (…)` with no column list — column-order-dependent. Adding no
  column there is the right decision, and the note is worth keeping.
- **The migration block exists** and already carries four `ADD COLUMN IF NOT EXISTS` statements
  (`Storage/DuckDbProject.cs:73-76`). NULL-reads-as-`raw` is the correct semantics; there is
  nothing to backfill.
- **Rejecting `--redaction` as the carrier** is right, and section 6's rejection of a
  `Func<string,string>` injected from the hosts matches CLAUDE.md section B.
- **`--sanitize-sql-text` parsing** can mirror `Program.cs:59-65` verbatim (`Enum.TryParse`,
  `ignoreCase: true`, explicit valid-values error, non-zero exit).
- **The two-extra-parses claim is exact**: `StatementTextRewriter.Rewrite` does one `Parse` and
  one `GetTokenStream` (`Obfuscation/StatementTextRewriter.cs:22-32`).

---

## B. Blocking findings

### B1 — `Obfuscated` does not tokenize identifiers. The level table and two tests are wrong.

**Evidence.** `StatementTextRewriter.Rewrite` maps an identifier only if it is *already* in the
map:

```csharp
// Obfuscation/StatementTextRewriter.cs:56-72
var lookup = map.BuildTextLookup();
...
if (t.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
{
    ...
    if (lookup.TryGetValue(key, out var tok2)) { sb.Append(...); continue; }
}
sb.Append(t.Text);          // unknown identifier → emitted verbatim
```

`BuildTextLookup` (`Obfuscation/ObfuscationMap.cs:66`) only enumerates entries already minted.
`map.Token(...)` — the call that mints a new token — is reached from the token-stream path for
**variables and `#temp` names only** (`StatementTextRewriter.cs:46-64`). Real table, column,
schema and alias names are minted by `PlanObfuscator` while obfuscating a plan, not by the
rewriter.

**Consequence.** At ingest time, on a fresh project, `obfuscation_map` is empty. `Obfuscated`
therefore produces: literals → `?`, comment bodies dropped, `@vars` and `#temps` tokenized,
**`dbo.Customers.Email` written out unchanged**. That is `Literals` plus comment stripping, at
double the parse cost — not the level the design sells.

Directly affected: the section 3 table row ("identifiers tokenized"), the section 4.2 consistency
argument (`Table1` means the same table in both places — vacuously true when nothing is
tokenized), and the tests `Obfuscated_TokenizesIdentifiers` and `Obfuscated_PopulatesProjectMap`,
both of which will go red and stay red.

**Options.** (a) Extend the rewriter to mint tokens for unknown identifiers — but the rewriter
cannot tell a table from a column from an alias in a token stream, so every name would land under
one kind, and it changes `Obfuscation`, which section 4.2 promises not to do. (b) Keep the
rewriter as is and re-scope the level honestly — rename it and describe it as "literals, comments,
variables and temp names", with identifier tokenization only for names a prior `obfuscate-plan`
run put in the map. (c) Drop the third level from this change and ship `Raw` / `Literals`.

(b) or (c). (a) is a real design problem masquerading as a small patch. Whichever is chosen,
section 3's table and section 9's tests must be rewritten to match, and section 4.2's "`Table1`
means the same table in both" must become conditional on the map being populated first.

### B2 — Literals leak through `normalized_queries.normalized_sql` on unparseable input.

**Evidence.** `TokenNormalizer.FallbackCollapse` (`Normalization/TokenNormalizer.cs:127`) is
`Regex.Replace(raw, @"\s+", " ").Trim().ToLowerInvariant()` — whitespace collapse and lowercase,
**literals fully intact**. The design says so itself in section 4.3, then only protects
`sql_text_raw`. Section 4.1's snippet passes `nq` through unchanged:

```csharp
buffer.Add(new PreparedRow(e with { SqlTextRaw = text }, nq, RedactParams(e)));
```

and `UpsertSignature` (`Storage/DuckDbProject.cs:166-174`) writes `n.NormalizedSql` into
`normalized_queries`. So on every tokenize failure the placeholder lands in `executions` while
`alice@example.com` lands, lowercased, in `normalized_queries` — same file, joinable on
`normalized_hash`. `PrepareProc` already avoids exactly this by substituting `safeNq`
(`IngestionService.cs:130`); the new path drops the guard.

**Fix.** Mirror `PrepareProc`: when the level is not `Raw` and `nq.TokenizeFailed`, build
`nq with { NormalizedSql = placeholder }` and put *that* in the `PreparedRow`. `NormalizedHash` is
unaffected, so section 4.1's fingerprint property survives. Have `SqlTextSanitizer.Apply` return
the substituted `NormalizedQuery` rather than only the text.

**Also document:** `UpsertSignature` is `ON CONFLICT … DO UPDATE SET last_seen_at`. A hash first
inserted by a `Raw` run keeps its raw `normalized_sql` forever; a later sanitized run into the
same project will not overwrite it. Sanitization is per-run, `normalized_queries` is project-wide.
A project is only safe to share if *every* run was sanitized — that belongs in `docs/privacy.md`
next to the `obfuscation_map` warning section 10 already plans.

Related, smaller, same section: at `Obfuscated`, `normalized_queries.normalized_sql` retains real
identifiers even on the success path (it comes from `QueryNormalizer`, not the rewriter). The
design never says so, and a reader will assume the whole project is tokenized.

### B3 — The estimated-plan refusal is not free, and section 7 says it is.

**Evidence.** `EstimatedPlanService` is `(string connectionString, string plansFolder)`
(`Server/EstimatedPlanService.cs:13`) and `CaptureAsync(ExecutionEvent ev, string planId, …)`. It
has no database handle. `ExecutionEvent` (`Model/ExecutionEvent.cs`) has **no `RunId`**, and
`WorkloadQueries.LoadExecution` (`Analysis/WorkloadQueries.cs:151-158`) does not select `run_id`.

So "`EstimatedPlanService` reads the run's `sql_text_policy` for the execution it was handed"
requires, at minimum: adding `run_id` to the `LoadExecution` projection, adding a field to the
`ExecutionEvent` model, and giving `Server` a way to query `ingestion_runs` — a `Server → Storage`
edge that section 6 does not mention while making a point of approving `Ingestion → Obfuscation`.

**Recommendation.** Put the refusal at the host boundary, where the project is already open: CLI
and TUI look up `sql_text_policy` for the execution's run through a one-line `WorkloadQueries`
helper and refuse before constructing the service. Same user-visible message, no model change, no
new Core edge. If the refusal must live in Core for symmetry with `export-events`, say so
explicitly and budget the model and query change in section 2's in-scope list.

---

## C. Fix before implementation (non-blocking)

1. **`SqlTextSanitizer.Apply` cannot detect the `Obfuscated` fallback.** `Rewrite` returns a bare
   `string` (`StatementTextRewriter.cs:17`) and swallows its fallback internally. Section 4.2
   promises "no changes to `StatementTextRewriter`" while section 4.3 requires both levels to
   increment one counter. Pick one: add a `Rewrite(…, out bool usedFallback)` overload (a change
   to `Obfuscation`), or use `nq.TokenizeFailed` as the proxy — both use `TSql160Parser` with the
   same `Parse`-then-`GetTokenStream` failure test, so they agree in practice. The proxy is
   cheaper; state it as a deliberate approximation rather than leaving the contradiction.

2. **Map durability vs. batched inserts.** Section 4.2 loads the map once and saves it "when the
   run finishes". `InsertBatch` commits every 5000 rows. A crash mid-import leaves committed
   `sql_text_raw` full of `#Temp3` and `@Param7` with **no `obfuscation_map` rows** — the
   "reversible with the map" property is gone for that project, silently. Save the map with each
   batch. `SaveObfuscationMap` is already `ON CONFLICT DO NOTHING` inside its own transaction, so
   repeated calls are cheap and safe.

3. **"Reversible: yes, with the map" is misleading.** Literals collapse to `?` at `Obfuscated`
   too, irreversibly. Only identifiers are reversible. Say "identifiers reversible with the map;
   literals are not".

4. **`SqlTextSanitizer.Apply`'s `map` parameter** is meaningless at `Raw` and `Literals`. Make it
   `ObfuscationMap?` and validate, or split the entry points. As written, `Literals` callers must
   invent a map.

5. **Section 8's "close to double".** Baseline is ~3 ScriptDom passes per event
   (`docs/development.md#known-gaps`); `Obfuscated` adds 2 → ~5, roughly +65%, and only on the
   parse work, not on the whole ingest (XELite read, DuckDB insert). "Materially more expensive,
   roughly +60-70% of parse cost" is defensible; "close to double" is not.

6. **Naming.** Three names for one concept: enum `SqlTextSanitization`, flag
   `--sanitize-sql-text`, config key `ingest.sanitizeSqlTextRaw`. The config key is the odd one —
   `Raw` is one of the *values*, so `sanitizeSqlTextRaw: "raw"` reads as a contradiction. Suggest
   `ingest.sqlTextSanitization`.

7. **`SqlFerretConfig` is a positional record** with five members
   (`Config/SqlFerretConfig.cs:6`). Adding a member is source-breaking for anything constructing it
   positionally — cheap to absorb, but section 3 should say so the way it does for
   `IngestionOptions`.

8. **Missing from section 10: the generated project `README.md`.** `Project/AuditProject.cs:136`
   and `:151` describe the redaction policy and what lands on disk; a project imported at
   `Literals` or `Obfuscated` would ship a README describing the wrong privacy posture.

9. **Missing from section 10: `plans/**/*.digest.json`.** Section 2 correctly puts it out of
   scope, but `docs/privacy.md` must then say plainly that a sanitized project can still carry
   real statement text in the plan digests — otherwise the feature creates false confidence, which
   is worse than the documented gap it replaces.

10. **Test gap.** `Sanitization_DoesNotChangeFingerprint` covers the hash. Add the mirror
    assertion for B2: at `Literals` with unparseable input, the literal is absent from
    **`normalized_queries.normalized_sql`**, not only from `executions.sql_text_raw`. That is the
    test that would have caught it.

---

## D. Observations, no action requested

- `StatementTextRewriter.Fallback` mutates the map (registers `@vars` and `#temps`,
  `StatementTextRewriter.cs:88-96`) before rebuilding the lookup. Ingest at `Obfuscated` therefore
  grows `obfuscation_map` from *every* unparseable batch, not only from plans. Harmless, and it
  reinforces C2.
- Section 5.1's caveat about map-dependent output is the most honest paragraph in the document.
  Keep it verbatim.
- Section 11's TUI deferral is correct: `ImportPresenter` constructs `IngestionOptions`
  positionally and compiles unchanged against the new trailing default.

---

## E. Suggested minimal edit set

1. Section 3 table, 4.2, 9: re-scope or drop `Obfuscated` (B1).
2. Section 4.1, 4.3: substitute `NormalizedQuery.NormalizedSql`, and add the cross-run
   `normalized_queries` caveat (B2).
3. Section 2, 6, 7: move the estimated-plan refusal to the hosts, or budget the model change (B3).
4. Section 4.2 / 4.3: resolve the fallback-detection contradiction (C1).
5. Section 4.2: save the map per batch (C2).
6. Section 3, 8, 10: naming, cost figure, the two missing doc targets (C3-C9).

None of this changes the shape of the design. `Raw` / `Literals`, the version constant, the three
`ingestion_runs` columns and the NULL migration semantics are ready to implement as written once
B2 is folded in.
