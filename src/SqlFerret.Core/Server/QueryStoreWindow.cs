using System.Globalization;

namespace SqlFerret.Core.Server;

/// <summary>A resolved Query Store time window. Both bounds null ⇒ extract everything.</summary>
public readonly record struct QueryStoreWindow(DateTime? From, DateTime? To)
{
    public bool IsBounded => From is not null || To is not null;

    /// <summary>
    /// Resolves the window from CLI flags. `last` (e.g. "24h"/"7d") is mutually exclusive with
    /// `from`/`to` and resolves to (now − span, now). Throws <see cref="ArgumentException"/> on a
    /// bad value or a mutual-exclusion violation.
    /// </summary>
    public static QueryStoreWindow Parse(string? from, string? to, string? last, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(last))
        {
            if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
                throw new ArgumentException("--last cannot be combined with --from/--to");
            return new QueryStoreWindow(now - ParseSpan(last), now);
        }
        return new QueryStoreWindow(ParseDate(from), ParseDate(to));
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            throw new ArgumentException($"invalid datetime: '{s}'");
        return dt;
    }

    private static TimeSpan ParseSpan(string last)
    {
        var unit = last[^1];
        if (!int.TryParse(last[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException($"invalid --last value: '{last}' (expected e.g. 24h or 7d)");
        return unit switch
        {
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            _ => throw new ArgumentException($"invalid --last unit in '{last}' (use h or d)"),
        };
    }
}
