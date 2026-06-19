namespace SqlFerret.Core.Model;
public record NormalizedQuery(
    string NormalizedSql, string NormalizedHash,
    string StatementKind, string? PrimaryTable, bool TokenizeFailed);
