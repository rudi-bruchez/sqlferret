// src/SqlFerret.Core/Storage/PreparedBlocking.cs
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Storage;

public record PreparedBlockingProcess(BlockingProcess Process, NormalizedQuery? Normalized, string? StoredInputBuf);
public record PreparedBlockingReport(BlockingReport Report, PreparedBlockingProcess Blocked, PreparedBlockingProcess Blocking, string? RawXml = null);
