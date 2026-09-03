// src/SqlFerret.Core/Normalization/SpExecuteSqlUnwrapper.cs
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

/// <summary>
/// Unwraps <c>exec sp_executesql N'&lt;stmt&gt;', ...</c> so the inner statement's identifiers
/// survive <see cref="SqlTextSanitization.Literals"/>. Without this, <see cref="TokenNormalizer"/>
/// treats the inner statement as an ordinary string literal and collapses it to '?' along with
/// every other value — safe, but it destroys every table and column name, which is the dominant
/// shape for parameterized applications.
/// </summary>
/// <remarks>
/// The statement argument is the <em>only</em> thing this class re-emits specially. Every other
/// argument — including the parameter-declaration literal (<c>N'@e nvarchar(50)'</c>) — collapses
/// to <c>?</c> exactly like any other literal in the outer call. An earlier revision kept that
/// declaration verbatim on the theory that it only ever carries parameter names and types, never
/// a value; three rounds of adversarial review each found a new shape that slipped a value through
/// it anyway (a value assigned inside the declaration, a comment, a bracketed identifier, a
/// numeric default). Rather than extend that predicate a fourth time, the pass-through was
/// removed — reason enough on its own, since it repeatedly leaked values regardless of any
/// downstream persistence. Parameter names and types are also not lost: they are persisted per
/// execution in <c>execution_parameters</c> (<c>name</c>, <c>sql_type_guess</c>) under every
/// redaction policy EXCEPT <c>off</c>, which stores no parameter rows at all
/// (<c>IngestionService.RedactParams</c> skips them entirely), so keeping a second,
/// harder-to-validate copy inside <c>sql_text_raw</c> was redundant under every policy that keeps
/// parameter rows in the first place.
/// </remarks>
internal static class SpExecuteSqlUnwrapper
{
    /// <summary>
    /// Returns the rewritten text, or null when the input is not an unwrappable
    /// <c>sp_executesql</c> call. Callers must fall back to <c>nq.NormalizedSql</c> in that case
    /// — it is never less safe than a rewrite this method declines to produce.
    /// </summary>
    public static string? TryUnwrap(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql)) return null;

        // Cheap pre-check: every plain sql_batch_completed event pays for this call at
        // `Literals`, and the identifier text must appear literally for the match below to ever
        // succeed — so skip the two ScriptDom passes entirely when it can't possibly be present.
        if (rawSql.IndexOf("sp_executesql", StringComparison.OrdinalIgnoreCase) < 0) return null;

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);

            // Same failure-detection shape as TokenNormalizer.Normalize: Parse() catches
            // semantically invalid SQL that the lenient GetTokenStream() would let through.
            using (var parseReader = new StringReader(rawSql))
            {
                parser.Parse(parseReader, out IList<ParseError> parseErrors);
                if (parseErrors.Count > 0) return null;
            }

            using var reader = new StringReader(rawSql);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(reader, out IList<ParseError> errors);
            if (errors.Count > 0) return null;

            // Content tokens only: whitespace/comments carry no positional meaning for the
            // "immediately follows" checks below.
            var content = new List<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                switch (tokens[i].TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                    case TSqlTokenType.EndOfFile:
                        continue;
                }
                content.Add(i);
            }

            int spExecPos = content.FindIndex(i =>
                tokens[i].TokenType == TSqlTokenType.Identifier &&
                string.Equals(tokens[i].Text, "sp_executesql", StringComparison.OrdinalIgnoreCase));
            if (spExecPos < 0) return null; // not an sp_executesql invocation

            // The statement argument must be the token immediately following the identifier —
            // otherwise a value literal belonging to a later, unrelated argument (e.g. @e='x')
            // could be mistaken for it.
            if (spExecPos + 1 >= content.Count) return null;
            int stmtIdx = content[spExecPos + 1];
            if (!IsStringLiteral(tokens[stmtIdx].TokenType)) return null; // statement in a variable

            string? innerRewritten = RewriteInnerStatement(tokens[stmtIdx]);
            if (innerRewritten is null) return null; // inner statement failed to tokenize

            var sb = new StringBuilder();
            bool lastWasSpace = false;

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                switch (t.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                    case TSqlTokenType.EndOfFile:
                        if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                        continue;
                }

                string text;
                if (i == stmtIdx)
                    text = innerRewritten;
                else if (TokenNormalizer.LiteralTokens.Contains(t.TokenType))
                    text = "?";
                else if (t.TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier)
                    // A double-quoted token: ambiguous under SET QUOTED_IDENTIFIER OFF, could be
                    // a value. Fail safe, same as TokenNormalizer's qiCollapsedSql (see
                    // NormalizedQuery.QiCollapsedSql). Distinct from QuotedIdentifier ([...]),
                    // which is unambiguously an identifier and stays verbatim below.
                    text = "?";
                else if (TokenNormalizer.KeywordTokens.Contains(t.TokenType))
                    text = t.Text.ToLowerInvariant();
                else
                    text = t.Text;

                sb.Append(text);
                lastWasSpace = false;
            }

            return sb.ToString().Trim();
        }
        catch
        {
            // Deliberate fallback: any parser failure here must degrade to the caller's safe,
            // fully-collapsed form (TokenNormalizer's own output), never propagate. Losing the
            // unwrap loses readability, not privacy — no value survives either way.
            return null;
        }
    }

    private static bool IsStringLiteral(TSqlTokenType t) =>
        t is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral;

    private static string? RewriteInnerStatement(TSqlParserToken stmtToken)
    {
        bool isUnicode = stmtToken.TokenType == TSqlTokenType.UnicodeStringLiteral;
        string raw = stmtToken.Text;
        // Strip the surrounding quotes (and leading N for a unicode literal), then unescape
        // the doubled '' the outer literal used to represent an embedded quote.
        string body = isUnicode ? raw[2..^1] : raw[1..^1];
        string inner = body.Replace("''", "'");

        var (_, tokenizeFailed, qiCollapsedSql) = TokenNormalizer.Normalize(inner);
        // An unparseable inner statement keeps its literals intact under TokenNormalizer's
        // fallback — re-emitting it would leak. Falling back to nq.NormalizedSql (the whole
        // outer statement collapsed to '?') is the safe choice here.
        if (tokenizeFailed) return null;

        // qiCollapsedSql, not normalizedSql: a double-quoted token inside the inner statement
        // (SET QUOTED_IDENTIFIER OFF) must fail safe here too — see NormalizedQuery.QiCollapsedSql.
        return (isUnicode ? "N'" : "'") + qiCollapsedSql.Replace("'", "''") + "'";
    }
}
