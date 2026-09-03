// tests/SqlFerret.Core.Tests/HealthCycleGroupingTests.cs
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class HealthCycleGroupingTests
{
    private static ServerDiagnosticsSample S(DateTime ts, string comp) =>
        new(ts, comp, "CLEAN", DiagnosticsOutcome.Parsed, [], [], [], [], [], [], []);

    /// <summary>
    /// Les quatre evenements d'un cycle arrivent a moins d'une milliseconde d'ecart, dans cet
    /// ordre, avec des horodatages tous distincts.
    /// </summary>
    private static IReadOnlyList<ServerDiagnosticsSample> Cycle(DateTime at) =>
    [
        S(at, "SYSTEM"),
        S(at.AddTicks(619), "RESOURCE"),
        S(at.AddTicks(2352), "QUERY_PROCESSING"),
        S(at.AddTicks(2393), "IO_SUBSYSTEM"),
    ];

    [Fact]
    public void Four_components_under_a_millisecond_apart_are_one_cycle()
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, 900, DateTimeKind.Utc);

        var cycles = HealthCycleGrouper.Group([.. Cycle(at)]);

        var c = Assert.Single(cycles);
        Assert.Equal(4, c.Samples.Count);
        Assert.Equal(at, c.CycleAt);            // le plus tot des quatre
    }

    /// <summary>
    /// Une session unique reste une serie quelle que soit sa cadence. Une premiere version se
    /// clait sur la seconde-dans-la-minute et decoupait la meme session en soixante series des
    /// que la periode n'etait pas un nombre rond de minutes.
    /// </summary>
    [Theory]
    [InlineData(300)]   // 5 min pile
    [InlineData(270)]   // 4,5 min — la cadence mesuree sur la capture reelle
    [InlineData(299)]   // decalait la seconde a chaque cycle
    [InlineData(137)]   // cadence quelconque
    public void One_session_stays_one_series_whatever_its_cadence(int periodSeconds)
    {
        var at = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 0; i < 40; i++) all.AddRange(Cycle(at.AddSeconds((double)periodSeconds * i)));

        Assert.Single(HealthCycleGrouper.Group(all).Select(c => c.SeriesKey).Distinct());
    }

    /// <summary>
    /// Deux sessions entrelacees ne sont PAS separees, et c'est delibere : la capture ne dit pas
    /// quand chaque session a commence. Tous les cycles sont conserves, dans l'ordre, et
    /// l'irregularite de la cadence est signalee par le digest sans etre attribuee.
    /// </summary>
    [Fact]
    public void Interleaved_sessions_are_kept_whole_and_not_split_by_guesswork()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var b = a.AddMinutes(35.5);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 0; i < 12; i++)
        {
            all.AddRange(Cycle(a.AddMinutes(5 * i)));
            all.AddRange(Cycle(b.AddMinutes(5 * i)));
        }

        var cycles = HealthCycleGrouper.Group(all);

        Assert.Equal(24, cycles.Count);
        Assert.Single(cycles.Select(c => c.SeriesKey).Distinct());
        Assert.Equal(cycles.Select(c => c.CycleAt).OrderBy(x => x), cycles.Select(c => c.CycleAt));
    }

    [Fact]
    public void A_single_series_yields_one_series_key()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 0; i < 5; i++) all.AddRange(Cycle(a.AddMinutes(5 * i)));

        Assert.Single(HealthCycleGrouper.Group(all).Select(c => c.SeriesKey).Distinct());
    }

    [Fact]
    public void Cycles_come_back_in_chronological_order()
    {
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 4; i >= 0; i--) all.AddRange(Cycle(a.AddMinutes(5 * i)));   // sems en desordre

        var cycles = HealthCycleGrouper.Group(all);

        Assert.Equal(5, cycles.Count);
        Assert.Equal(cycles.Select(c => c.CycleAt).OrderBy(x => x), cycles.Select(c => c.CycleAt));
    }
}
