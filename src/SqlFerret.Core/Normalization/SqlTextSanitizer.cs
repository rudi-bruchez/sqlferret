// src/SqlFerret.Core/Normalization/SqlTextSanitizer.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Normalization;

/// <summary>How much of the captured statement text is written to disk.</summary>
public enum SqlTextSanitization
{
    /// <summary>The statement exactly as captured, inlined literals included. Historical default.</summary>
    Raw,
    /// <summary>The normalized form: literals collapsed to '?'. Identifiers are NOT removed.</summary>
    Literals,
}

/// <summary>
/// Rewrites statement text before it reaches storage. Reuses the <see cref="NormalizedQuery"/>
/// the ingestion loop already computed, so no level costs an additional parse.
/// </summary>
public static class SqlTextSanitizer
{
    /// <summary>
    /// Bump when the rewrite rules change: text produced under two versions is not comparable.
    /// Persisted per run on <c>ingestion_runs.sql_text_sanitizer_version</c>. Coupled to
    /// <see cref="QueryNormalizer.Version"/> — bumping that one changes sanitized text too.
    /// </summary>
    public const int Version = 1;

    /// <summary>Substituted when tokenization failed and the fallback left literals intact.</summary>
    public const string Placeholder = "(unparseable sql text; redacted)";

    /// <summary>
    /// Returns the text to store, the <see cref="NormalizedQuery"/> to store alongside it, and
    /// whether the unsafe-fallback path was taken.
    /// </summary>
    /// <remarks>
    /// <see cref="TokenNormalizer"/>'s fallback only lowercases and collapses whitespace, so a
    /// tokenize failure leaves literals intact in <see cref="NormalizedQuery.NormalizedSql"/> as
    /// well as in the raw text. Protecting only the raw text would move the literal into
    /// <c>normalized_queries</c>, which lives in the same file and joins on the same hash.
    /// <see cref="NormalizedQuery.NormalizedHash"/> is a non-reversible hash and is preserved so
    /// fingerprint joins keep working. Mirrors <c>IngestionService.PrepareProc</c>.
    /// </remarks>
    public static (string Text, NormalizedQuery Normalized, bool Failed) Apply(
        string raw, NormalizedQuery nq, SqlTextSanitization level)
    {
        if (level == SqlTextSanitization.Raw) return (raw, nq, false);
        if (nq.TokenizeFailed) return (Placeholder, nq with { NormalizedSql = Placeholder }, true);

        // sp_executesql's inner statement is itself a string literal: TokenNormalizer.Normalize
        // collapses it along with the trailing parameter values, losing every table/column name.
        // Unwrap it when possible; nq.NormalizedSql (fully collapsed) is always a safe fallback.
        var unwrapped = SpExecuteSqlUnwrapper.TryUnwrap(raw);
        if (unwrapped is not null) return (unwrapped, nq, false);

        return (nq.NormalizedSql, nq, false);
    }
}
