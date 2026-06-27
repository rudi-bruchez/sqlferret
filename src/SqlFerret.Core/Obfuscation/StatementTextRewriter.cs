// src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFerret.Core.Obfuscation;

public static class StatementTextRewriter
{
    private static readonly HashSet<TSqlTokenType> Literals =
    [
        TSqlTokenType.Integer, TSqlTokenType.Numeric, TSqlTokenType.Money,
        TSqlTokenType.Real, TSqlTokenType.HexLiteral,
        TSqlTokenType.AsciiStringLiteral, TSqlTokenType.UnicodeStringLiteral,
    ];

    public static string Rewrite(string sqlFragment, ObfuscationMap map)
    {
        if (string.IsNullOrWhiteSpace(sqlFragment)) return sqlFragment ?? string.Empty;
        var lookup = map.BuildTextLookup();
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            // Use Parse() to detect semantically invalid SQL — GetTokenStream() is lenient
            // and returns no errors even for input like "@@@ not sql ((".
            using (var pr = new StringReader(sqlFragment))
            {
                parser.Parse(pr, out IList<ParseError> perr);
                if (perr.Count > 0) return Fallback(sqlFragment, lookup);
            }
            using var r = new StringReader(sqlFragment);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(r, out IList<ParseError> err);
            if (err.Count > 0) return Fallback(sqlFragment, lookup);

            var sb = new StringBuilder();
            foreach (var t in tokens)
            {
                switch (t.TokenType)
                {
                    case TSqlTokenType.EndOfFile:
                        continue;
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                        sb.Append(' '); // drop comment bodies (may carry literals)
                        continue;
                }
                if (Literals.Contains(t.TokenType)) { sb.Append('?'); continue; }
                if (t.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
                {
                    var key = ObfuscationMap.Strip(t.Text).ToLowerInvariant();
                    if (lookup.TryGetValue(key, out var tok))
                    {
                        sb.Append(t.TokenType == TSqlTokenType.QuotedIdentifier ? "[" + tok + "]" : tok);
                        continue;
                    }
                }
                sb.Append(t.Text);
            }
            return sb.ToString();
        }
        catch
        {
            return Fallback(sqlFragment, lookup);
        }
    }

    // Safety net: never let an original name or literal escape, even if parsing failed.
    private static string Fallback(string raw, IReadOnlyDictionary<string, string> lookup)
    {
        var s = Regex.Replace(raw, @"'(?:[^']|'')*'", "?");                  // string literals
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);     // block comments
        s = Regex.Replace(s, @"--[^\n]*", " ");                              // line comments
        s = Regex.Replace(s, @"(?<![A-Za-z_@#$0-9.])\d+(\.\d+)?", "?");       // numeric literals
        foreach (var kv in lookup.OrderByDescending(kv => kv.Key.Length))     // longest-first to avoid substrings
        {
            // Keys that start with a non-word char (# for temp tables, @ for parameters, $ for rare forms)
            // need identifier-aware lookarounds: \b does not fire before a non-word char so bare
            // '#Foo' or '@Bar' would never be matched. Word-char-starting keys keep the original \b
            // approach so that e.g. the inner name "Foo" (stored without its leading #) is still
            // matched inside "#Foo" in DDL text (word boundary exists between '#' and 'F').
            bool startsNonWord = kv.Key.Length > 0 && kv.Key[0] is '#' or '@' or '$';
            var pat = startsNonWord
                ? @"(?i)(?<![A-Za-z0-9_@#$])(\[?)" + Regex.Escape(kv.Key) + @"\]?(?![A-Za-z0-9_@#$])"
                : @"(?i)(\[?)\b" + Regex.Escape(kv.Key) + @"\b\]?";
            s = Regex.Replace(s, pat,
                m => m.Groups[1].Length > 0 ? "[" + kv.Value + "]" : kv.Value);
        }
        return s;
    }
}
