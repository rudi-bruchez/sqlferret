using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

public static class TokenNormalizer
{
    // internal: reused by SpExecuteSqlUnwrapper's token walk so the two stay in lockstep.
    internal static readonly HashSet<TSqlTokenType> LiteralTokens =
    [
        TSqlTokenType.Integer, TSqlTokenType.Numeric, TSqlTokenType.Money,
        TSqlTokenType.Real, TSqlTokenType.HexLiteral,
        TSqlTokenType.AsciiStringLiteral, TSqlTokenType.UnicodeStringLiteral,
    ];

    // Explicit allow-list of keyword token types exercised by the golden tests.
    // This avoids the fiddly heuristic approach. Identifiers (dbo.Users, [my table])
    // are NOT in this set and keep their original casing.
    internal static readonly HashSet<TSqlTokenType> KeywordTokens =
    [
        TSqlTokenType.Select,
        TSqlTokenType.From,
        TSqlTokenType.Where,
        TSqlTokenType.Exec,
        TSqlTokenType.Execute,
        TSqlTokenType.In,
        TSqlTokenType.And,
        TSqlTokenType.Or,
        TSqlTokenType.Not,
        TSqlTokenType.Join,
        TSqlTokenType.On,
        TSqlTokenType.Order,
        TSqlTokenType.Group,
        TSqlTokenType.By,
        TSqlTokenType.Having,
        TSqlTokenType.Inner,
        TSqlTokenType.Left,
        TSqlTokenType.Right,
        TSqlTokenType.Outer,
        TSqlTokenType.Full,
        TSqlTokenType.Cross,
        TSqlTokenType.Insert,
        TSqlTokenType.Update,
        TSqlTokenType.Delete,
        TSqlTokenType.Set,
        TSqlTokenType.Into,
        TSqlTokenType.Values,
        TSqlTokenType.Top,
        TSqlTokenType.Distinct,
        TSqlTokenType.As,
        TSqlTokenType.Case,
        TSqlTokenType.When,
        TSqlTokenType.Then,
        TSqlTokenType.Else,
        TSqlTokenType.End,
        TSqlTokenType.Null,
        TSqlTokenType.Is,
        TSqlTokenType.Like,
        TSqlTokenType.Between,
        TSqlTokenType.Exists,
        TSqlTokenType.Union,
        TSqlTokenType.All,
    ];

    /// <returns>
    /// <c>normalizedSql</c>: literals collapsed to <c>?</c>, everything else (including a
    /// double-quoted token) verbatim. <c>qiCollapsedSql</c>: the same, except a
    /// <see cref="TSqlTokenType.QuotedIdentifier"/> token also collapses to <c>?</c> — see
    /// <see cref="SqlFerret.Core.Model.NormalizedQuery.QiCollapsedSql"/> for why. Both are built in
    /// the one token pass so producing the second costs no extra parse.
    /// </returns>
    public static (string normalizedSql, bool tokenizeFailed, string qiCollapsedSql) Normalize(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
            return (string.Empty, false, string.Empty);

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);

            // Use Parse() to detect semantically invalid SQL — GetTokenStream() is lenient
            // and returns no errors even for input like "@@@ not sql ((".
            using (var parseReader = new StringReader(rawSql))
            {
                parser.Parse(parseReader, out IList<ParseError> parseErrors);
                if (parseErrors.Count > 0)
                    return (FallbackCollapse(rawSql), true, FallbackCollapse(rawSql));
            }

            using var reader = new StringReader(rawSql);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(reader, out IList<ParseError> errors);
            if (errors.Count > 0)
                return (FallbackCollapse(rawSql), true, FallbackCollapse(rawSql));

            var sb = new StringBuilder();
            var sbQi = new StringBuilder();
            bool lastWasSpace = false;

            foreach (var t in tokens)
            {
                switch (t.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                    case TSqlTokenType.EndOfFile:
                        if (!lastWasSpace) { sb.Append(' '); sbQi.Append(' '); lastWasSpace = true; }
                        continue;
                }

                string text, textQi;
                if (LiteralTokens.Contains(t.TokenType))
                {
                    text = "?";
                    textQi = "?";
                }
                else if (t.TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier)
                {
                    // A double-quoted token ("..."): ScriptDom itself can't disambiguate value
                    // from identifier without knowing the session's QUOTED_IDENTIFIER setting,
                    // which the capture never records — hence this dedicated ambiguous token
                    // type (distinct from QuotedIdentifier, which is bracket-quoted [...] and
                    // unambiguously an identifier — never collapsed).
                    // `normalizedSql` keeps it verbatim (existing behavior, drives the hash);
                    // `qiCollapsedSql` fails safe and collapses it like a literal.
                    text = t.Text;
                    textQi = "?";
                }
                else if (KeywordTokens.Contains(t.TokenType))
                {
                    text = t.Text.ToLowerInvariant();
                    textQi = text;
                }
                else
                {
                    text = t.Text;
                    textQi = t.Text;
                }

                sb.Append(text);
                sbQi.Append(textQi);
                lastWasSpace = false;
            }

            var collapsed = CollapseInList(sb.ToString().Trim());
            var qiCollapsed = CollapseInList(sbQi.ToString().Trim());
            return (collapsed, false, qiCollapsed);
        }
        catch
        {
            return (FallbackCollapse(rawSql), true, FallbackCollapse(rawSql));
        }
    }

    // Collapse "in (?, ?, ?)" → "in (?)"  (case-insensitive, whitespace-tolerant)
    private static string CollapseInList(string sql) =>
        Regex.Replace(sql, @"(?i)\bin\s*\(\s*\?(?:\s*,\s*\?)+\s*\)", "in (?)");

    private static string FallbackCollapse(string raw) =>
        Regex.Replace(raw, @"\s+", " ").Trim().ToLowerInvariant();
}
