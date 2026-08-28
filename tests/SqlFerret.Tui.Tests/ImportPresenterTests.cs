using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Project;
using SqlFerret.Tui.Presenters;

public class ImportPresenterTests
{
    [SkippableFact]
    public async Task RunAsync_imports_real_sample_and_reports_progress()
    {
        var file = SampleFile.FindSmallest();
        Skip.If(file is null, "sample/ folder with a .xel trace not present");

        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            var project = AuditProject.OpenOrCreate(dir);
            using var db = project.OpenDb();
            var presenter = new ImportPresenter(db, project);
            var ticks = new List<ImportProgress>();
            IProgress<ImportProgress> sync = new ListProgress(ticks.Add);

            var result = await presenter.RunAsync(file!, RedactionMode.Masked, sync, CancellationToken.None);

            Assert.True(result.Read > 0);
            Assert.NotEmpty(ticks);
            Assert.Equal(1.0, ticks[^1].OverallFraction, 3);   // reaches 100% at the end
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("literal")]   // misspelled name
    [InlineData("7")]         // Enum.TryParse accepts numeric strings for undefined values too
    public async Task RunAsync_throws_on_an_invalid_configured_sql_text_policy(string configuredValue)
    {
        // No sample/ needed: the bad config value is read and validated before the presenter
        // ever touches the (nonexistent) import path, so this does not require SkippableFact.
        var dir = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "sqlferret.config.json"),
                $$"""{ "ingest": { "sqlTextSanitization": "{{configuredValue}}" } }""");

            var project = AuditProject.OpenOrCreate(dir);
            using var db = project.OpenDb();
            var presenter = new ImportPresenter(db, project);
            IProgress<ImportProgress> sync = new ListProgress(_ => { });

            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                presenter.RunAsync("does-not-matter.xel", RedactionMode.Masked, sync, CancellationToken.None));

            Assert.Contains(configuredValue, ex.Message);
            Assert.Contains("ingest.sqlTextSanitization", ex.Message);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    private sealed class ListProgress(Action<ImportProgress> a) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => a(value); }
}
