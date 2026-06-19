# SQLFerret TUI (MVP vertical slice) — design

> Plan 2 of the project. Builds a Terminal.Gui v2 host over the **already-merged**
> `SqlFerret.Core` engine (Plan 1). Refines §7 of the original design
> (`2026-06-19-sqlferret-core-tui-design.md`) into a buildable, testable slice.

## 1. Summary & scope

Deliver a keyboard-first terminal UI that proves the entire path **ingest → analyze →
drill → copy** on the minimum surface. The remaining views from §7 (Top Frequent,
Queries, Session Flow, Dimensions, Quality, Plans) are deferred to a later cycle —
each is a thin repeat of the patterns this slice establishes.

**In scope (MVP):**
- App **shell** — master/detail, left view-rail, keyboard navigation, status bar, filter chips.
- **Import view** — pick `.xel` file or `logs/` folder + redaction policy; run ingestion on a
  background task with **live progress**.
- **Top Slow view** — grouped-by-signature grid (`WorkloadQueries.TopSlow`), sortable, text filter.
- **Signature drill-down** — raw+normalized SQL, metric summary, and three sub-panels:
  **Occurrences**, **Parameter-impact comparison**, **Build-for-SSMS** (clipboard).
- **Column chooser** (`C`) for the two grids, persisted to `sqlferret.ui.json`.

**Out of scope (deferred → next plan):** Top Frequent, Queries browser, Session Flow,
Dimensions, Quality, Plans/estimated-plan capture, Avalonia, LLM features.

## 2. Design principles (KISS — inherits Plan 1)

- **The TUI holds no analysis logic.** Every grid is a parametrized `WorkloadQueries`
  call; durations render via `DisplayFormat` + the config `durationUnit`. No aggregation
  in C#.
- **Thin presenters, no `INotifyPropertyChanged`.** One plain presenter class per view holds
  view state and calls Core; the Terminal.Gui `View` is code-behind that renders and wires
  keys. Presenters are the single unit tested headlessly. (INPC/MVVM is deferred to an
  eventual Avalonia port, where it earns its keep.)
- **No new abstractions or DI container.** Presenters take concrete Core types
  (`WorkloadQueries`, `IngestionService`) by constructor. The only interface introduced is
  `IClipboard` (a real native impl + a file-fallback impl — two genuine implementations).
- Modern C# 14 / `net10.0`; raw-string SQL stays in Core; secrets via `.env`/`${ENV}`.

## 3. Architecture & projects

```
src/SqlFerret.Tui/            Terminal.Gui v2 (2.4.6) host → references SqlFerret.Core
  Program.cs                  Application.Init → load config → open project → Run → Shutdown
  Shell/
    MainWindow.cs             view-rail + content host + title + status bar + filter chips
    Keys.cs                   keybinding constants
  Presenters/
    ImportPresenter.cs
    TopSlowPresenter.cs
    DrillDownPresenter.cs
  Views/
    ImportView.cs
    TopSlowView.cs
    DrillDownView.cs
    ColumnChooserDialog.cs
  Clipboard/
    IClipboard.cs             string Copy(text) → returns how it was delivered
    NativeClipboard.cs        Terminal.Gui clipboard / xclip / wl-clipboard
    FileFallbackClipboard.cs  writes <tmp>/sqlferret-<id>.sql, returns the path
tests/SqlFerret.Tui.Tests/    xUnit — presenter tests (headless) + shell smoke test
```

**Two small additions to Plan-1 Core** (both minimal, KISS, keep data access in Core):
1. **Ingestion progress** (`Ingestion`): `record IngestionProgress(long Read, long Mapped,
   long Unmapped, long Cleaned, long TokenizeFailures, string CurrentFile)` and an optional
   `IProgress<IngestionProgress>? progress = null` parameter on `IngestionService.Ingest`.
   The service reports after each batch (and on file change). Backward-compatible; the CLI may
   adopt it for a progress line.
2. **Occurrence reconstruction** (`Analysis`): `ExecutionEvent WorkloadQueries.LoadExecution(
   long executionId)` — reads one `executions` row plus its `execution_parameters` and
   rebuilds an `ExecutionEvent` (with `Parameters`) so `ReplayBuilder.Build` can produce the
   build-for-SSMS script. Values come back as stored (already redacted at ingest).

Both are tested in `SqlFerret.Core.Tests` (TDD) alongside the existing suite.

## 4. The presenter seam (signatures)

All presenters are constructed from concrete Core objects and have no Terminal.Gui dependency.

```csharp
// Top Slow
sealed class TopSlowPresenter(WorkloadQueries q)
{
    public string SortColumn { get; private set; } = "total_duration_us";
    public int Limit { get; set; } = 50;
    public string? TextFilter { get; private set; }          // substring match on normalized SQL
    public IReadOnlyList<FilterRule> Filters { get; set; } = [];
    public IReadOnlyList<QueryStat> Load();                   // calls q.TopSlow(Limit, SortColumn, Filters), applies TextFilter
    public void CycleSort();                                  // total → count → avg → p95 → max → total
    public void SetTextFilter(string? s);
}

// Signature drill-down (constructed with the selected QueryStat)
sealed class DrillDownPresenter(WorkloadQueries q, QueryStat signature)
{
    public IReadOnlyList<Occurrence> Occurrences(int limit = 200);
    public IReadOnlyList<ParamImpact> ParameterImpact(string paramName);
    public ReplayScript BuildReplay(long executionId);       // q.LoadExecution → ReplayBuilder.Build
}

// Import — async ingestion with progress
sealed class ImportPresenter(DuckDbProject project)
{
    public async Task<IngestionResult> RunAsync(
        string path, RedactionMode redaction,
        IProgress<IngestionProgress> progress, CancellationToken ct);
    //   files = XelSource.Resolve(path); events = new XelReader().Read(files);
    //   new IngestionService(project, new IngestionOptions(redaction, [])).Ingest(path, events, progress)
}
```

