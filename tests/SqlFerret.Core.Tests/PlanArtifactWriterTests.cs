using SqlFerret.Core.Plans;

public class PlanArtifactWriterTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"sf_plans_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static PlanProfile P(string hash, long? durationUs, string source = "queryplanhash") => new()
    {
        PlanHash = hash,
        PlanHashSource = source,
        FileStem = PlanIdentity.FileStem(hash, source),
        StatementCount = 1,
        CapturedAt = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
        DurationUs = durationUs,
    };

    [Fact]
    public void Multi_and_content_plans_get_their_own_prefixes()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("ABC", 100, "multi"), "<m/>");
            w.Write(P("DEF", 100, "content"), "<c/>");

            Assert.True(File.Exists(Path.Combine(dir, "m_ABC.sqlplan")));
            Assert.True(File.Exists(Path.Combine(dir, "c_DEF.sqlplan")));
            Assert.False(File.Exists(Path.Combine(dir, "p_ABC.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // SQL Server émet les .sqlplan en UTF-16 et leur déclaration XML l'annonce.
    // Écrire les octets en UTF-8 sous une déclaration disant utf-16 produit un fichier
    // incohérent que certains lecteurs refusent.
    [Fact]
    public void Files_are_written_as_utf16_with_bom()
    {
        var dir = TempDir();
        try
        {
            const string xml = """<?xml version="1.0" encoding="utf-16"?><ShowPlanXML>é</ShowPlanXML>""";
            new PlanArtifactWriter(dir).Write(P("ABC", 100), xml);

            var bytes = File.ReadAllBytes(Path.Combine(dir, "p_ABC.sqlplan"));
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);
            Assert.Equal(xml, File.ReadAllText(Path.Combine(dir, "p_ABC.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void First_occurrence_writes_the_base_file()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            Assert.Equal(PlanWriteOutcome.WroteFirst, w.Write(P("ABC", 100), "<plan1/>"));

            Assert.True(File.Exists(Path.Combine(dir, "p_ABC.sqlplan")));
            Assert.False(File.Exists(Path.Combine(dir, "p_ABC.worst.sqlplan")));
            Assert.Equal("<plan1/>", File.ReadAllText(Path.Combine(dir, "p_ABC.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Increasing_durations_produce_a_worst_file_holding_the_slowest()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("ABC", 100), "<plan100/>");
            Assert.Equal(PlanWriteOutcome.WroteWorst, w.Write(P("ABC", 200), "<plan200/>"));
            Assert.Equal(PlanWriteOutcome.WroteWorst, w.Write(P("ABC", 300), "<plan300/>"));

            Assert.Equal("<plan100/>", File.ReadAllText(Path.Combine(dir, "p_ABC.sqlplan")));
            Assert.Equal("<plan300/>", File.ReadAllText(Path.Combine(dir, "p_ABC.worst.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Decreasing_durations_never_produce_a_worst_file()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("ABC", 300), "<plan300/>");
            Assert.Equal(PlanWriteOutcome.Skipped, w.Write(P("ABC", 200), "<plan200/>"));
            Assert.Equal(PlanWriteOutcome.Skipped, w.Write(P("ABC", 100), "<plan100/>"));

            Assert.False(File.Exists(Path.Combine(dir, "p_ABC.worst.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Equal_durations_keep_the_first_one_that_reached_the_maximum()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("ABC", 100), "<first/>");
            Assert.Equal(PlanWriteOutcome.Skipped, w.Write(P("ABC", 100), "<second/>"));

            Assert.False(File.Exists(Path.Combine(dir, "p_ABC.worst.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Une durée nulle ne peut pas départager deux exécutions : elle ne gagne jamais.
    [Fact]
    public void Null_durations_never_win_the_worst_slot()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            Assert.Equal(PlanWriteOutcome.WroteFirst, w.Write(P("ABC", null), "<first/>"));
            Assert.Equal(PlanWriteOutcome.Skipped, w.Write(P("ABC", null), "<second/>"));

            Assert.False(File.Exists(Path.Combine(dir, "p_ABC.worst.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_non_null_duration_beats_a_null_first_occurrence()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("ABC", null), "<first/>");
            Assert.Equal(PlanWriteOutcome.WroteWorst, w.Write(P("ABC", 50), "<slower/>"));

            Assert.Equal("<slower/>", File.ReadAllText(Path.Combine(dir, "p_ABC.worst.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Distinct_hashes_get_distinct_files()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("AAA", 100), "<a/>");
            w.Write(P("BBB", 100), "<b/>");

            Assert.Equal("<a/>", File.ReadAllText(Path.Combine(dir, "p_AAA.sqlplan")));
            Assert.Equal("<b/>", File.ReadAllText(Path.Combine(dir, "p_BBB.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Null_runDir_tracks_dedup_state_but_writes_nothing()
    {
        var w = new PlanArtifactWriter(null);
        Assert.Equal(PlanWriteOutcome.WroteFirst, w.Write(P("ABC", 100), "<a/>"));
        Assert.Equal(PlanWriteOutcome.WroteWorst, w.Write(P("ABC", 200), "<b/>"));
        Assert.Equal(PlanWriteOutcome.Skipped, w.Write(P("ABC", 150), "<c/>"));
    }

    [Fact]
    public void Write_failure_returns_Failed_without_throwing()
    {
        var dir = TempDir();
        try
        {
            // Un répertoire portant le nom du fichier cible fait échouer l'écriture.
            Directory.CreateDirectory(Path.Combine(dir, "p_ABC.sqlplan"));
            var w = new PlanArtifactWriter(dir);
            Assert.Equal(PlanWriteOutcome.Failed, w.Write(P("ABC", 100), "<a/>"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Une écriture ratée ne doit pas laisser sa durée dans l'état : sinon une exécution
    // ultérieure, plus lente que le réel mais moins que celle qui a échoué, serait écartée
    // et aucun fichier ne porterait le pire cas.
    [Fact]
    public void Failed_worst_write_does_not_poison_the_dedup_state()
    {
        var dir = TempDir();
        try
        {
            var w = new PlanArtifactWriter(dir);
            w.Write(P("ABC", 100), "<first/>");

            // Bloque uniquement l'écriture du .worst
            Directory.CreateDirectory(Path.Combine(dir, "p_ABC.worst.sqlplan"));
            Assert.Equal(PlanWriteOutcome.Failed, w.Write(P("ABC", 500), "<blocked/>"));
            Directory.Delete(Path.Combine(dir, "p_ABC.worst.sqlplan"));

            // 200 > 100 : doit toujours l'emporter, l'échec à 500 n'ayant rien mémorisé.
            Assert.Equal(PlanWriteOutcome.WroteWorst, w.Write(P("ABC", 200), "<second/>"));
            Assert.Equal("<second/>", File.ReadAllText(Path.Combine(dir, "p_ABC.worst.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Failed_first_write_is_retried_on_the_next_occurrence()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "p_ABC.sqlplan"));
            var w = new PlanArtifactWriter(dir);
            Assert.Equal(PlanWriteOutcome.Failed, w.Write(P("ABC", 100), "<a/>"));
            Directory.Delete(Path.Combine(dir, "p_ABC.sqlplan"));

            Assert.Equal(PlanWriteOutcome.WroteFirst, w.Write(P("ABC", 100), "<b/>"));
            Assert.Equal("<b/>", File.ReadAllText(Path.Combine(dir, "p_ABC.sqlplan")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
