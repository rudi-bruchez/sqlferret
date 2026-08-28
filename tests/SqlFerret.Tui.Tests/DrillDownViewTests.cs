// tests/SqlFerret.Tui.Tests/DrillDownViewTests.cs
// Headless render test: DrillDownView builds its DataTable from the presenter.
// Pattern mirrors TopSlowViewTests — Application.Create(VirtualTimeProvider), no app.Init().
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Tui.Clipboard;
using SqlFerret.Tui.Presenters;
using SqlFerret.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Time;

public class DrillDownViewTests
{
    [Fact]
    public void View_lists_occurrences_and_copies_replay()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 1", "dbo.GetOrder", 4000L),
        ]);

        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var view = new DrillDownView(
                new DrillDownPresenter(q, sig),
                new FileFallbackClipboard(dir),
                "ms");

            view.Reload();
            Assert.Equal(1, view.OccurrenceCount);

            view.SelectRow(0);
            var res = view.CopySelectedReplay();
            Assert.True(File.Exists(res.FilePath));
            Assert.Contains("EXEC dbo.GetOrder", File.ReadAllText(res.FilePath!));

            view.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CopySelectedReplay_description_contains_redaction_warning_when_params_are_hashed()
    {
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 123", "dbo.GetOrder", 4000L),
        ], RedactionMode.Hash);

        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var view = new DrillDownView(
                new DrillDownPresenter(q, sig),
                new FileFallbackClipboard(dir),
                "ms");

            view.Reload();
            view.SelectRow(0);
            var res = view.CopySelectedReplay();

            Assert.Contains("WARNING: parameter values are redacted", res.Description);

            view.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CopySelectedReplay_description_contains_sanitization_warning_for_a_raw_batch()
    {
        // RawBatch is built directly from the (possibly sanitized) stored statement text —
        // ReplayBuilder.Build's last-resort branch — so it genuinely won't execute; it must
        // get the warning.
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var db = TestProject.SeedFrom(
        [
            ("sql_batch_completed", "SELECT Name FROM dbo.Customers WHERE Email = 'x'", null, 4000L),
        ], sqlText: SqlTextSanitization.Literals);

        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var view = new DrillDownView(
                new DrillDownPresenter(q, sig),
                new FileFallbackClipboard(dir),
                "ms");

            view.Reload();
            view.SelectRow(0);
            var res = view.CopySelectedReplay();

            Assert.Contains("WARNING: statement text is sanitized and will not execute", res.Description);

            view.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CopySelectedReplay_omits_sanitization_warning_for_an_exec_proc_on_a_sanitized_project()
    {
        // ExecProc (an RPC with an ObjectName and parameters) is built from ev.Parameters, never
        // from the stored statement text (ReplayBuilder.Build) — it executes fine on a sanitized
        // project, so the "will not execute" warning must not be shown for it.
        using IApplication app = Application.Create(new VirtualTimeProvider());

        using var db = TestProject.SeedFrom(
        [
            ("rpc_completed", "exec dbo.GetOrder @OrderId = 123", "dbo.GetOrder", 4000L),
        ], sqlText: SqlTextSanitization.Literals);

        var q = new WorkloadQueries(db.Connection);
        var sig = q.TopSlow(10, "total_duration_us", [])[0];
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var view = new DrillDownView(
                new DrillDownPresenter(q, sig),
                new FileFallbackClipboard(dir),
                "ms");

            view.Reload();
            view.SelectRow(0);
            var res = view.CopySelectedReplay();

            Assert.DoesNotContain("WARNING: statement text is sanitized and will not execute", res.Description);

            view.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }
}
