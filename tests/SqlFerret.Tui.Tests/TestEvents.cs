// tests/SqlFerret.Tui.Tests/TestEvents.cs
// Shared test helpers — single source of truth for FakeEvent across this project.
using SqlFerret.Core.Ingestion;

internal sealed record FakeEvent(
    string Name,
    DateTime Timestamp,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyDictionary<string, object?> Actions) : IXeEventData;
