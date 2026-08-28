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
        IReadOnlyList<string> files,
        Action<string, long>? onRead = null,
        Action<string, long>? onFileComplete = null)
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
                    onRead?.Invoke(name, collected.Count);   // live read-phase progress
                    return Task.CompletedTask;
                },
                CancellationToken.None).GetAwaiter().GetResult();

            onFileComplete?.Invoke(name, collected.Count);   // exact count for calibration

            foreach (var ev in collected)
                yield return (ev, name, ordinal++);
        }
    }

    private sealed class XeEventDataAdapter : IXeEventData
    {
        private readonly IXEvent _e;
        private readonly IReadOnlyDictionary<string, object?> _fields;
        private readonly IReadOnlyDictionary<string, object?> _actions;

        public XeEventDataAdapter(IXEvent e)
        {
            _e = e;
            _fields = e.Fields.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
            _actions = e.Actions.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
        }

        public string Name => _e.Name;

        // IXEvent.Timestamp is a DateTimeOffset; expose as UTC DateTime per IXeEventData.
        public DateTime Timestamp => _e.Timestamp.UtcDateTime;

        public IReadOnlyDictionary<string, object?> Fields => _fields;

        public IReadOnlyDictionary<string, object?> Actions => _actions;
    }
}
