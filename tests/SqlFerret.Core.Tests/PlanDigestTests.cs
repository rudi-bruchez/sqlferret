using System.Text.Json;
using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

public class PlanDigestTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"sf_plans_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static PlanProfile P(string hash, long durationUs, DateTime at, params PlanFinding[] findings) => new()
    {
        PlanHash = hash,
        PlanHashSource = "queryplanhash",
        FileStem = "p_" + hash,
        StatementCount = 1,
        QueryHash = "56006EFD9093F347",
        StatementType = "SELECT",
        StatementText = "SELECT 1",
        StatementTextLength = 8,
        CapturedAt = at,
        DurationUs = durationUs,
        CpuTimeUs = durationUs / 2,
        Dop = 8,
        SerialDesiredMemoryKb = 42_225_192,
        GrantedMemoryKb = 20_193_000,
        MaxUsedMemoryKb = 16_533_000,
        Findings = findings,
    };

    [Fact]
    public void Digest_carries_the_exact_timestamps_of_first_and_worst()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            var first = new DateTime(2026, 8, 4, 11, 2, 7, DateTimeKind.Utc).AddTicks(4412380);
            var worst = new DateTime(2026, 8, 4, 13, 5, 56, DateTimeKind.Utc).AddTicks(9107440);

            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("t.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [
                new PreparedPlanProfile(P("ABC", 225_183_000, first), PlanWriteOutcome.WroteFirst),
                new PreparedPlanProfile(P("ABC", 1_478_000_000, worst,
                    new PlanFinding("row_goal_defeated", 9, """{"rows":100}""")), PlanWriteOutcome.WroteWorst)]);
            db.FinalizePlanRun(runId);

            var rows = db.ReadPlanDigestRows(runId);
            new PlanArtifactWriter(dir).WriteDigests(rows);

            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "p_ABC.digest.json"))).RootElement;

            Assert.Equal(1, json.GetProperty("schema_version").GetInt32());
            Assert.Equal("ABC", json.GetProperty("plan_hash").GetString());
            Assert.Equal(2, json.GetProperty("executions").GetProperty("count").GetInt32());
            Assert.Equal(8, json.GetProperty("statement_text_length").GetInt32());

            var files = json.GetProperty("files");
            Assert.Equal("p_ABC.sqlplan", files.GetProperty("first").GetProperty("path").GetString());
            Assert.StartsWith("2026-08-04T11:02:07.441238", files.GetProperty("first").GetProperty("captured_at_utc").GetString());
            Assert.EndsWith("Z", files.GetProperty("first").GetProperty("captured_at_utc").GetString());
            Assert.Equal("p_ABC.worst.sqlplan", files.GetProperty("worst").GetProperty("path").GetString());
            Assert.StartsWith("2026-08-04T13:05:56.910744", files.GetProperty("worst").GetProperty("captured_at_utc").GetString());

            // Les findings publiés sont ceux de la ligne retenue (is_worst).
            Assert.Equal("row_goal_defeated", json.GetProperty("findings")[0].GetProperty("kind").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    // Un plan vu une seule fois est à la fois is_first et is_worst, mais le writer n'a
    // produit QU'UN fichier : p_ONE.sqlplan. Publier p_ONE.worst.sqlplan enverrait tout
    // consommateur du digest — l'agent LLM le premier — ouvrir un fichier inexistant.
    [Fact]
    public void First_and_worst_share_the_base_file_when_no_strictly_slower_execution()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("t.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [
                new PreparedPlanProfile(P("ONE", 10, new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc)),
                    PlanWriteOutcome.WroteFirst)]);
            db.FinalizePlanRun(runId);

            new PlanArtifactWriter(dir).WriteDigests(db.ReadPlanDigestRows(runId));

            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "p_ONE.digest.json"))).RootElement;
            Assert.Equal(1, json.GetProperty("executions").GetProperty("count").GetInt32());
            Assert.Equal("p_ONE.sqlplan", json.GetProperty("files").GetProperty("first").GetProperty("path").GetString());
            Assert.Equal("p_ONE.sqlplan", json.GetProperty("files").GetProperty("worst").GetProperty("path").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    // L'autre chemin menant à is_worst == is_first : des durées décroissantes. Le writer
    // n'écrase jamais, donc là encore aucun .worst.sqlplan n'existe.
    [Fact]
    public void Decreasing_durations_publish_the_base_file_as_worst()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            var t0 = new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc);
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("t.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [
                new PreparedPlanProfile(P("DEC", 300, t0), PlanWriteOutcome.WroteFirst),
                new PreparedPlanProfile(P("DEC", 200, t0.AddMinutes(1)), PlanWriteOutcome.Skipped),
                new PreparedPlanProfile(P("DEC", 100, t0.AddMinutes(2)), PlanWriteOutcome.Skipped)]);
            db.FinalizePlanRun(runId);

            new PlanArtifactWriter(dir).WriteDigests(db.ReadPlanDigestRows(runId));

            var files = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "p_DEC.digest.json")))
                .RootElement.GetProperty("files");
            Assert.Equal("p_DEC.sqlplan", files.GetProperty("first").GetProperty("path").GetString());
            Assert.Equal("p_DEC.sqlplan", files.GetProperty("worst").GetProperty("path").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    // Toute chaîne publiée doit désigner un fichier réellement présent.
    [Fact]
    public void Every_published_path_exists_on_disk()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            var t0 = new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc);
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("t.xel", 1, 0, "off");

            var writer = new PlanArtifactWriter(dir);
            var rows = new List<PreparedPlanProfile>();
            foreach (var (hash, dur, min) in ((string, long, int)[])[
                ("AAA", 100, 0), ("AAA", 300, 1), ("AAA", 200, 2), ("BBB", 50, 3)])
            {
                var prof = P(hash, dur, t0.AddMinutes(min));
                rows.Add(new PreparedPlanProfile(prof, writer.Write(prof, $"<plan h='{hash}' d='{dur}'/>")));
            }
            db.InsertPlanProfileBatch(runId, rows);
            db.FinalizePlanRun(runId);

            var digestRows = db.ReadPlanDigestRows(runId);
            foreach (var r in digestRows)
            {
                if (r.First is { } f) Assert.True(File.Exists(Path.Combine(dir, f.Path)), f.Path);
                if (r.Worst is { } w) Assert.True(File.Exists(Path.Combine(dir, w.Path)), w.Path);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Index_lists_one_row_per_plan_with_finding_kinds()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("t.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [
                new PreparedPlanProfile(P("AAA", 100, new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc),
                    new PlanFinding("large_scan", 10, "{}")), PlanWriteOutcome.WroteFirst),
                new PreparedPlanProfile(P("BBB", 200, new DateTime(2026, 8, 4, 11, 0, 0, DateTimeKind.Utc)),
                    PlanWriteOutcome.WroteFirst)]);
            db.FinalizePlanRun(runId);

            var rows = db.ReadPlanDigestRows(runId);
            var w = new PlanArtifactWriter(dir);
            w.WriteIndex(rows);

            var arr = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "index.json")))
                .RootElement.GetProperty("plans");
            Assert.Equal(2, arr.GetArrayLength());

            var aaa = arr.EnumerateArray().First(e => e.GetProperty("plan_hash").GetString() == "AAA");
            Assert.Equal("large_scan", aaa.GetProperty("finding_kinds")[0].GetString());
            Assert.Equal(200L, arr.EnumerateArray()
                .First(e => e.GetProperty("plan_hash").GetString() == "BBB")
                .GetProperty("duration_us").GetProperty("max").GetInt64());
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }
}
