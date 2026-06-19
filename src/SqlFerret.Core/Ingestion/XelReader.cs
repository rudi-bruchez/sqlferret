// src/SqlFerret.Core/Ingestion/XelReader.cs
using Microsoft.SqlServer.XEvent.XELite;

namespace SqlFerret.Core.Ingestion;

public class XelReader
{
    /// <summary>
    /// Streams events from one or more .xel files in order.
    /// Returns a lazy sequence of (adapter, fileName, perFileOrdinal) tuples.
    /// offset is a 0-based per-file event ordinal (XELite does not expose byte offsets;
    /// this is a documented surrogate for resume/dedup).
    /// </summary>
    public IEnumerable<(IXeEventData ev, string fileName, long offset)> Read(
        IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            long ordinal = 0;
            var collected = new List<IXeEventData>();

            var streamer = new XEFileEventStreamer(file);

            // XELite is push/async; ReadEventStream takes:
            //   Func<Task>         headerReadCallback  (called once when header is parsed)
            //   Func<IXEvent,Task> eventCallback       (called per event)
            //   CancellationToken
            streamer.ReadEventStream(
                () => Task.CompletedTask,
                xevent =>
                {
                    collected.Add(new XeEventDataAdapter(xevent));
                    return Task.CompletedTask;
                },
                CancellationToken.None).GetAwaiter().GetResult();

            foreach (var ev in collected)
                yield return (ev, name, ordinal++);
        }
    }

    private sealed class XeEventDataAdapter : IXeEventData
    {
        private readonly IXEvent _e;

        public XeEventDataAdapter(IXEvent e) => _e = e;

        public string Name => _e.Name;

        // IXEvent.Timestamp is a DateTimeOffset; expose as UTC DateTime per IXeEventData.
        public DateTime Timestamp => _e.Timestamp.UtcDateTime;

        public IReadOnlyDictionary<string, object?> Fields =>
            _e.Fields.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        public IReadOnlyDictionary<string, object?> Actions =>
            _e.Actions.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
    }
}
