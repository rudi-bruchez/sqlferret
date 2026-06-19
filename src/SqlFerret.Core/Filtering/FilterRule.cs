namespace SqlFerret.Core.Filtering;

public record FilterRule(
    string Id, string Field, string Op,
    string[]? Values, string? Value,
    string Stage, string Action, bool Enabled);
