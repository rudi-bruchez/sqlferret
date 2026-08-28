// src/SqlFerret.Core/Normalization/SpExecuteSqlUnwrapper.cs
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

/// <summary>
/// Unwraps <c>exec sp_executesql N'&lt;stmt&gt;', N'&lt;params&gt;', ...</c> so the inner
/// statement's identifiers survive <see cref="SqlTextSanitization.Literals"/>. Without this,
/// <see cref="TokenNormalizer"/> treats the inner statement as an ordinary string literal and
/// collapses it to '?' along with every other value — safe, but it destroys every table and
/// column name, which is the dominant shape for parameterized applications.
/// </summary>
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
            // could be mistaken for it. Same requirement for the parameter-declaration argument:
            // it must immediately follow the statement argument's separating comma.
            if (spExecPos + 1 >= content.Count) return null;
            int stmtIdx = content[spExecPos + 1];
            if (!IsStringLiteral(tokens[stmtIdx].TokenType)) return null; // statement in a variable

            int paramsIdx = -1;
            int afterStmtPos = spExecPos + 2;
            if (afterStmtPos < content.Count && tokens[content[afterStmtPos]].TokenType == TSqlTokenType.Comma
                && afterStmtPos + 1 < content.Count
                && IsStringLiteral(tokens[content[afterStmtPos + 1]].TokenType))
            {
                int candidate = content[afterStmtPos + 1];
                // Only pass this slot through verbatim when its content is provably declaration-
                // shaped (a "@name type[, @name type]..." list). Positional guessing alone isn't
                // a guarantee — nothing stops a caller from putting a value in this slot instead.
                if (IsDeclarationShaped(tokens[candidate])) paramsIdx = candidate;
            }

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
                else if (i == paramsIdx)
                    text = t.Text; // verbatim — validated declaration-shaped by IsDeclarationShaped
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

    /// <summary>
    /// True only when <paramref name="token"/>'s unquoted body tokenizes to a plausible
    /// <c>sp_executesql</c> parameter declaration: it opens with a parameter name (a
    /// <c>@name</c> <see cref="TSqlTokenType.Variable"/> token — every real declaration does)
    /// and carries no string-literal or comment token anywhere, since that is where a value
    /// would hide (an Integer token, e.g. <c>nvarchar(50)</c>'s <c>50</c>, is fine and expected).
    /// Anything else — including a bare word like <c>N'SECRET'</c>, which starts with neither
    /// <c>@</c> nor a value token but is not a declaration either — is rejected: false negatives
    /// only cost readability (the caller collapses the slot to <c>?</c>), false positives would
    /// leak a value, so this stays deliberately strict.
    /// </summary>
    private static bool IsDeclarationShaped(TSqlParserToken token)
    {
        try
        {
            bool isUnicode = token.TokenType == TSqlTokenType.UnicodeStringLiteral;
            string raw = token.Text;
            string body = isUnicode ? raw[2..^1] : raw[1..^1];
            string content = body.Replace("''", "'");

            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(content);
            IList<TSqlParserToken> innerTokens = parser.GetTokenStream(reader, out IList<ParseError> errors);
            if (errors.Count > 0) return false;

            bool sawFirstToken = false;
            foreach (var t in innerTokens)
            {
                switch (t.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.EndOfFile:
                        continue;
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                    case TSqlTokenType.AsciiStringLiteral:
                    case TSqlTokenType.UnicodeStringLiteral:
                        return false;
                }

                if (!sawFirstToken)
                {
                    if (t.TokenType != TSqlTokenType.Variable) return false;
                    sawFirstToken = true;
                }
            }
            return sawFirstToken; // reject an empty/whitespace-only declaration too
        }
        catch
        {
            return false;
        }
    }

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
