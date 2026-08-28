using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlFerret.Core.Config;

public record SqlFerretConfig(string DurationUnit, string CpuUnit, string RedactionPolicy,
    string? ConnectionString, string PlansFolder)
{
    public static SqlFerretConfig Load(string? jsonPath)
    {
        string durationUnit = "ms", cpuUnit = "ms", redaction = "masked", plans = "./plans";
        string? conn = null;

        if (jsonPath is not null && File.Exists(jsonPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("display", out var d))
            {
                if (d.TryGetProperty("durationUnit", out var v)) durationUnit = v.GetString() ?? durationUnit;
                if (d.TryGetProperty("cpuUnit", out var v2)) cpuUnit = v2.GetString() ?? cpuUnit;
            }
            if (root.TryGetProperty("ingest", out var i) && i.TryGetProperty("redactionPolicy", out var r))
                redaction = r.GetString() ?? redaction;
            if (root.TryGetProperty("server", out var s))
            {
                if (s.TryGetProperty("connectionString", out var cs)) conn = cs.GetString();
                if (s.TryGetProperty("plansFolder", out var pf)) plans = pf.GetString() ?? plans;
            }
        }

        if (conn is not null)
            conn = Regex.Replace(conn, @"\$\{(\w+)\}",
                m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

        return new SqlFerretConfig(durationUnit, cpuUnit, redaction, conn, plans);
    }
}
