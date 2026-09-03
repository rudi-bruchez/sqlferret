namespace SqlFerret.Core.Model;

/// <param name="QiCollapsedSql">
/// Same shape as <paramref name="NormalizedSql"/>, except a double-quoted token
/// (<c>TSqlTokenType.AsciiStringOrQuotedIdentifier</c> — distinct from
/// <c>TSqlTokenType.QuotedIdentifier</c>, which is bracket-quoted <c>[...]</c> and unambiguously
/// an identifier, never collapsed) is also collapsed to <c>?</c>. Under
/// <c>SET QUOTED_IDENTIFIER OFF</c> a value like <c>"alice@example.com"</c> tokenizes as a quoted
/// identifier, not a string literal, so <see cref="NormalizedSql"/> alone is not safe to store at
/// <see cref="Normalization.SqlTextSanitization.Literals"/>. The capture never records the
/// session's QUOTED_IDENTIFIER setting, so this is genuinely ambiguous — a legitimately
/// double-quoted identifier (<c>"Order Details"</c>) is lost to <c>?</c> along with any value;
/// fail safe. Computed alongside <see cref="NormalizedSql"/> in the same token pass — never used
/// to change <see cref="NormalizedHash"/>, which stays derived from <see cref="NormalizedSql"/> so
/// fingerprints are unaffected by the sanitization level.
/// </param>
public record NormalizedQuery(
    string NormalizedSql, string NormalizedHash,
    string StatementKind, string? PrimaryTable, string? TargetObject, bool TokenizeFailed,
    string QiCollapsedSql = "");