## 5. Shell & navigation

- **Layout:** left `ListView` rail (`Import`, `Top Slow`); central content `FrameView` that
  swaps the active view; top title showing `project name · N execs / M signatures`; bottom
  `StatusBar` with context keybindings; a one-line **filter-chips** display above the grid.
- **Keys (MVP subset of §7):** `↑↓`/`Tab` move · `Enter` drill into a signature · `Esc` back ·
  `/` text-filter (Top Slow) · `s` cycle sort · `C` choose columns · `c` copy build-for-SSMS ·
  `q` quit. Mouse supported (TG v2 default).
- **Startup:** `Program.cs` runs `DotEnv.Load` → `SqlFerretConfig.Load` → `UiState.Load` →
  `DuckDbProject.Open(projectPath)` (the `.duckdb` path is a required CLI argument, like the
  CLI host's `--project`), builds `MainWindow`,
  `Application.Run()`, then `Application.Shutdown()` and `UiState.Save` on exit.

## 6. Views

- **Import** — a form: path field (file/folder, with a TG `FileDialog` picker), redaction
  `RadioGroup` (off/hash/masked/full, default from config), `Start` button. On start: disable
  inputs, run `ImportPresenter.RunAsync` on a background task, render live `IngestionProgress`
  counters (read/mapped/unmapped/cleaned/failures + current file). On completion: show the final
  `IngestionResult`, refresh the title counts, and select **Top Slow**.
- **Top Slow** — `TableView` bound to `TopSlowPresenter.Load()`; columns `kind · signature ·
  count · avg · p95 · max · total` (durations via `DisplayFormat`). `s` cycles sort, `/` text-
  filters, `C` chooses columns, `Enter` drills.
- **Drill-down** — header with raw + normalized SQL and the metric summary from the `QueryStat`;
  a sub-panel selector (`Tab`) across **Occurrences** (`TableView`: time · db · login · duration ·
  sql), **Parameter impact** (prompt for `@param`, then `TableView`: value · count · avg · p95 ·
  max, sorted slowest-first), and **Build-for-SSMS** (`c` on a selected occurrence → reconstruct
  via `BuildReplay`, copy to clipboard, show the `ReplayKind` + confidence + redaction warning).

## 7. Build-for-SSMS clipboard

`DrillDownPresenter.BuildReplay(executionId)` → `ReplayScript`. `IClipboard.Copy(script.Sql)`:
- `NativeClipboard` tries Terminal.Gui's clipboard; on Linux falls through to `xclip` /
  `wl-clipboard` if present.
- `FileFallbackClipboard` (used when no clipboard mechanism exists) writes the script to
  `<temp>/sqlferret-<executionId>.sql` and returns that path.

`Copy` returns a small result describing the delivery (`"copied to clipboard"` vs the file path),
which the view surfaces. The confidence flag and a redaction warning (when stored values were
masked/hashed at ingest) are shown next to the script.

## 8. Column chooser & UiState

`C` opens `ColumnChooserDialog` over the active view's **column catalog** (the grid's known
columns): toggle visibility, reorder (move up/down), reset to defaults. On accept, the view
re-renders and the layout is written to `sqlferret.ui.json` via the existing `UiState`
(`ViewLayout(Columns, Sort)` keyed by view id — `"topSlow"`, `"occurrences"`). On startup each
grid reads its `ViewLayout` (falling back to defaults when absent/malformed).

## 9. Threading model

The UI runs on Terminal.Gui's single main loop. The **only** background work is ingestion:
`Task.Run(() => ImportPresenter.RunAsync(...))` with
`progress = new Progress<IngestionProgress>(p => Application.Invoke(() => render(p)))`.
`Application.Invoke` marshals the render onto the UI loop — presenters never touch UI types, and
views never block the loop. Analysis queries are top-N bounded and run synchronously; if a query
ever proves slow it uses the same `Task.Run`/`Invoke` pattern.

## 10. Error handling

- Malformed `config`/`ui` files → defaults + a warning line (Core already guarantees no-crash).
- Bad path / unreadable `.xel` / missing project → a message dialog; the app stays alive.
- Clipboard unavailable → `FileFallbackClipboard` + a notice with the file path.
- A failed ingestion surfaces the exception text in the Import view and leaves the project usable;
  partial rows already committed remain (run lifecycle is the boundary).

## 11. Testing

- **Presenter tests (headless, the core of the suite):** seed a temp DuckDB (reuse Core's insert
  path / the existing fake `IXeEventData` reader), then assert:
  - `TopSlowPresenter` — sort cycling, text filter, row contents/order.
  - `DrillDownPresenter` — occurrences, parameter-impact ordering, and `BuildReplay` output for
    a batch (raw) and an RPC (EXEC with params) row.
  - `ImportPresenter` — progress callbacks fire with monotonic counters and the final
    `IngestionResult` matches.
- **Core additions** tested in `SqlFerret.Core.Tests`: `IProgress` is invoked per batch;
  `LoadExecution` round-trips an `ExecutionEvent` (incl. parameters) from stored rows.
- **Shell smoke test:** construct `MainWindow` against a temp project via TG v2's test driver
  and assert it wires up without throwing (no full event-loop drive).
- A documented **manual end-to-end** pass against the gitignored `sample/` workload trace.

## 12. Out of scope (later cycle, its own spec)

Top Frequent · Queries browser · Session Flow · Dimensions · Quality · Plans view +
estimated-plan capture from the TUI · Avalonia host · LLM-assisted analysis.
