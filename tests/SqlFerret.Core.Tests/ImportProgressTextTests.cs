using SqlFerret.Core.Ingestion;

public class ImportProgressTextTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(812_345, "812k")]
    [InlineData(999_999, "999k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_250_000, "1.2M")]
    public void Abbrev_formats_counts(long n, string expected) =>
        Assert.Equal(expected, ImportProgressText.Abbrev(n));

    [Fact]
    public void Render_produces_canonical_ascii_line()
    {
        var p = new ImportProgress(
            FileIndex: 2, FileCount: 5, CurrentFile: "perf_3.xel",
            FileFraction: 0.4748, OverallFraction: 0.612,
            Read: 812_345, Mapped: 790_000, Unmapped: 21_000, Cleaned: 0, TokenizeFailures: 0);

        Assert.Equal(
            "[2/5] perf_3.xel  file 47%  overall 61%  " +
            "read=812k mapped=790k unmapped=21k cleaned=0 failures=0",
            ImportProgressText.Render(p));
    }

    [Fact]
    public void Render_without_files_omits_index_header()
    {
        var p = new ImportProgress(0, 0, "", 0, 0, 0, 0, 0, 0, 0);
        Assert.StartsWith("  file 0%  overall 0%", ImportProgressText.Render(p));
    }
}
