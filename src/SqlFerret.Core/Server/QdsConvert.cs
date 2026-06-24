using System.Globalization;

namespace SqlFerret.Core.Server;

/// <summary>
/// Pure conversions used when reading Query Store rows — isolated here so the marshaling rules are
/// unit-testable without a live SQL Server (the class of bug a skipped integration test misses).
/// </summary>
public static class QdsConvert
{
    /// <summary>
    /// Query Store datetime columns are <c>datetimeoffset</c>; <see cref="System.Data.IDataRecord.GetValue"/>
    /// returns a boxed <see cref="DateTimeOffset"/>, which <see cref="Convert.ToDateTime(object)"/> cannot
    /// unbox (it throws <see cref="InvalidCastException"/>). Normalize to a UTC <see cref="DateTime"/>.
    /// </summary>
    public static DateTime AsDateTime(object value) =>
        value is DateTimeOffset dto ? dto.UtcDateTime : Convert.ToDateTime(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Query Store wait times are <c>float</c> milliseconds. Convert to microseconds with rounding,
    /// reading the source as a double first so sub-millisecond waits are not truncated to zero.
    /// </summary>
    public static long MsToUs(double ms) => (long)Math.Round(ms * 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A window bound as <see cref="DateTimeOffset"/> (the QS interval column type). A null bound opens
    /// that end of the interval via <paramref name="fallback"/> (Min/Max). The clock time is treated as
    /// UTC regardless of the input <see cref="DateTimeKind"/>.
    /// </summary>
    public static DateTimeOffset WindowBound(DateTime? value, DateTimeOffset fallback) =>
        value is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero) : fallback;
}
