using SqlFerret.Core.Config;
using Xunit;

public class ConfigTests
{
    [Fact]
    public void DotEnv_sets_absent_keys_only()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "# comment\nexport SF_TEST_A=hello\nSF_TEST_B=\"world\"\n");
        Environment.SetEnvironmentVariable("SF_TEST_A", null);
        Environment.SetEnvironmentVariable("SF_TEST_B", "preset");
        DotEnv.Load(f);
        Assert.Equal("hello", Environment.GetEnvironmentVariable("SF_TEST_A"));
        Assert.Equal("preset", Environment.GetEnvironmentVariable("SF_TEST_B")); // not overwritten
    }

    [Fact]
    public void DotEnv_missing_file_is_silent_noop()
    {
        // Should not throw when file does not exist
        DotEnv.Load("/nonexistent/path/that/does/not/exist/.env");
    }

    [Fact]
    public void DotEnv_strips_single_quotes()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "SF_TEST_SINGLE='quoted_value'\n");
        Environment.SetEnvironmentVariable("SF_TEST_SINGLE", null);
        DotEnv.Load(f);
        Assert.Equal("quoted_value", Environment.GetEnvironmentVariable("SF_TEST_SINGLE"));
    }

    [Fact]
    public void Config_defaults_when_no_file()
    {
        var c = SqlFerretConfig.Load(null);
        Assert.Equal("ms", c.DurationUnit);
        Assert.Equal("masked", c.RedactionPolicy);
        Assert.Equal("./plans", c.PlansFolder);
    }

    [Fact]
    public void Config_interpolates_env_in_connection_string()
    {
        Environment.SetEnvironmentVariable("SF_AUTH", "User ID=sa;Password=x");
        var f = Path.GetTempFileName();
        File.WriteAllText(f, """{ "server": { "connectionString": "Server=h;${SF_AUTH}" } }""");
        var c = SqlFerretConfig.Load(f);
        Assert.Equal("Server=h;User ID=sa;Password=x", c.ConnectionString);
    }

    [Fact]
    public void Config_missing_env_var_leaves_blank_not_crash()
    {
        Environment.SetEnvironmentVariable("SF_NONEXISTENT_VAR", null);
        var f = Path.GetTempFileName();
        File.WriteAllText(f, """{ "server": { "connectionString": "Server=h;${SF_NONEXISTENT_VAR}" } }""");
        var c = SqlFerretConfig.Load(f);
        // Missing env var → degrade gracefully (leave blank), not crash
        Assert.Equal("Server=h;", c.ConnectionString);
    }

    [Fact]
    public void Config_missing_json_file_uses_defaults()
    {
        var c = SqlFerretConfig.Load("/nonexistent/path.json");
        Assert.Equal("ms", c.DurationUnit);
        Assert.Equal("ms", c.CpuUnit);
        Assert.Equal("masked", c.RedactionPolicy);
        Assert.Null(c.ConnectionString);
        Assert.Equal("./plans", c.PlansFolder);
    }

    [Theory]
    [InlineData(1500000, "ms", "1500 ms")]
    [InlineData(1500000, "s", "1.5 s")]
    [InlineData(1500000, "us", "1500000 us")]
    public void DisplayFormat_converts(long us, string unit, string expected)
        => Assert.Equal(expected, DisplayFormat.Duration(us, unit));
}
