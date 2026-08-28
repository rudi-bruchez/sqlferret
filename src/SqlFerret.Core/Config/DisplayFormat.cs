using System.Globalization;

namespace SqlFerret.Core.Config;

public static class DisplayFormat
{
    public static string Duration(long microseconds, string unit) => unit switch
    {
        "s" => $"{(microseconds / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture)} s",
        "us" => $"{microseconds} us",
        _ => $"{microseconds / 1000} ms",
    };
}
