using SqlFerret.Core.Project;
using Xunit;

public class ProjectManifestTests
{
    [Fact]
    public void Write_then_TryRead_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm_{Guid.NewGuid():N}.json");
        try
        {
            var created = new DateTime(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc);
            var m = new ProjectManifest(1, "1.2.3", created, created, null);
            m.Write(path);

            var back = ProjectManifest.TryRead(path);
            Assert.NotNull(back);
            Assert.Equal(1, back!.SchemaVersion);
            Assert.Equal("1.2.3", back.ToolVersion);
            Assert.Equal(created, back.CreatedUtc);
            Assert.Equal(created, back.LastOpenedUtc);
            Assert.Null(back.Notes);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TryRead_missing_returns_null()
        => Assert.Null(ProjectManifest.TryRead("/nonexistent/dir/project.json"));

    [Fact]
    public void TryRead_malformed_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not valid json ");
        try { Assert.Null(ProjectManifest.TryRead(path)); }
        finally { File.Delete(path); }
    }
}
