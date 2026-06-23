// src/SqlFerret.Core/Ingestion/BlockingReportParser.cs
using System.Globalization;
using System.Xml.Linq;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

public static class BlockingReportParser
{
    public static BlockingReport? Parse(string reportXml, DateTime capturedAt)
    {
        if (string.IsNullOrWhiteSpace(reportXml)) return null;
        XElement root;
        try { root = XElement.Parse(reportXml); }
        catch { return null; }   // malformed → caller counts a parse failure

        var blocked = ParseProcess(root.Element("blocked-process")?.Element("process"));
        var blocking = ParseProcess(root.Element("blocking-process")?.Element("process"));
        if (blocked is null || blocking is null) return null;

        int? loop = IntAttr(root, "monitorLoop");
        return new BlockingReport(capturedAt, loop, blocked.DatabaseHint, blocked.Process, blocking.Process);
    }

    private sealed record Parsed(BlockingProcess Process, int? DatabaseHint);

    private static Parsed? ParseProcess(XElement? p)
    {
        if (p is null) return null;
        string? waitRaw = (string?)p.Attribute("waitresource");
        var wr = WaitResourceParser.Parse(waitRaw);
        long? waitUs = IntAttr(p, "waittime") is int ms ? ms * 1000L : null;
        var proc = new BlockingProcess(
            Spid: IntAttr(p, "spid"), Ecid: IntAttr(p, "ecid"), Status: (string?)p.Attribute("status"),
            WaitResourceRaw: waitRaw, WaitResourceType: wr.Type, ObjectId: wr.ObjectId, HobtId: wr.HobtId,
            WaitTimeUs: waitUs, LockMode: (string?)p.Attribute("lockMode"),
            IsolationLevel: (string?)p.Attribute("isolationlevel"), TranCount: IntAttr(p, "trancount"),
            ClientApp: (string?)p.Attribute("clientapp"), HostName: (string?)p.Attribute("hostname"),
            LoginName: (string?)p.Attribute("loginname"),
            InputBufRaw: p.Element("inputbuf")?.Value.Trim(), InputBufFingerprint: null);
        return new Parsed(proc, wr.DatabaseId);
    }

    private static int? IntAttr(XElement e, string name) =>
        int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public static class DeadlockReportParser
{
    public static DeadlockReport? Parse(string deadlockXml, DateTime capturedAt)
    {
        if (string.IsNullOrWhiteSpace(deadlockXml)) return null;
        XElement root;
        try { root = XElement.Parse(deadlockXml); }
        catch { return null; }

        var victims = root.Descendants("victim-list").Elements("victimProcess")
            .Select(v => Spid((string?)v.Attribute("id"))).OfType<int>().ToList();
        var participants = root.Descendants("process")
            .Select(pr => ParseInt((string?)pr.Attribute("spid"))).OfType<int>().Distinct().ToList();
        if (participants.Count == 0) return null;
        return new DeadlockReport(capturedAt, victims, participants, deadlockXml);
    }

    // victim id is a process token, not a spid; participants carry the real spid. Victim spids are
    // resolved by matching ids in a full implementation; for the light version we keep participant spids
    // and an empty victim list when ids don't resolve to spids.
    private static int? Spid(string? _) => null;
    private static int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;
}
