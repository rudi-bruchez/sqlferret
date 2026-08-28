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
2. **`AstClassifier`** determines the statement kind and the primary table.
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

`AstClassifier` parses the statement into an AST and takes the **first** statement of the batch:

| Kind | Primary table |
|---|---|
| `SELECT` | First named table in the FROM clause |
| `INSERT` | Insert target |
| `UPDATE` | Update target |
| `DELETE` | Delete target |
| `EXEC` | Procedure name |
| `OTHER` | none |

"First statement wins" is a real limitation for multi-statement batches. A batch that opens with
a `SET NOCOUNT ON` classifies as `OTHER`. This is a known simplification, not an accident.

### Fallback

When ScriptDom cannot parse the input, normalization does not fail: it falls back to collapsing
whitespace and lowercasing the whole string, and flags the event. That flag is counted in
`ingestion_runs.tokenize_failures`, so a trace full of dynamic SQL or non-T-SQL noise shows up as
a number rather than as mysteriously bad grouping.

Note that ScriptDom's `Parse()` is used to *detect* invalid input before `GetTokenStream()` is
trusted, because the token stream is lenient enough to happily tokenize `@@@ not sql ((` without
raising an error.

## Versioning

`QueryNormalizer.Version` is currently **1** and is persisted twice: on `ingestion_runs` and on
every row in `normalized_queries`.

`SqlTextSanitizer.Version` (also 1, on `ingestion_runs.sql_text_sanitizer_version`) is coupled to
it: at the `literals` level the stored statement text *is* the normalizer's output, so a rule
change in one should prompt a look at the other. See [privacy.md](privacy.md).

This exists so that a future change to the normalization rules is detectable rather than
silently corrupting comparisons. If you compare two projects, or two runs inside one project,
check that the versions match. Fingerprints from different normalizer versions are not
comparable, even when the SQL is identical.

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
