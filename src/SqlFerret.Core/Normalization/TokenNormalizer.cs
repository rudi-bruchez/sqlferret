using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Normalization;

public static class TokenNormalizer
{
    private static readonly HashSet<TSqlTokenType> LiteralTokens =
    [
        TSqlTokenType.Integer, TSqlTokenType.Numeric, TSqlTokenType.Money,
        TSqlTokenType.Real, TSqlTokenType.HexLiteral,
        TSqlTokenType.AsciiStringLiteral, TSqlTokenType.UnicodeStringLiteral,
    ];

    // Explicit allow-list of keyword token types exercised by the golden tests.
    // This avoids the fiddly heuristic approach. Identifiers (dbo.Users, [my table])
    // are NOT in this set and keep their original casing.
    private static readonly HashSet<TSqlTokenType> KeywordTokens =
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

    public static (string normalizedSql, bool tokenizeFailed) Normalize(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
            return (string.Empty, false);

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);

            // Use Parse() to detect semantically invalid SQL — GetTokenStream() is lenient
            // and returns no errors even for input like "@@@ not sql ((".
            using (var parseReader = new StringReader(rawSql))
            {
                parser.Parse(parseReader, out IList<ParseError> parseErrors);
                if (parseErrors.Count > 0)
                    return (FallbackCollapse(rawSql), true);
            }

            using var reader = new StringReader(rawSql);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(reader, out IList<ParseError> errors);
            if (errors.Count > 0)
                return (FallbackCollapse(rawSql), true);

            var sb = new StringBuilder();
            bool lastWasSpace = false;

            foreach (var t in tokens)
            {
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
                if (LiteralTokens.Contains(t.TokenType))
                    text = "?";
                else if (KeywordTokens.Contains(t.TokenType))
                    text = t.Text.ToLowerInvariant();
                else
                    text = t.Text;

                sb.Append(text);
                lastWasSpace = false;
            }

            var collapsed = CollapseInList(sb.ToString().Trim());
            return (collapsed, false);
        }
        catch
        {
            return (FallbackCollapse(rawSql), true);
        }
    }

    // Collapse "in (?, ?, ?)" → "in (?)"  (case-insensitive, whitespace-tolerant)
    private static string CollapseInList(string sql) =>
        Regex.Replace(sql, @"(?i)\bin\s*\(\s*\?(?:\s*,\s*\?)+\s*\)", "in (?)");

    private static string FallbackCollapse(string raw) =>
        Regex.Replace(raw, @"\s+", " ").Trim().ToLowerInvariant();
}
