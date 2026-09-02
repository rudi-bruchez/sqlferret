# Query normalization

Normalization is the operation the entire tool rests on. It turns

```sql
SELECT o.Id, o.Total FROM dbo.Orders o WHERE o.CustomerId = 4218 AND o.Status IN (1, 2, 5)
```

into

```sql
select o.Id , o.Total from dbo.Orders o where o.CustomerId = ? and o.Status in (?)
```

and then into a SHA-256 fingerprint. Two executions that differ only in their literals produce the
same fingerprint, which is what makes "this one query shape costs 40% of the server" a
computable statement rather than an intuition.

## How it works

`QueryNormalizer.Normalize` does three things and returns a `NormalizedQuery`:

1. **`TokenNormalizer`** rewrites the statement text.
2. **`AstClassifier`** determines the statement kind, the primary table and the target object.
3. **`Fingerprint`** hashes the normalized text with SHA-256 and returns lowercase hex.

Both steps 1 and 2 use `Microsoft.SqlServer.TransactSql.ScriptDom` with a `TSql160Parser`. This
is the same parser SQL Server tooling uses. It is not a regex approximation, which matters the
moment you meet a string literal containing the word `FROM`, a bracketed identifier with a space
in it, or a nested comment.

### Token rewriting

| Token class | Treatment |
|---|---|
| Literals — integer, numeric, money, real, hex, ASCII string, Unicode string | Replaced with `?` |
| An explicit allow-list of keywords (`SELECT`, `FROM`, `WHERE`, `JOIN`, `EXEC`, `CASE`, `UNION`, …) | Lowercased |
| Everything else, identifiers included | **Left exactly as written** |
| Whitespace, line comments, block comments | Collapsed to a single space |

Identifiers keeping their original casing is deliberate. `dbo.Orders` and `dbo.orders` stay
distinct shapes, because on a case-sensitive collation they *are* distinct, and flattening them
would silently merge two different objects.

The keyword list is an explicit allow-list rather than a heuristic. A heuristic that tried to
guess "is this token a keyword" would eventually lowercase an identifier that happens to collide
with a reserved word, and that failure is both silent and unrecoverable.

Finally, `in (?, ?, ?)` collapses to `in (?)`, so an application that builds `IN` lists of varying
length still groups into one shape.

### Classification

`AstClassifier` parses the statement into an AST and walks it with a visitor. It produces three
values at once — `statement_kind`, `primary_table` and `target_object` — and they are **locked
together on the first match**: whichever statement claims the kind also supplies the table and the
target, so you never get the kind of one statement paired with the table of another.

DML and procedure calls:

| Kind | Primary table | Target object |
|---|---|---|
| `SELECT` | First named table in the FROM clause | — |
| `INSERT` | Insert target | — |
| `UPDATE` | Update target | — |
| `DELETE` | Delete target | — |
| `MERGE` | Merge target | — |
| `EXEC` | Procedure name | — |
| `FETCH` | — | Cursor name |
| `OTHER` | — | — |

DDL is classified too, which is what makes an upgrade-script audit possible. The kind carries the
**form** of the statement, `primary_table` the table it acts on, and `target_object` the
sub-object — the column, constraint, index, procedure or view being touched:

| Kind | Primary table | Target object |
|---|---|---|
| `CREATE TABLE`, `DROP TABLE`, `TRUNCATE TABLE` | The table | — |
| `ALTER TABLE ADD COLUMN`, `ALTER TABLE ALTER COLUMN` | The table | The column |
| `ALTER TABLE ADD CONSTRAINT`, `ALTER TABLE DROP` | The table | The constraint or element |
| `ALTER TABLE CHECK CONSTRAINT`, `ALTER TABLE NOCHECK CONSTRAINT` | The table | The constraint |
| `ALTER TABLE REBUILD`, `ALTER TABLE SET`, `ALTER TABLE ADD` | The table | — |
| `ALTER TABLE SWITCH` | The source table | The target table |
| `ALTER TABLE SPLIT PARTITION`, `ALTER TABLE MERGE PARTITION` | The table | — |
| `ALTER TABLE ENABLE/DISABLE TRIGGER` | The table | The trigger |
| `ALTER TABLE ENABLE/DISABLE CHANGE_TRACKING` | The table | — |
| `ALTER TABLE ENABLE/DISABLE FILETABLE_NAMESPACE` | The table | — |
| `CREATE INDEX`, `CREATE CLUSTERED INDEX`, `ALTER INDEX`, `DROP INDEX` | The table | The index |
| `CREATE STATISTICS`, `UPDATE STATISTICS` | The table | The statistics object |
| `CREATE PROCEDURE`, `ALTER PROCEDURE`, `DROP PROCEDURE` | — | The procedure |
| `CREATE FUNCTION`, `ALTER FUNCTION`, `DROP FUNCTION` | — | The function |
| `CREATE VIEW`, `ALTER VIEW`, `DROP VIEW` | The view | — |
| `CREATE TRIGGER`, `ALTER TRIGGER`, `DROP TRIGGER` | The table | The trigger |
| `CREATE PARTITION FUNCTION`, `CREATE PARTITION SCHEME` | — | The object |
| `ALTER DATABASE` | — | The database |

