using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using Xunit;

public class UiStateTests
{
    [Fact]
    public void Roundtrips_filters_and_layouts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui_{Guid.NewGuid():N}.json");
        try
        {
            var state = UiState.Load(path); // missing → empty
            Assert.Empty(state.Filters);
            state.Filters.Add(new FilterRule("noise", "object_name", "eq", null, "sp_reset_connection", "ingest", "exclude", true));
            state.Views["topSlow"] = new UiState.ViewLayout(["kind", "signature", "total"], "total_desc");
            state.Save(path);

            var reloaded = UiState.Load(path);
            Assert.Single(reloaded.Filters);
            Assert.Equal("sp_reset_connection", reloaded.Filters[0].Value);
            Assert.Equal("total_desc", reloaded.Views["topSlow"].Sort);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Malformed_file_yields_empty_state()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{ not json");
        var state = UiState.Load(path);
        Assert.Empty(state.Filters);
        Assert.Empty(state.Views);
    }
}
