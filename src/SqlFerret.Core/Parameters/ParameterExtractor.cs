using System.Text.RegularExpressions;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Parameters;

public static class ParameterExtractor
{
    // Matches "@name = <value>" where value is a quoted string (incl. N'') or a bare token.
    private static readonly Regex Assignment = new(
        @"(?<name>@\w+)\s*=\s*(?<value>N?'(?:[^']|'')*'|[^,;\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<RawParameter> Extract(EventClass eventClass, string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText)) return [];

        double confidence = sqlText.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase) ? 0.7
                          : eventClass == EventClass.RpcCall ? 0.9 : 0.6;

        var result = new List<RawParameter>();
        int ordinal = 0;
        foreach (Match m in Assignment.Matches(sqlText))
        {
            result.Add(new RawParameter(
                Ordinal: ordinal++,
                Name: m.Groups["name"].Value,
                SourceKind: ParameterSourceKind.RpcParameter,
                SqlTypeGuess: GuessType(m.Groups["value"].Value),
                ValueText: m.Groups["value"].Value,
                ParseConfidence: confidence));
        }
        return result;
    }

    private static string GuessType(string value)
    {
        if (value.StartsWith("N'")) return "nvarchar";
        if (value.StartsWith("'")) return "varchar";
        if (long.TryParse(value, out _)) return "int";
        if (decimal.TryParse(value, out _)) return "decimal";
        return "unknown";
    }
}
