// src/SqlFerret.Core/Storage/PreparedRow.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Storage;

public record PreparedParameter(
    int Ordinal, string? Name, string SourceKind, string? TypeGuess,
    string Value, bool Redacted, bool Truncated, double Confidence);

public record PreparedRow(
    ExecutionEvent Event,
    NormalizedQuery Normalized,
    IReadOnlyList<PreparedParameter> Parameters);
