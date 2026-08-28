using SqlFerret.Core.Model;

namespace SqlFerret.Core.Normalization;

public static class QueryNormalizer
{
    public const int Version = 1;

    public static NormalizedQuery Normalize(string rawSql)
    {
        var (normalized, failed) = TokenNormalizer.Normalize(rawSql);
        var (kind, table) = AstClassifier.Classify(rawSql);
        var hash = Fingerprint.Hash(normalized);
        return new NormalizedQuery(normalized, hash, kind, table, failed);
    }
}
