using SqlFerret.Core.Plans;
using SqlFerret.Core.Storage;

public class PlanFinalizeTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static PlanProfile P(string hash, long? durationUs) => new()
    {
        PlanHash = hash,
        PlanHashSource = "queryplanhash",
        FileStem = "p_" + hash,
        StatementCount = 1,
        CapturedAt = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
        DurationUs = durationUs,
    };

    private static (DuckDbProject Db, long RunId) Seed(string path, params (string Hash, long? Dur, PlanWriteOutcome Out)[] rows)
    {
        var db = DuckDbProject.Open(path);
        long runId = db.BeginRun("t.xel", 1, 0, "off");
        db.InsertPlanProfileBatch(runId,
            rows.Select(r => new PreparedPlanProfile(P(r.Hash, r.Dur), r.Out)).ToList());
        return (db, runId);
    }

    [Fact]
    public void Slowest_execution_is_marked_worst()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", 100, PlanWriteOutcome.WroteFirst),
                ("A", 300, PlanWriteOutcome.WroteWorst),
                ("A", 200, PlanWriteOutcome.Skipped));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT duration_us FROM plan_profiles WHERE is_worst";
                Assert.Equal(300L, Convert.ToInt64(c.ExecuteScalar()));
                c.CommandText = "SELECT count(*) FROM plan_profiles WHERE is_worst";
                Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Le writer en flux n'écrase que sur un > strict : à durées égales, la PREMIÈRE
    // exécution ayant atteint le maximum garde le fichier. SQL doit trancher pareil,
    // sinon le digest publierait un horodatage ne correspondant pas au .worst.sqlplan.
    [Fact]
    public void Ties_are_broken_by_lowest_plan_profile_id()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", 300, PlanWriteOutcome.WroteFirst),
                ("A", 300, PlanWriteOutcome.Skipped),
                ("A", 300, PlanWriteOutcome.Skipped));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT min(plan_profile_id) FROM plan_profiles";
                long first = Convert.ToInt64(c.ExecuteScalar());
                c.CommandText = "SELECT plan_profile_id FROM plan_profiles WHERE is_worst";
                Assert.Equal(first, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Null_durations_never_win_and_produce_no_worst_row()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", null, PlanWriteOutcome.WroteFirst),
                ("A", null, PlanWriteOutcome.Skipped));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(*) FROM plan_profiles WHERE is_worst";
                Assert.Equal(0L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_non_null_duration_beats_nulls()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", null, PlanWriteOutcome.WroteFirst),
                ("A", 5, PlanWriteOutcome.WroteWorst));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT duration_us FROM plan_profiles WHERE is_worst";
                Assert.Equal(5L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void sqlplan_path_is_set_only_on_first_and_worst_rows()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", 100, PlanWriteOutcome.WroteFirst),
                ("A", 300, PlanWriteOutcome.WroteWorst),
                ("A", 200, PlanWriteOutcome.Skipped));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT sqlplan_path FROM plan_profiles WHERE is_first";
                Assert.Equal("p_A.sqlplan", (string)c.ExecuteScalar()!);
                c.CommandText = "SELECT sqlplan_path FROM plan_profiles WHERE is_worst";
                Assert.Equal("p_A.worst.sqlplan", (string)c.ExecuteScalar()!);
                c.CommandText = "SELECT count(*) FROM plan_profiles WHERE sqlplan_path IS NULL";
                Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Une ligne dont l'écriture a échoué ne peut pas remporter is_worst : le fichier
    // qu'on lui attribuerait n'existe pas sur le disque.
    [Fact]
    public void Failed_writes_are_excluded_from_the_worst_ranking()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", 100, PlanWriteOutcome.WroteFirst),
                ("A", 900, PlanWriteOutcome.Failed));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT duration_us FROM plan_profiles WHERE is_worst";
                Assert.Equal(100L, Convert.ToInt64(c.ExecuteScalar()));
                c.CommandText = "SELECT count(*) FROM plan_profiles WHERE write_outcome='Failed' AND sqlplan_path IS NOT NULL";
                Assert.Equal(0L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Le piège que la revue a trouvé : une seule exécution est à la fois is_first et
    // is_worst, mais le writer n'a produit que le fichier de base.
    [Fact]
    public void Single_execution_gets_the_base_path_not_a_phantom_worst_path()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path, ("A", 100, PlanWriteOutcome.WroteFirst));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT is_first, is_worst, sqlplan_path FROM plan_profiles";
                using var r = c.ExecuteReader();
                Assert.True(r.Read());
                Assert.True(r.GetBoolean(0));
                Assert.True(r.GetBoolean(1));
                Assert.Equal("p_A.sqlplan", r.GetString(2));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Superseded_worst_rows_lose_their_path()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", 100, PlanWriteOutcome.WroteFirst),
                ("A", 200, PlanWriteOutcome.WroteWorst),   // écrasée ensuite
                ("A", 300, PlanWriteOutcome.WroteWorst));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(*) FROM plan_profiles WHERE sqlplan_path IS NOT NULL";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
                c.CommandText = "SELECT sqlplan_path FROM plan_profiles WHERE duration_us = 200";
                Assert.True(c.ExecuteReader() is var rr && rr.Read() && rr.IsDBNull(0));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Each_plan_hash_gets_its_own_worst()
    {
        var path = TempDb();
        try
        {
            var (db, runId) = Seed(path,
                ("A", 100, PlanWriteOutcome.WroteFirst),
                ("A", 300, PlanWriteOutcome.WroteWorst),
                ("B", 50, PlanWriteOutcome.WroteFirst),
                ("B", 70, PlanWriteOutcome.WroteWorst));
            using (db)
            {
                db.FinalizePlanRun(runId);
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(*) FROM plan_profiles WHERE is_worst";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
