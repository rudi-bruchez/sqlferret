# Terminal UI

An interactive front end over the same `SqlFerret.Core` engine as the CLI. Built with
Terminal.Gui 2.4.6, so it runs in any reasonably modern terminal on Windows, Linux and macOS.

```bash
dotnet run --project src/SqlFerret.Tui -- ./audits/prod-2026-08
```

The single argument is the project directory. Like the CLI, it is created if it does not exist,
so you can start the UI on an empty folder and import from inside it.

## Layout

```text
┌ Views ─────────┬ Top Slow ──────────────────────────────────────────────┐
│ Top Slow       │ kind    signature              count   avg   p95   max │
│ Import         │ SELECT  select o.id , o.tot…    1204  674   3204  9912 │
│                │ EXEC    exec dbo.RebuildCar…     318 1264   5120 14300 │
│                │ UPDATE  update dbo.Sessions…    9822   30     84   612 │
└────────────────┴────────────────────────────────────────────────────────┘
 Q Quit
```

A view rail on the left, content on the right, a status bar at the bottom. The window title shows
the open database.

Navigation between views is blocked while an import is running, so you cannot leave a running
ingestion half-attended.

## Views

### Top Slow

A table of query shapes, loaded through the same `WorkloadQueries.TopSlow` the CLI uses. Default
limit is 50 rows.

| Key | Action |
|---|---|
| `↑` `↓` `PgUp` `PgDn` | Move |
| `Enter` | Drill into the selected shape |
| `s` | Cycle the sort column |
| `/` | Filter by text |
| `Shift+C` | Choose visible columns |
| `q` | Quit |

**Sort cycle** (`s`): `total_duration_us` → `p95_duration_us` → `max_duration_us` →
`avg_duration_us` → back to total. This is the same allow-list `WorkloadQueries` enforces, and it
is the one thing the TUI gives you that the CLI does not: `top-slow` is fixed to total duration.

**Text filter** (`/`) matches against the normalized SQL, case-insensitively, as a bound `ILIKE`
parameter. Type a table name to see everything touching it.

**Columns** (`Shift+C`) opens a chooser over the catalogue: `kind`, `signature`, `count`, `avg`,
`p95`, `max`, `total`. There is a Reset button in the dialog.

Column choice and sort order are saved as soon as you change them, and restored next launch. See
[state](#state) below.

### Drill-down

Reached with `Enter` from Top Slow. Header shows the shape and its aggregate statistics; the table
below lists individual executions with timestamp, database, login and duration.

| Key | Action |
|---|---|
| `c` | Copy a runnable replay script for the selected execution |
| `Esc` | Back to Top Slow |

**Copy** builds the script through `ReplayBuilder` and reports what it produced in the status
line, including two things worth reading before you paste it into a query window:

- **A confidence figure** below 1.0. A raw batch replays exactly (1.0); a reconstructed
  `EXEC proc @a = …` inherits the lowest parse confidence of its parameters; an `sp_executesql`
  call is 0.7.
- **A redaction warning.** If the execution's parameters were masked or hashed at import, the
  replay carries those placeholder values, not the originals. The status line says so explicitly.
  Replaying it will compile a plan for `'****'`, which tells you nothing about production.

Copying uses the native terminal clipboard when Terminal.Gui can reach one, and falls back to
writing a temp file otherwise. Either way the status line tells you where the text went.

### Import

A form: path, redaction mode, Start. The redaction default comes from the project config.

Progress is reported live and asynchronously, through the same `ImportRunner` and
`ImportProgressTracker` the CLI drives, so a TUI import and a CLI import produce identical results
and identical counters. On success the view switches back to Top Slow with the new data loaded.

The view rail is locked for the duration.

## State

The TUI persists column layout, sort column and filter rules to `uistate.json` in the binary's
directory (`AppContext.BaseDirectory`), **not** in the project.

That is deliberate: it is a per-user preference, not project data, so it does not travel when you
zip an audit and send it to someone. A missing or corrupt file resets to defaults without
complaining.

## Choosing between the TUI, the CLI and raw SQL

- **TUI** for exploring. Sorting by p95, filtering by table name, and drilling into individual
  executions are all faster here than anywhere else.
- **CLI** for anything repeatable: scripted imports, scheduled exports, CI, piping to a file.
- **DuckDB directly** for anything the other two do not do. It is a plain database and no feature
  of it is off limits. See [data-model.md](data-model.md#useful-queries).

## Current limits

The TUI covers the two views wired into the rail: Top Slow and Import. Blocking digests, event
export, Query Store import and plan obfuscation are CLI-only for now. All of them run over the
same project directory, so mixing hosts is normal and safe: import in the TUI, export from the
CLI, query in DuckDB.
