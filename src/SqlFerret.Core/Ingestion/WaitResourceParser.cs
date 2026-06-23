// src/SqlFerret.Core/Ingestion/WaitResourceParser.cs
using System.Globalization;
using SqlFerret.Core.Model;

namespace SqlFerret.Core.Ingestion;

public record WaitResourceInfo(WaitResourceType Type, int? DatabaseId, long? ObjectId, long? HobtId);

/// <summary>Classifies a blocked-process waitresource string. The resource TYPE is the locality signal:
/// OBJECT/KEY/PAGE/RID = a user object (potentially tenant-local); DATABASE/PAGELATCH/APPLICATION = shared.</summary>
public static class WaitResourceParser
{
    public static WaitResourceInfo Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(WaitResourceType.Other, null, null, null);
        var s = raw.Trim();
        int colon = s.IndexOf(':');
        string head = (colon < 0 ? s : s[..colon]).Trim().ToUpperInvariant();
        string rest = colon < 0 ? "" : s[(colon + 1)..].Trim();

        var type = head switch
        {
            "OBJECT" => WaitResourceType.Object,
            "KEY" => WaitResourceType.Key,
            "PAGE" => WaitResourceType.Page,
            "RID" => WaitResourceType.Rid,
            "DATABASE" => WaitResourceType.Database,
            "APPLICATION" => WaitResourceType.AppLock,
            _ => head.StartsWith("PAGELATCH") || head.StartsWith("PAGEIOLATCH") ? WaitResourceType.PageLatch
               : head.StartsWith("APPLICATION") ? WaitResourceType.AppLock
               : WaitResourceType.Other
        };

        // tokens after the head are colon-separated numerics: db[:object|hobt[:index]]
        var parts = rest.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries);
        int? db = parts.Length > 0 && int.TryParse(parts[0], out var d) ? d : null;
        long? objId = type == WaitResourceType.Object && parts.Length > 1 && long.TryParse(parts[1], out var o) ? o : null;
        long? hobt = type == WaitResourceType.Key && parts.Length > 1 && long.TryParse(parts[1], out var h) ? h : null;
        return new(type, db, objId, hobt);
    }
}
