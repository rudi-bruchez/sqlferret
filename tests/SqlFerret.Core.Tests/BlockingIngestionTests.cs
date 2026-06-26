// tests/SqlFerret.Core.Tests/BlockingIngestionTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public class BlockingIngestionTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sf_{Guid.NewGuid():N}.duckdb");

    private static BlockingProcess Proc(int spid, WaitResourceType t, long? waitUs, string sql) =>
        new(spid, 0, "suspended", "KEY: 5:1 (x)", t, null, null, waitUs, "S", "read committed (2)", 1,
            "WedaApp", "WS", "svc", sql, "fp_" + spid);

    [Fact]
    public void InsertBlockingBatch_persists_reports_and_processes()
    {
        var path = TempDb();
        try
        {
            using var db = DuckDbProject.Open(path);
            long runId = db.BeginRun("logs/", 1, 0, "masked");
            var report = new BlockingReport(new DateTime(2026, 2, 24), 42, 5,
                Proc(201, WaitResourceType.Key, 5_972_000L, "exec dbo.X"),
                Proc(118, WaitResourceType.Other, null, "update dbo.Y"));
            var prepared = new PreparedBlockingReport(report,
                new PreparedBlockingProcess(report.Blocked, null, "exec dbo.X"),
                new PreparedBlockingProcess(report.Blocking, null, "update dbo.Y"));

            db.InsertBlockingBatch(runId, [prepared]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT count(*) FROM blocking_reports";
            Assert.Equal(1L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT count(*) FROM blocking_processes";
            Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            c.CommandText = "SELECT wait_time_us FROM blocking_processes WHERE role='blocked'";
            Assert.Equal(5_972_000L, Convert.ToInt64(c.ExecuteScalar()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void InsertDeadlockBatch_reopen_and_append_no_pk_collision()
    {
        var path = TempDb();
        try
        {
            // Session 1: insert one blocking report and one deadlock report
            using (var db = DuckDbProject.Open(path))
            {
                long runId = db.BeginRun("logs/", 1, 0, "masked");
                var report = new BlockingReport(new DateTime(2026, 2, 24), 1, 5,
                    Proc(201, WaitResourceType.Key, 1_000_000L, "exec dbo.A"),
                    Proc(118, WaitResourceType.Other, null, "update dbo.B"));
                var prepared = new PreparedBlockingReport(report,
                    new PreparedBlockingProcess(report.Blocked, null, "exec dbo.A"),
                    new PreparedBlockingProcess(report.Blocking, null, "update dbo.B"));
                db.InsertBlockingBatch(runId, [prepared]);

                var deadlock1 = new DeadlockReport(new DateTime(2026, 2, 24), [201], [201, 118], "<deadlock/>");
                db.InsertDeadlockBatch(runId, [deadlock1]);
            }

            // Session 2: reopen the SAME project file and insert another deadlock
            using (var db = DuckDbProject.Open(path))
            {
                long runId = db.BeginRun("logs/", 1, 0, "masked");
                var deadlock2 = new DeadlockReport(new DateTime(2026, 2, 25), [301], [301, 400], "<deadlock2/>");
                db.InsertDeadlockBatch(runId, [deadlock2]); // must NOT throw PK collision
            }

            // Verify: 2 distinct deadlock_reports
            using (var db = DuckDbProject.Open(path))
            {
                using var c = db.Connection.CreateCommand();
                c.CommandText = "SELECT count(*) FROM deadlock_reports";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
                c.CommandText = "SELECT count(DISTINCT report_id) FROM deadlock_reports";
                Assert.Equal(2L, Convert.ToInt64(c.ExecuteScalar()));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ingest_routes_blocking_report_and_counts_it()
    {
        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var xml = "<blocked-process-report monitorLoop=\"1\">" +
                      "<blocked-process><process spid=\"201\" waitresource=\"OBJECT: 5:99:0\" waittime=\"5000\">" +
                      "<inputbuf>exec dbo.X @Nir='2921225462283'</inputbuf></process></blocked-process>" +
                      "<blocking-process><process spid=\"118\"><inputbuf>update dbo.Y set a=1 where id=2</inputbuf></process></blocking-process>" +
                      "</blocked-process-report>";
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = xml },
                new Dictionary<string, object?>());

            var result = new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Masked, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            Assert.Equal(1, result.Blocking);
            Assert.Equal(0, result.Unmapped);     // not counted as unmapped

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT inputbuf FROM blocking_processes WHERE role='blocked'";
            var stored = (string)c.ExecuteScalar()!;
            Assert.DoesNotContain("2921225462283", stored);   // literal redacted via normalization
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Fix 1 — tokenize-failure redaction leak
    // SQL Server truncates inputbuf at ~4000 chars, often mid-statement.
    // An unterminated string literal causes ScriptDom to fail tokenization;
    // FallbackCollapse only lowercases+collapses whitespace — it does NOT strip literals.
    // Under any non-Off redaction mode the PII literal must NOT appear in storage.
    [Fact]
    public void Ingest_tokenize_failure_does_not_leak_pii_under_masked_redaction()
    {
        // The missing closing quote makes ScriptDom return parse errors → FallbackCollapse path
        const string pii = "2921225462283";
        const string truncatedInputbuf = $"exec dbo.X @Nir='{pii}"; // unterminated string — no closing quote

        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var xml = "<blocked-process-report monitorLoop=\"1\">" +
                      $"<blocked-process><process spid=\"201\" waitresource=\"OBJECT: 5:99:0\" waittime=\"5000\">" +
                      $"<inputbuf>{truncatedInputbuf}</inputbuf></process></blocked-process>" +
                      "<blocking-process><process spid=\"118\"><inputbuf>update dbo.Y set a=1</inputbuf></process></blocking-process>" +
                      "</blocked-process-report>";
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = xml },
                new Dictionary<string, object?>());

            new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Masked, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            using var c = db.Connection.CreateCommand();

            // blocking_processes.inputbuf must not expose the PII literal
            c.CommandText = "SELECT inputbuf FROM blocking_processes WHERE role='blocked'";
            var storedInputbuf = c.ExecuteScalar() as string ?? "";
            Assert.DoesNotContain(pii, storedInputbuf, StringComparison.Ordinal);

            // normalized_queries.normalized_sql must not expose the PII literal either
            c.CommandText = "SELECT normalized_sql FROM normalized_queries";
            var storedNormalized = c.ExecuteScalar() as string;
            if (storedNormalized is not null)
                Assert.DoesNotContain(pii, storedNormalized, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private const string SampleBlockedXml =
        "<blocked-process-report monitorLoop=\"1\">" +
        "<blocked-process><process spid=\"201\" waitresource=\"OBJECT: 5:99:0\" waittime=\"5000\">" +
        "<inputbuf>update dbo.Y set a=1 where id=2</inputbuf></process></blocked-process>" +
        "<blocking-process><process spid=\"118\"><inputbuf>select 1</inputbuf></process></blocking-process>" +
        "</blocked-process-report>";

    [Fact]
    public void Ingest_stores_raw_blocking_xml_when_redaction_off()
    {
        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = SampleBlockedXml },
                new Dictionary<string, object?>());

            new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Off, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT raw_xml FROM blocking_reports";
            Assert.Equal(SampleBlockedXml, (string)c.ExecuteScalar()!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ingest_nulls_raw_blocking_xml_when_redaction_masked()
    {
        var path = TempDb();
        try
        {
            using var db = SqlFerret.Core.Storage.DuckDbProject.Open(path);
            var ev = new FakeEvent("blocked_process_report", new DateTime(2026, 2, 24),
                new Dictionary<string, object?> { ["blocked_process"] = SampleBlockedXml },
                new Dictionary<string, object?>());

            new SqlFerret.Core.Ingestion.IngestionService(db,
                    new SqlFerret.Core.Ingestion.IngestionOptions(SqlFerret.Core.Parameters.RedactionMode.Masked, []))
                .Ingest("logs/", [((SqlFerret.Core.Ingestion.IXeEventData)ev, "s.xel", 0L)]);

            using var c = db.Connection.CreateCommand();
            c.CommandText = "SELECT raw_xml FROM blocking_reports";
            Assert.True(c.ExecuteReader() is var r && r.Read() && r.IsDBNull(0));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
