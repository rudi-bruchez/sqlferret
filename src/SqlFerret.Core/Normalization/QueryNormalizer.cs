using SqlFerret.Core.Model;

namespace SqlFerret.Core.Normalization;

public static class QueryNormalizer
{
    /// <summary>
    /// Version de la normalisation <b>et</b> de la classification : les deux sortent d'ici, et
    /// c'est elle que <c>HasStaleClassification</c> compare pour proposer <c>reclassify</c>.
    /// A incrementer des qu'AstClassifier rend une valeur differente sur une entree donnee, sans
    /// quoi un projet deja importe garderait silencieusement l'ancienne classification.
    /// <para>v3 : visiteurs de classe de base pour les modules (les formes <c>ALTER PROCEDURE</c>,
    /// <c>ALTER VIEW</c>... etaient classees d'apres la premiere instruction de leur corps),
    /// visiteurs <c>DROP PROCEDURE/FUNCTION/TRIGGER/VIEW</c>, <c>ALTER TABLE</c>
    /// <c>REBUILD/SWITCH/ENABLE TRIGGER</c> et filet generique, <c>MERGE</c>, <c>FETCH</c>, et
    /// syntaxe heritee <c>DROP INDEX table.index</c>.</para>
    /// </summary>
    public const int Version = 3;

    public static NormalizedQuery Normalize(string rawSql)
    {
        var (normalized, failed) = TokenNormalizer.Normalize(rawSql);
        var cls = AstClassifier.Classify(rawSql);
        var hash = Fingerprint.Hash(normalized);
        return new NormalizedQuery(normalized, hash, cls.Kind, cls.PrimaryTable, cls.TargetObject, failed);
    }
}
