using SqlFerret.Core.Model;

namespace SqlFerret.Core.Filtering;

public static class FilterCompiler
{
    private static readonly HashSet<string> NumericFields = new()
    {
        "duration_us", "cpu_time_us", "logical_reads", "physical_reads",
        "writes", "row_count", "session_id"
    };

    private static readonly HashSet<string> AllowedFields = new(NumericFields)
    {
        "object_name", "event_name", "event_class", "database_name", "login_name",
        "client_app_name", "client_hostname", "statement_kind", "is_system", "normalized_hash"
    };

    public static string ToWhereClause(IEnumerable<FilterRule> rules)
    {
        var clauses = rules
            .Where(r => r.Enabled && r.Stage == "view" && AllowedFields.Contains(r.Field))
            .Select(BuildClause)
            .Where(c => c is not null)
            .ToList();
        return clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses);
    }

    private static string? BuildClause(FilterRule r)
    {
        bool numeric = NumericFields.Contains(r.Field);
        string Val(string v) => numeric ? v : $"'{v.Replace("'", "''")}'";

        string predicate = r.Op switch
        {
            "in"  => $"{r.Field} IN ({string.Join(", ", (r.Values ?? []).Select(Val))})",
            "eq"  => $"{r.Field} = {Val(r.Value!)}",
            "neq" => $"{r.Field} <> {Val(r.Value!)}",
            "gt"  => $"{r.Field} > {Val(r.Value!)}",
            "lt"  => $"{r.Field} < {Val(r.Value!)}",
            "gte" => $"{r.Field} >= {Val(r.Value!)}",
            "lte" => $"{r.Field} <= {Val(r.Value!)}",
            "like"=> $"{r.Field} LIKE {Val(r.Value!)}",
            _ => ""
        };
        if (predicate == "") return null;

        // exclude → negate; keep → assert
        return r.Action == "exclude" ? NegateInPlace(r, predicate) : $"({predicate})";
    }

    private static string NegateInPlace(FilterRule r, string predicate) => r.Op switch
    {
        "in"  => predicate.Replace(" IN (", " NOT IN ("),
        "eq"  => predicate.Replace(" = ", " <> "),
        "like"=> $"({predicate.Replace(" LIKE ", " NOT LIKE ")})",
        _     => $"(NOT ({predicate}))"
    };

    public static Func<ExecutionEvent, bool> ToIngestPredicate(IEnumerable<FilterRule> rules)
    {
        var ingest = rules.Where(r => r.Enabled && r.Stage == "ingest" && AllowedFields.Contains(r.Field)).ToList();
        return ev =>
        {
            foreach (var r in ingest)
            {
                bool matches = Matches(r, ev);
                if (r.Action == "exclude" && matches) return false; // drop
                if (r.Action == "keep" && !matches) return false;   // keep-only: drop non-matches
            }
            return true;
        };
    }

    private static bool Matches(FilterRule r, ExecutionEvent ev)
    {
        string? field = r.Field switch
        {
            "object_name" => ev.ObjectName,
            "event_name" => ev.EventName,
            "event_class" => ev.EventClass.ToString(),
            "database_name" => ev.DatabaseName,
            "login_name" => ev.LoginName,
            "client_app_name" => ev.ClientAppName,
            "client_hostname" => ev.ClientHostname,
            "is_system" => ev.IsSystem.ToString().ToLowerInvariant(),
            _ => null
        };
        if (field is null) return false;
        return r.Op switch
        {
            "in"  => r.Values?.Contains(field, StringComparer.OrdinalIgnoreCase) ?? false,
            "eq"  => string.Equals(field, r.Value, StringComparison.OrdinalIgnoreCase),
            "neq" => !string.Equals(field, r.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
