# Privacy and redaction

Production traces contain production data. This page states plainly what SQLFerret writes to
disk, what it does not, and what you control.

Read the [threat model](#threat-model) section before deciding a policy is good enough for your
situation.

---

## What lands on disk

| Artifact | Contains | Controlled by |
|---|---|---|
| `executions.sql_text_raw` | The statement as captured, including any inlined literals | `--sanitize-sql-text` (default `raw`: always stored) |
| `normalized_queries.normalized_sql` | The statement shape; literals `?`. Real literals on tokenize failure unless sanitized | Both policies, whichever is stricter |
| `execution_parameters.value_text` | Extracted RPC / `sp_executesql` parameter values | The redaction policy |
| `blocking_reports.raw_xml` | The full blocked-process report, including both input buffers | Retained only under `--redaction off` **and** `--sanitize-sql-text raw` |
| `blocking_processes.inputbuf` | The input buffer text | Same gate; otherwise stored normalized |
| `deadlock_reports.graph_xml` | The deadlock graph | Same gate, otherwise stored as `<redacted/>` |
| `plan_profiles.statement_text` | Statement text inside `sqlferret.duckdb` itself | Not sanitized by `--sanitize-sql-text` |
| `blocking_processes.inputbuf` reached through `export-health --format json` | The blocked process's input buffer, for blocking seen inside a diagnostics cycle | Same gate as any input buffer — but the **Markdown rendering omits it entirely**, so the two formats of one command disclose different things. The digest says so in a note |
| `plans/**/*.sqlplan` | Showplan XML: schema, table, column and index names, and sometimes literal predicate values | `obfuscate-plan`, after the fact |
| `health_pending_io.file_path` | Server-side file paths: instance name, drive layout, database file names | **Nothing.** Stored verbatim under every redaction mode, `full` included |
| `health_cpu_requests.session_id`, `.command` | Session id and command class (`SELECT`, `BACKUP DATABASE`), not statement text | Nothing |
| `health_memory_entries.description`, `.report_name` | SQL Server's own memory counter names | Nothing |
| `health_samples.component` | The five documented components, **plus one row per Always On availability group** — and an availability group is named by whoever configured the server, often after a business unit, an application or the customer | Nothing. Stored and printed verbatim under `full`; the digest names them in a note |
| `health_metrics.name`, `.value_text` | Engine counter names, and engine strings such as `lastNotification` and `sickSpinlockType` | Nothing |
| `health_waits.wait_type`, `health_pending_tasks.entry_point`, `health_cpu_requests.task_address` | Engine-defined enumerations and addresses, not customer naming | Nothing |
| `plans/**/*.digest.json` | Plan metrics plus a truncated `StatementText` | Not redacted |
| `obfuscation_map` table and `*.map.json` | The reverse mapping from tokens to real identifiers | Nothing. This *is* the key. |

### The one thing to internalize

**Redaction covers extracted parameters, not statement text.**

A parameterized call gets its values redacted:

```sql
EXEC dbo.GetCustomer @Email = 'alice@example.com'   -- @Email is hashed, always
```

A batch with inlined literals does not:

```sql
SELECT * FROM Customers WHERE Email = 'alice@example.com'   -- stored verbatim
```

Normalization replaces literals with `?` in `normalized_queries.normalized_sql`, but by default
`executions.sql_text_raw` keeps the original, and a tokenize failure leaves real literals in
`normalized_sql` too. Normalization is a grouping mechanism, not a privacy mechanism on its own.
If your workload inlines literals, redaction alone will not protect you — the statement text
needs its own control. That control is `--sanitize-sql-text`, covered next.

### Statement-text sanitization

1. **The levels.** `raw` (default, unchanged) and `literals` (statement text stored with literals
   collapsed to `?`). Set per import with `--sanitize-sql-text`, or in `sqlferret.config.json`
   under `ingest.sqlTextSanitization`. Independent of `--redaction` — the two flags control
   different columns and neither implies the other.
2. **`literals` removes values, not schema.** Table, column and procedure names remain in both
   `sql_text_raw` and `normalized_sql`. A sanitized project still tells a reader your data model.
   This includes `sp_executesql` RPCs: the inner statement passed as `N'...'` is unwrapped and
   normalized in place, so its identifiers survive too — `exec sp_executesql N'SELECT Name FROM
   dbo.Customers WHERE Email = @e',N'@e nvarchar(50)',@e='...'` becomes `exec sp_executesql
   N'select Name from dbo.Customers where Email = @e',?,@e=?`. The inner query text is the *only*
   thing kept from the call — every other argument, including the parameter-declaration literal
   (`N'@e nvarchar(50)'`), collapses to `?` like any other literal. Parameter names and types are
   not lost to `literals`: they're already recorded per execution in `execution_parameters`
   (`name`, `sql_type_guess`) — but only when parameter rows are persisted at all, which is every
   redaction policy *except* `off` (`off` stores no `execution_parameters` rows for any execution,
   full stop). Regardless, that overlap is not why the pass-through was removed: it's removed
   because it repeatedly leaked a value — several rounds of review each found a new way to hide
   one inside it — which is reason enough on its own. The unwrap only recognizes the positional
   literal form, `exec sp_executesql N'...', ...` — the statement argument immediately following
   the procedure name, as a string literal. When the statement argument isn't a literal (passed
   via a variable, e.g. `exec sp_executesql @stmt, ...`), passed by name (`exec sp_executesql
   @stmt = N'...', @params = N'...'`), invoked through a bracketed identifier (`[sys].
   [sp_executesql]`), or the inner statement fails to parse, the unwrap is skipped and the whole
   call falls back to the fully collapsed form, same as any other unparseable statement — a loss
   of readability, not of privacy: no value survives either way, but a user seeing `exec
   sp_executesql ?,?` where they expected readable SQL should know it fell back rather than
   failed. A double-quoted token (`SET QUOTED_IDENTIFIER OFF`) is genuinely ambiguous — the
   capture never records the session's setting — so `literals` fails safe and collapses it to `?`
   too, in both `sql_text_raw` and `normalized_sql`; a legitimately double-quoted identifier is
   lost along with it. Bracketed identifiers (`[Order Details]`) are unaffected.
