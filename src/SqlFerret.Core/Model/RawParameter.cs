namespace SqlFerret.Core.Model;
public record RawParameter(
    int Ordinal, string? Name, ParameterSourceKind SourceKind,
    string? SqlTypeGuess, string ValueText, double ParseConfidence);
