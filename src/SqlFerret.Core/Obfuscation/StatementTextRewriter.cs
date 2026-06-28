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
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            // Use Parse() to detect semantically invalid SQL — GetTokenStream() is lenient
            // and returns no errors even for input like "@@@ not sql ((".
            using (var pr = new StringReader(sqlFragment))
            {
                parser.Parse(pr, out IList<ParseError> perr);
                if (perr.Count > 0) return Fallback(sqlFragment, map);
            }
            using var r = new StringReader(sqlFragment);
            IList<TSqlParserToken> tokens = parser.GetTokenStream(r, out IList<ParseError> err);
            if (err.Count > 0) return Fallback(sqlFragment, map);

            var lookup = map.BuildTextLookup();
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
                    case TSqlTokenType.Variable:
                        // @@system globals (@@TRANCOUNT, @@ERROR, …) are preserved verbatim.
                        // Single-@ user variables (@foo) and local DECLARE'd variables are mapped.
                        if (t.Text.StartsWith("@@", StringComparison.Ordinal))
                            sb.Append(t.Text);
                        else
                            sb.Append(map.Token(NameKind.Parameter, t.Text));
                        continue;
                }
                if (Literals.Contains(t.TokenType)) { sb.Append('?'); continue; }
                if (t.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
                {
                    var stripped = ObfuscationMap.Strip(t.Text);
                    // On-the-fly temp table mapping: #/## names may appear only in SQL text
                    // (DDL-only path) without a prior operator-tree registration.
                    if (stripped.StartsWith('#'))
                    {
                        var tok = map.Token(NameKind.TempTable, ObfuscationMap.NormalizeTempName(stripped));
                        sb.Append(t.TokenType == TSqlTokenType.QuotedIdentifier ? "[" + tok + "]" : tok);
                        continue;
                    }
                    var key = stripped.ToLowerInvariant();
                    if (lookup.TryGetValue(key, out var tok2))
                    {
                        sb.Append(t.TokenType == TSqlTokenType.QuotedIdentifier ? "[" + tok2 + "]" : tok2);
                        continue;
                    }
                }
                sb.Append(t.Text);
            }
            return sb.ToString();
        }
        catch
        {
            return Fallback(sqlFragment, map);
        }
    }

    // Safety net: never let an original name or literal escape, even if parsing failed.
    // Pre-scans the fragment to register any user @variables and #temp names not already in the map,
    // then rebuilds the lookup so the replacement loop covers them.
    private static string Fallback(string raw, ObfuscationMap map)
    {
        // Strip strings + comments before scanning so '@literal' or '-- @x' don't produce false entries.
        var scanText = Regex.Replace(raw, @"'(?:[^']|'')*'", " ");
        scanText = Regex.Replace(scanText, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        scanText = Regex.Replace(scanText, @"--[^\n]*", " ");
        // Single-@ user variables: (?<!@) ensures we skip the second '@' of @@system globals.
        foreach (Match m in Regex.Matches(scanText, @"(?<!@)@(?!@)[A-Za-z_][A-Za-z0-9_$#]*"))
            map.Token(NameKind.Parameter, m.Value);
        // Temp table names (local # or global ##).
        foreach (Match m in Regex.Matches(scanText, @"#{1,2}[A-Za-z_][A-Za-z0-9_$#]*"))
            map.Token(NameKind.TempTable, ObfuscationMap.NormalizeTempName(m.Value));

        var lookup = map.BuildTextLookup();
        var s = Regex.Replace(raw, @"'(?:[^']|'')*'", "?");                  // string literals
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);     // block comments
        s = Regex.Replace(s, @"--[^\n]*", " ");                              // line comments
        // Hex literals (0xDEADBEEF) BEFORE the numeric scrub, which would otherwise consume only the
        // leading '0' and leave the hex payload intact (review fix #3).
        s = Regex.Replace(s, @"(?<![A-Za-z_@#$0-9.])0[xX][0-9A-Fa-f]+", "?"); // hex literals
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