3. **A project is only safe to share if every run in it was sanitized.**
   `normalized_queries` is project-wide, and its `ON CONFLICT` upsert updates only
   `last_seen_at` — a query shape first seen during a `raw` import keeps that raw text forever,
   and a later `literals` import into the same project will not clean it up. Check with:
   ```sql
   SELECT run_id, sql_text_policy FROM ingestion_runs;
   ```
   Every row must read `literals` before the project is safe to hand out.

   The same first-writer-wins upsert also cuts the other way, as a utility loss rather than a
   privacy one: if a `literals` import hits a query shape whose statement tokenize-fails first,
   that hash's `normalized_sql` is pinned to `"(unparseable sql text; redacted)"` for the life of
   the project. A later `raw` import of the same shape cannot repair it — `last_seen_at` moves,
   the placeholder text does not.
4. **A sanitized project can still carry real statement text elsewhere — four places, largest
   first.**
   - `qds_query_text.query_sql_text`. A project that ran `query-store-import` stores Query Store
     statement text verbatim and untruncated, in the same `sqlferret.duckdb`, regardless of
     `--sanitize-sql-text`. This is the largest of the four, and the one most likely to surprise
     you.
   - `plan_profiles.statement_text`, also inside `sqlferret.duckdb` itself, this option does not
     touch — the most surprising of the four, since it sits in the database the user thinks is
     sanitized.
   - `plans/**/*.digest.json` — a truncated `StatementText` this option does not touch.
   - `.sqlplan` files — statement text until `obfuscate-plan` rewrites them.
