// tests/SqlFerret.Core.Tests/SqlFerretConfigSqlTextTests.cs
using SqlFerret.Core.Config;
using Xunit;

public class SqlFerretConfigSqlTextTests
{
    [Fact]
    public void Defaults_to_raw_when_the_key_is_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "ingest": { "redactionPolicy": "hash" } }""");
            var cfg = SqlFerretConfig.Load(path);
            Assert.Equal("raw", cfg.SqlTextPolicy);
            Assert.Equal("hash", cfg.RedactionPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reads_the_configured_level()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "ingest": { "sqlTextSanitization": "literals" } }""");
            Assert.Equal("literals", SqlFerretConfig.Load(path).SqlTextPolicy);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_to_raw_when_there_is_no_config_file()
    {
        Assert.Equal("raw", SqlFerretConfig.Load(null).SqlTextPolicy);
    }
}
