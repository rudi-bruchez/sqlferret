using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

public class PlanStorageTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static PlanProfile P(string hash, long? durationUs, params PlanFinding[] findings) => new()
    {
        PlanHash = hash,
        PlanHashSource = "queryplanhash",
        FileStem = "p_" + hash,
        StatementCount = 1,
        QueryHash = "56006EFD9093F347",
        StatementType = "SELECT",
        StatementText = "SELECT 1",
        StatementTextLength = 8,
        CapturedAt = new DateTime(2026, 8, 4, 13, 5, 56, DateTimeKind.Utc),
        DurationUs = durationUs,
        CpuTimeUs = 41_200_000,
        EstimatedRows = 50000,
        SubtreeCost = 8868.44,
        Dop = 8,
        SerialDesiredMemoryKb = 42_225_192,
        GrantedMemoryKb = 20_193_000,
        MaxUsedMemoryKb = 16_533_000,
        Findings = findings,
    };

    [Fact]
    public void Schema_creates_plan_tables_and_migrates_ingestion_runs()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            using var c = db.Connection.CreateCommand();

            c.CommandText = "SELECT count(*) FROM plan_profiles";
            Assert.Equal(0L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM plan_findings";
            Assert.Equal(0L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT events_plan_profiles, plan_parse_failures, plan_write_failures FROM ingestion_runs";
            Assert.False(c.ExecuteReader().Read());   // colonnes présentes, table vide
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void InsertPlanProfileBatch_persists_row_and_its_findings()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("trace_0.xel", 1, 0, "off");

            db.InsertPlanProfileBatch(runId, [
                new PreparedPlanProfile(
                    P("ABC", 225_183_000,
                      new PlanFinding("row_goal_defeated", 9, """{"rows":100}"""),
                      new PlanFinding("large_scan", 10, """{"table":"WidgetRecalc"}""")),
                    PlanWriteOutcome.WroteFirst)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT plan_hash, is_first, is_worst, duration_us, dop, statement_text_length FROM plan_profiles";
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("ABC", r.GetString(0));
            Assert.True(r.GetBoolean(1));
            Assert.False(r.GetBoolean(2));
            Assert.Equal(225_183_000L, r.GetInt64(3));
            Assert.Equal(8, r.GetInt32(4));
            Assert.Equal(8, r.GetInt32(5));
            r.Close();

            c.CommandText = "SELECT count(*) FROM plan_findings";
            Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT kind FROM plan_findings WHERE node_id = 9";
            Assert.Equal("row_goal_defeated", (string)c.ExecuteScalar()!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Only_WroteFirst_sets_is_first()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("trace_0.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [
                new PreparedPlanProfile(P("A", 1), PlanWriteOutcome.WroteFirst),
                new PreparedPlanProfile(P("A", 2), PlanWriteOutcome.WroteWorst),
                new PreparedPlanProfile(P("A", 1), PlanWriteOutcome.Skipped),
                new PreparedPlanProfile(P("A", 1), PlanWriteOutcome.Failed)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM plan_profiles WHERE is_first";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void sqlplan_path_is_null_at_insert_time()
    {
        // Il n'est renseigné qu'en passe finale : un chemin posé en flux deviendrait
        // mensonger dès qu'une exécution plus lente écrase le fichier .worst.
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("trace_0.xel", 1, 0, "off");
            db.InsertPlanProfileBatch(runId, [new PreparedPlanProfile(P("A", 1), PlanWriteOutcome.WroteFirst)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM plan_profiles WHERE sqlplan_path IS NULL";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reopen_and_append_no_pk_collision()
    {
        var path = TempDb();
        try
        {
            using (var db = DuckDbProject.Open(path))
            {
                long runId = db.BeginRun("t1.xel", 1, 0, "off");
                db.InsertPlanProfileBatch(runId, [new PreparedPlanProfile(P("A", 1), PlanWriteOutcome.WroteFirst)]);
            }
            using (var db = DuckDbProject.Open(path))
            {
                long runId = db.BeginRun("t2.xel", 1, 0, "off");
                db.InsertPlanProfileBatch(runId, [new PreparedPlanProfile(P("B", 1), PlanWriteOutcome.WroteFirst)]);
            }
            using (var db = DuckDbProject.Open(path))
            {
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(DISTINCT plan_profile_id) FROM plan_profiles";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PeekNextRunId_matches_the_id_BeginRun_assigns_twice_in_a_row()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);

            long peek1 = db.PeekNextRunId();
            Assert.Equal(peek1, db.PeekNextRunId());          // idempotent
            Assert.Equal(peek1, db.BeginRun("a.xel", 1, 0, "off"));

            long peek2 = db.PeekNextRunId();
            Assert.NotEqual(peek1, peek2);
            Assert.Equal(peek2, db.BeginRun("b.xel", 1, 0, "off"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