5. **Statement text obeys the stricter of the two policies, so `off` plus `literals` costs you
   the blocking XML.** An input buffer is statement text, so `--sanitize-sql-text` governs it as
   much as `--redaction` does. The two compose rather than ignoring each other: verbatim statement
   text is retained only when redaction is `off` **and** the text policy is `raw`.

   The consequence is that `--redaction off --sanitize-sql-text literals` retains **no**
   `blocking_reports.raw_xml` and stores `<redacted/>` for `deadlock_reports.graph_xml`, exactly
   as a `masked` import would. `blocking_processes.inputbuf` is still stored, normalized. The
   reason is blunt: those two columns hold whole XML documents whose `inputbuf` nodes carry the
   literals, and nothing here rewrites XML. Retaining them under a policy that asked for literals
   to go would contradict that policy from the next column over, so the honest answer is not to
   retain them. `export-events` therefore has nothing to export from such a run — see
   [cli-reference.md](cli-reference.md#export-events).

   If you want both the raw blocking XML and sanitized statement text, you cannot have them in one
   project: import twice, under two policies.
---

### The health tables sit outside the policy table

`import` on a `system_health` capture writes eight `health_*` tables. Their contents come from
SQL Server's own diagnostics, not from user queries, and **no redaction mode touches them**. That
is deliberate — they hold counters, wait types and engine flags — with one consequence worth
stating plainly: `health_pending_io.file_path` discloses your instance name, drive layout and
database file names, and `--redaction full` does not remove it. If you share a project or an
`export-health` digest, you share that.

Blocking reports lifted out of a diagnostics cycle are the exception that proves the rule: they
carry statement text, so they go through `IngestionService.PrepareProc` exactly like
event-sourced reports and inherit both policies, with no separate gate.

## Redaction policies

Set per import with `--redaction`, or in the project's `sqlferret.config.json` under
`ingest.redactionPolicy`. Applied in `IngestionService` **before** the row is built, so an
unredacted value never reaches the storage layer at all.

| Policy | Stored value | `value_redacted` |
|---|---|---|
| `off` | `""` — the value is discarded | `true` |
| `hash` | SHA-256 hex of the value | `true` |
| `masked` *(default)* | `*` repeated, 1 to 8 characters, length clamped | `true` |
| `full` | The value, verbatim | `false` |

Choosing between them:

- **`hash`** when you need to know whether two executions used the *same* value without knowing
  what it was. This is the right default for parameter-sniffing analysis: it keeps
  `ParameterImpact` grouping meaningful while revealing nothing.
- **`masked`** when you only need to see that a parameter existed and roughly how long it was.
- **`full`** only on a workload you own end to end, on a machine you control.
- **`off`** when parameter values must not exist anywhere. Note the caveat below.

### Sensitive-name override

Regardless of policy, a parameter whose **name** contains `password`, `token`, `secret` or
`email` (case-insensitive substring match) is forced to `hash`. `full` does not override this.

The list is a constructor argument on `RedactionPolicy`, so it is customizable from library code,
though nothing currently exposes it through configuration.

### The sharp edge in `off`

`off` is not "no redaction". It means "do not store parameter values". It is the *most* private
setting for parameters.

But the same value simultaneously **enables** retention of blocked-process XML, deadlock graphs
and input buffers, because those are only kept when nothing is being redacted. So:

- `--redaction off` (with the default `--sanitize-sql-text raw`) → no parameter values, **full
  blocking and deadlock XML on disk**, and the input buffers verbatim
- `--redaction off --sanitize-sql-text literals` → no parameter values, **no blocking or deadlock
  XML**, input buffers stored normalized
- `--redaction masked` → masked parameter values, **no blocking or deadlock XML at all**

Which means `export-events` only ever has something to export from runs imported with `off`. If
you run it against a `masked` project it writes nothing and tells you to re-import. That is a
deliberate refusal, not a bug.

If you need both parameter privacy and blocking XML, you currently have to import twice, into two
projects, under two policies.

`--sanitize-sql-text literals` narrows `off` rather than widening it: it takes the blocking and
deadlock XML away too. `off` plus `literals` is a real, supported combination — it is the private
one, and it is the one that leaves `export-events` nothing to do. See point 5 above.

---

## Plan obfuscation

`.sqlplan` files leak your data model: every schema, table, column, index and statistics name is
in there, along with the statement text. `obfuscate-plan` rewrites them.

```bash
sqlferret obfuscate-plan --in-dir ./plans --out-dir ./share/plans
```

| Kind | Token |
|---|---|
| Database | `Db1`, `Db2`, … |
| Schema | `Schema1`, … |
| Table | `Table1`, … |
| Temp table | `#Temp1`, … (the `#` is part of the prefix, so tokens stay valid identifiers) |
| Column | `Col1`, … |
| Index | `Idx1`, … |
| Statistics | `Stat1`, … |
| Parameter | `@Param1`, … |
| Alias | `Alias1`, … |

Details that matter in practice:

- **`sys` and `INFORMATION_SCHEMA` are preserved.** So are the engine-internal `Worktable` and
  `Workfile`. Everything else is mapped, `tempdb` included, because user temp tables live there.
- **Temp-table name mangling is normalized.** SQL Server rewrites `#Foo` to
  `#Foo_______________________________0000ABCD`. Both forms map to the same token, so the plan
  stays readable. A name that genuinely ends in underscores without a hex run is left alone.
- **Statement text is rewritten too**, through ScriptDom, with literals replaced. When the text is
  truncated badly enough that it will not parse, a regex fallback handles the leading DDL clause.
- **The map is written outside the output folder.** For `--in-dir`, the default map path is a
  *sibling* of `--out-dir` named `<out-dir-name>.map.json`. That is intentional: the folder you
  are about to zip and send should not contain its own decryption key.

### The map is the secret

Three places hold reversible mappings:

- `<file>.map.json` next to a standalone or folder obfuscation output
- `<plan-id>.map.json` in the project's `plans/` folder
- The `obfuscation_map` table inside `sqlferret.duckdb`

Anyone holding a map plus an obfuscated plan has the original plan. Treat maps as credentials.
When you share obfuscated plans, share the `.anon.sqlplan` files only.

A project-scoped obfuscation (`--project --plan-id`) deliberately persists the map into the
database so tokens stay stable across every plan in the project. That consistency is what makes
several obfuscated plans comparable to each other, and it is also what makes the project file
sensitive.

---

## Secrets

Connection strings and API keys go in `.env`, never in tracked files.

- `sqlferret.config.json` references them as `${VAR}`, interpolated at load time.
- `.env` is gitignored; `.env.example` is committed as a template.
- **Real environment variables win over `.env`.** `DotEnv` only sets keys that are absent.
- A missing `.env` is a silent no-op, so nothing breaks in an environment where the variables come
  from a secret manager instead.
- Both the project directory and the working directory are searched, project first.

See [configuration.md](configuration.md).

---

## Threat model

SQLFerret's redaction is designed against **accidental disclosure**: a project file ending up in a
ticket attachment, a shared drive, a support case, or an LLM context window. It reduces what a
casual reader learns.

It is **not** designed against a determined adversary who has the file. Specifically:

- `hash` is an unsalted SHA-256. A low-cardinality column (a status code, a country, a boolean)
  is trivially reversed by a dictionary attack. Hashing tells you *sameness*, not secrecy.
- `masked` leaks value length, clamped at 8.
- Raw statement text is always present, so a workload with inlined literals is not protected at
  all.
- Obfuscated plans plus their map are fully reversible, by design.
- Nothing is encrypted at rest. A project directory is as sensitive as the trace it came from.

If the requirement is "this data must not leave the building", the control is where you put the
project directory, not which redaction flag you passed.

---

## A safe default recipe

For an audit you intend to share with a vendor, a consultant or a model:

```bash
# 1. import with parameter values hashed, so grouping still works
sqlferret import ./logs --project ./audits/share --redaction hash

# 2. obfuscate the plans, keeping the map out of the shareable folder
sqlferret obfuscate-plan --in-dir ./audits/share/plans \
                         --out-dir ./audits/share-anon/plans

# 3. share ./audits/share-anon/plans and the digests, NOT the .duckdb file
#    and NOT ./audits/share-anon.map.json
```

Then review `executions.sql_text_raw` before sharing anything derived from the database itself.
This recipe only applies to runs imported at `raw` — `ingestion_runs.sql_text_policy` tells you
which those are; a `literals` run has already had its literals collapsed.

```sql
SELECT sql_text_raw FROM executions
WHERE sql_text_raw NOT LIKE '%@%'
ORDER BY length(sql_text_raw) DESC LIMIT 50;
```

Statements with no parameter markers are the ones most likely to carry inlined literals.
