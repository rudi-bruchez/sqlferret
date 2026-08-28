namespace SqlFerret.Core.Ingestion;

public interface IXeEventData
{
    string Name { get; }
    DateTime Timestamp { get; }
    IReadOnlyDictionary<string, object?> Fields { get; }
    IReadOnlyDictionary<string, object?> Actions { get; }
}
