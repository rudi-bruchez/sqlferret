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
/// removed. Nothing of substance is lost: parameter names and types are already persisted per
/// execution in <c>execution_parameters</c> (<c>name</c>, <c>sql_type_guess</c>) under every
/// redaction policy, so keeping a second, harder-to-validate copy inside <c>sql_text_raw</c> was
/// redundant.
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

        var (normalizedSql, tokenizeFailed) = TokenNormalizer.Normalize(inner);
        // An unparseable inner statement keeps its literals intact under TokenNormalizer's
        // fallback — re-emitting it would leak. Falling back to nq.NormalizedSql (the whole
        // outer statement collapsed to '?') is the safe choice here.
        if (tokenizeFailed) return null;

        return (isUnicode ? "N'" : "'") + normalizedSql.Replace("'", "''") + "'";
    }
}