Creation and alteration stay distinct — the node type says which one happened, and the difference
carries meaning in an upgrade audit. `CREATE OR ALTER` is filed with the creations, because the
text does not say which of the two actually occurred.

**The first statement that a visitor recognizes wins.** That is not the same as the first
statement of the batch: a statement with no visitor is skipped rather than claiming the batch, so
`SET NOCOUNT ON; SELECT a FROM dbo.T` classifies as `SELECT` on `dbo.T`, not as `OTHER`. A batch
in which *nothing* is recognized classifies as `OTHER`.

Module bodies do not leak. `ALTER PROCEDURE dbo.P AS BEGIN UPDATE dbo.T … END` classifies as
`ALTER PROCEDURE` with target `dbo.P` — not as `UPDATE` on `dbo.T`. Without that rule, a
`GROUP BY primary_table` would charge the cost of replacing a procedure to a table the statement
never touches, which is a wrong value rather than a missing one.

**`primary_table` keeps the name as written**, schema prefix included when present. The same table
written `dbo.T` in one statement and `T` in another produces two distinct values, and a
`GROUP BY primary_table` splits it across two rows. Check before you aggregate.

### Fallback

When ScriptDom cannot parse the input, normalization does not fail: it falls back to collapsing
whitespace and lowercasing the whole string, and flags the event. That flag is counted in
`ingestion_runs.tokenize_failures`, so a trace full of dynamic SQL or non-T-SQL noise shows up as
a number rather than as mysteriously bad grouping.

Note that ScriptDom's `Parse()` is used to *detect* invalid input before `GetTokenStream()` is
trusted, because the token stream is lenient enough to happily tokenize `@@@ not sql ((` without
raising an error.

## Versioning

`QueryNormalizer.Version` is currently **3** and is persisted twice: on `ingestion_runs` and on
every row in `normalized_queries`.

This exists so that a change to the normalization rules is detectable rather than silently
corrupting comparisons. If you compare two projects, or two runs inside one project, check that
the versions match. Fingerprints from different normalizer versions are not comparable, even when
the SQL is identical.

The version covers **normalization and classification together**. It is bumped whenever
`AstClassifier` would return a different answer for the same input — not only when the token
rewriting changes. Without that, an already-imported project would silently keep its old
classification and never be offered an upgrade.

A project imported by an older version does not need re-importing. `reclassify` re-runs
classification in place against the stored statement text:

```bash
sqlferret reclassify --project ./audits/prod-2026-08
```

The commands that read a project warn on `stderr` when they detect a stale classification. Two
limits are worth knowing: a signature for which no source text was retained — neither an execution
nor a blocking input buffer — cannot be reclassified at all and is left as it is, and statements
that remain unclassified after the pass are reported so that `--force` can replay them once the
classifier gains a visitor for them. See
[cli-reference.md#reclassify](cli-reference.md#reclassify).

## What this does and does not guarantee

**It does guarantee** that two executions of the same statement with different literal values land
on the same fingerprint, and that the fingerprint is stable across machines, runs and projects at
a given normalizer version.

**It does not guarantee** that two *semantically* equivalent statements group together. These are
all different shapes:

- `SELECT a, b FROM t` and `SELECT b, a FROM t`
- `WHERE x = 1 AND y = 2` and `WHERE y = 2 AND x = 1`
- The same query with and without a schema prefix
- The same query formatted by two different ORMs

That is the correct behavior for a workload analyzer. Those really are distinct texts that the
server compiles and caches distinctly, and collapsing them would hide a genuine plan-cache
problem.

**Parameter values are not part of the fingerprint**, which is the whole point, but it means a
single shape can hide wildly different execution costs. The `ParameterImpact` query in
`WorkloadQueries` groups a shape's executions by parameter value precisely so you can find the
sniffing case, and the "spread" query in [data-model.md](data-model.md#useful-queries) surfaces
candidates.

## Reuse in blocking analysis

Blocked-process reports carry an input buffer, which is a SQL fragment. It goes through the same
normalizer, and the resulting hash is stored as `blocking_processes.inputbuf_fingerprint`. That
single decision is what lets you join blocking incidents to workload shapes and ask "which query
shape blocks the most", instead of eyeballing SPIDs.

## Related

- [data-model.md](data-model.md) — the `normalized_queries` table
- [privacy.md](privacy.md) — why normalization is *not* redaction
