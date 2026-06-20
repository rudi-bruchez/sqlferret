using System.Globalization;

namespace SqlFerret.Core.Ingestion;

/// <summary>
/// Renders an ImportProgress as one plain-ASCII line shared by the CLI and TUI hosts.
/// Counts are abbreviated (k / M); percentages are floored to integers.
/// </summary>
public static class ImportProgressText
{
    public static string Render(ImportProgress p)
    {
        string head = p.FileCount > 0 ? $"[{p.FileIndex}/{p.FileCount}] {p.CurrentFile}" : "";
        return $"{head}  file {Pct(p.FileFraction)}%  overall {Pct(p.OverallFraction)}%  " +
               $"read={Abbrev(p.Read)} mapped={Abbrev(p.Mapped)} unmapped={Abbrev(p.Unmapped)} " +
               $"cleaned={Abbrev(p.Cleaned)} failures={Abbrev(p.TokenizeFailures)}";
    }

    public static string Abbrev(long n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000) return (n / 1000).ToString(CultureInfo.InvariantCulture) + "k";
        var millions = Math.Floor(n / 1_000_000.0 * 10) / 10;
        return millions.ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    private static int Pct(double f) => Math.Clamp((int)(f * 100), 0, 100);
}
