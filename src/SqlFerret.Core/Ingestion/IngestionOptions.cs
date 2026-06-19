// src/SqlFerret.Core/Ingestion/IngestionOptions.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Parameters;

namespace SqlFerret.Core.Ingestion;

public record IngestionOptions(RedactionMode Redaction, IReadOnlyList<FilterRule> Filters, int BatchSize = 5000);
