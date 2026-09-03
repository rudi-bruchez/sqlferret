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

    [Fact]
    public void Two_interleaved_series_are_detected_as_two()
    {
        // Deux sessions echantillonnant le meme serveur, decalees, chacune toutes les 5 minutes.
        var a = new DateTime(2026, 9, 3, 0, 16, 34, DateTimeKind.Utc);
        var b = new DateTime(2026, 9, 3, 0, 52, 06, DateTimeKind.Utc);
        List<ServerDiagnosticsSample> all = [];
        for (int i = 0; i < 4; i++)
        {
            all.AddRange(Cycle(a.AddMinutes(5 * i)));
            all.AddRange(Cycle(b.AddMinutes(5 * i)));
        }

        var cycles = HealthCycleGrouper.Group(all);

        Assert.Equal(8, cycles.Count);
        Assert.Equal(2, cycles.Select(c => c.SeriesKey).Distinct().Count());
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
