# Configuration

Two files, both optional, both resolved relative to the project directory first and the working
directory second.

| File | Purpose | Tracked by git? |
|---|---|---|
| `sqlferret.config.json` | Display units, redaction policy, plans folder, connection string | Yes, if you want it to be |
| `.env` | Secrets referenced from the config as `${VAR}` | **No.** Gitignored. |

Missing files are not an error. With neither present, built-in defaults apply.

## `sqlferret.config.json`

```json
{
  "display": {
    "durationUnit": "ms",
    "cpuUnit": "ms"
  },
  "ingest": {
    "redactionPolicy": "masked",
    "sqlTextSanitization": "literals"
  },
  "server": {
    "connectionString": "Server=sql01;Database=Sales;${SQLFERRET_AUTH};TrustServerCertificate=True",
    "plansFolder": "./plans"
  }
}
```

| Key | Default | Values |
|---|---|---|
| `display.durationUnit` | `ms` | `ms`, `s`, `us`. Formatting only; storage is always microseconds. |
| `display.cpuUnit` | `ms` | Same. |
| `ingest.redactionPolicy` | `masked` | `off`, `hash`, `masked`, `full`. See [privacy.md](privacy.md). Overridable per import with `--redaction`. |
| `ingest.sqlTextSanitization` | `raw` | `raw`, `literals`. See [privacy.md](privacy.md). Overridable per import with `--sanitize-sql-text`; honored by both the CLI and the TUI import flow. |
| `server.connectionString` | none | Used by `query-store-import` and estimated-plan capture. `${VAR}` is interpolated from the environment. |
| `server.plansFolder` | `./plans` | Relative paths resolve against the **project directory**. An absolute path is used as-is. |

Unknown keys are ignored, and a partial file is fine: anything absent falls back to its default.

## `.env`

```ini
# Copy to .env and fill in. .env is gitignored.
SQLFERRET_AUTH=User ID=sqlferret;Password=CHANGE_ME;TrustServerCertificate=True
```

`.env.example` is committed as a template. Copy it, fill it in, and never commit the result.

Rules:

- **Real environment variables win.** `DotEnv` only sets keys that are not already present, so a
  value from your shell, your CI secret store or your container runtime overrides the file.
- **A missing `.env` is a silent no-op.** Nothing breaks in an environment where variables come
  from elsewhere.
- Interpolation is `${VAR}` inside `server.connectionString`. An undefined variable expands to the
  empty string, which usually produces a connection error rather than a silent misconnect.

## Resolution order

Opening a project runs this, in order:

1. Load `.env` from the **project directory**.
2. Load `.env` from the **working directory**.
3. Read `sqlferret.config.json` from the project directory; if absent, from the working directory;
   if absent, use defaults.
4. Interpolate `${VAR}` in the connection string against the now-populated environment.

Because `DotEnv` only fills absent keys, the effective precedence for secrets is:

```text
real OS environment  >  project .env  >  working-directory .env
```

And for settings:

```text
project sqlferret.config.json  >  working-directory sqlferret.config.json  >  defaults
```

The practical consequence: a per-project config in the audit directory travels with the project,
while a config in your repo checkout acts as your personal default for any project that does not
carry its own.

## Project files SQLFerret maintains

### `project.json`

Written at creation and updated on every open. Not meant to be hand-edited.

```json
{
  "SchemaVersion": 1,
  "ToolVersion": "1.0.0.0",
  "CreatedUtc": "2026-08-04T09:12:44.1120000+00:00",
  "LastOpenedUtc": "2026-08-28T07:31:02.4410000+00:00",
  "Notes": null
}
```

If the file is present but unreadable, SQLFerret reinitializes it and prints a warning to stderr,
because provenance has been lost:

```text
warning: project.json was unreadable and has been reinitialized; provenance (CreatedUtc) was reset.
```

Writing it is best-effort. A read-only or full filesystem does not fail an otherwise valid open,
so running a read-only `top-slow` against an archived project still works.

### The project `README.md`

Written once, at creation, if absent. It explains the directory layout, the `project.json` fields,
the microseconds convention and the capture-session requirements, so that the folder is
self-describing to whoever opens it in a year's time.

It is never overwritten. Edit and extend it freely; that is what it is for.

## TUI state

The terminal UI persists column layouts, sort order and filter rules in `uistate.json`, next to
the TUI binary (`AppContext.BaseDirectory`), not inside the project.

That is a per-user preference file, not project data. A corrupt or unreadable file resets to
defaults silently.

## Environment variables

| Variable | Used by |
|---|---|
| `SQLFERRET_TEST_CONN` | The environment-gated estimated-plan and Query Store integration tests. Absent means those tests skip. |
| Anything referenced as `${VAR}` in the config | Connection-string interpolation |
