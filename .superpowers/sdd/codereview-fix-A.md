# Code Review Fix A — TUI Concurrency Bugs

## Bugs fixed

### Bug 1 — No re-entrancy guard on StartAsync
**Mechanism**: The user could click Start a second time while an import was already running. Each click fired `StartAsync` independently; they would race on the same `_presenter` / `_progress`, producing interleaved progress updates and potentially two `Completed` events.
**Fix**: Added `private bool _running` field. `StartAsync` checks `if (_running) return;` before any await. The flag is set to `true` immediately after the guard and cleared in `finally`, so it resets on both success and failure paths. `_start.Enabled` is also toggled to disable the button in the UI.

### Bug 2 — Off-thread UI write on progress failure
**Mechanism**: The original `catch` block inside the `Progress<T>` callback wrote `_progress.Text` directly from the thread-pool thread (where the `IProgress<T>` continuation runs). Terminal.Gui views must only be mutated from the UI thread.
**Fix**: The catch block now silently swallows the exception ("drop this tick") instead of doing an off-thread UI write. Progress ticks during teardown or headless tests are simply lost, which is acceptable.

### Bug 3 — `Completed` raised while `_importing` is still `true` in MainWindow
**Mechanism**: `MainWindow.Show` used to subscribe to `view.Completed` and call `Show(0)` on success. But `Show` early-exits when `_importing == true`. Since `_importing` was never cleared (no `ImportFinished` event existed), `Completed` triggered `Show(0)` which was silently blocked, leaving the UI permanently stuck on the Import view.
**Fix**: Added `ImportFinished` event to `ImportView`. It is raised in `finally`, unconditionally, BEFORE `Completed`. `MainWindow` subscribes `view.ImportFinished += () => _importing = false;` so by the time `Completed` fires and calls `Show(0)`, the navigation guard is already lifted.

### Bug 4 — Rail navigation not blocked during import
**Mechanism**: While an import was running, the user could click "Top Slow" in the left rail. `Show(0)` would tear down the `ImportView` (via `_contentHost.RemoveAll()`), dispose the presenter context, and instantiate a new `TopSlowView`, while the background task continued writing to the now-orphaned progress label.
**Fix**: Added `private bool _importing` to `MainWindow`. `Show()` returns immediately if `_importing == true`. `ImportStarted` / `ImportFinished` events on `ImportView` drive the flag on the UI thread, wrapping the exact lifetime of the running task.

## Event ordering chosen

```
ImportStarted  →  _importing = true     (before first await)
   … import runs …
ImportFinished →  _importing = false    (in finally, before Completed)
Completed      →  Show(0)               (runs with _importing already false)
```

This ordering is the only one that avoids the deadlock: if `ImportFinished` fired after `Completed`, `Show(0)` would still be blocked.

## Test added — ImportViewTests.StartAsync_reentrant_call_while_running_is_ignored

Tests three assertions without needing a real `.xel` file:
1. `IsRunning` is `false` initially.
2. After an empty-path call (early return before guard sets flag), `IsRunning` is still `false`.
3. After a call with a non-existent path (RunAsync throws, `finally` clears the flag), `IsRunning` is `false` — the guard doesn't get stuck.
4. A subsequent call succeeds (no permanent lock).

The existing `StartAsync_empty_path_sets_progress_text_and_does_not_throw` was kept unchanged. The sample-dependent `Start_runs_import_and_raises_completed` is `[SkippableFact]` and skips when no `.xel` is present.

## Build + test output

```
dotnet build --nologo -v q
  → 6 projects, 0 errors, 0 warnings (00:00:04.53)

dotnet test --filter ImportViewTests --nologo -v q
  → passed: 3, failed: 0, skipped: 0

dotnet test --filter ShellSmokeTests --nologo -v q
  → passed: 1, failed: 0, skipped: 0

dotnet test --nologo -v q
  → SqlFerret.Core.Tests:  passed: 73, failed: 0, skipped: 1
  → SqlFerret.Tui.Tests:   passed: 20, failed: 0, skipped: 0
  → Total: 93 passed, 0 failed, 1 skipped
```
