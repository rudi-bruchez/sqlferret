using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public class PlanIngestionTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"sf_plans_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static IngestionResult Run(DuckDbProject db, string? planDir, params IXeEventData[] events) =>
        new IngestionService(db, new IngestionOptions(RedactionMode.Off, [], PlanProfileDir: planDir))
            .Ingest("trace_0.xel", events.Select(e => (e, "trace_0.xel", 0L)));

    [Fact]
    public void Plan_profile_is_counted_and_never_marked_unmapped()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            var result = Run(db, dir, PlanProfileParserTests.PlanEvent(PlanProfileParserTests.RealPlan));

            Assert.Equal(1, result.PlanProfiles);
            Assert.Equal(0, result.Unmapped);          // l'invariant qui compte
            Assert.Equal(0, result.PlanParseFailures);
            Assert.Equal(0, result.PlanWriteFailures);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT plan_hash FROM plan_profiles";
            Assert.Equal("14A792EA4D5BF89A", (string)c.ExecuteScalar()!);
            Assert.True(File.Exists(Path.Combine(dir, "p_14A792EA4D5BF89A.sqlplan")));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Malformed_plan_counts_as_parse_failure_not_unmapped()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            var ev = PlanProfileParserTests.PlanEvent("<ShowPlanXML><unclosed>");
            var result = Run(db, dir, ev);

            Assert.Equal(0, result.PlanProfiles);
            Assert.Equal(1, result.PlanParseFailures);
            Assert.Equal(0, result.Unmapped);
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_showplan_field_counts_as_parse_failure()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            var ev = new FakeEvent("query_post_execution_plan_profile", new DateTime(2026, 8, 4),
                new Dictionary<string, object?>(), new Dictionary<string, object?>());
            var result = Run(db, null, ev);

            Assert.Equal(1, result.PlanParseFailures);
            Assert.Equal(0, result.Unmapped);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Counters_are_persisted_on_ingestion_runs()
    {
        var path = TempDb(); var dir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            Run(db, dir, PlanProfileParserTests.PlanEvent(PlanProfileParserTests.RealPlan));

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT events_plan_profiles, plan_parse_failures, plan_write_failures FROM ingestion_runs";
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(1L, r.GetInt64(0));
            Assert.Equal(0L, r.GetInt64(1));
            Assert.Equal(0L, r.GetInt64(2));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Trailing_partial_batch_is_flushed()
    {
        // BatchSize = 5000 par défaut ; 3 événements ne déclenchent aucun flush intermédiaire.
        var path = TempDb(); var dir = TempDir();
        try
        {
            using var db = DuckDbProject.Open(path);
            Run(db, dir,
                PlanProfileParserTests.PlanEvent(PlanProfileParserTests.RealPlan),
                PlanProfileParserTests.PlanEvent(PlanProfileParserTests.RealPlan),
                PlanProfileParserTests.PlanEvent(PlanProfileParserTests.RealPlan));

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM plan_profiles";
            Assert.Equal(3L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir, true); }
    }
}
