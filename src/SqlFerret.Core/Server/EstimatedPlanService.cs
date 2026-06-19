using System.Text;
using Microsoft.Data.SqlClient;
using SqlFerret.Core.Model;
using SqlFerret.Core.Replay;

namespace SqlFerret.Core.Server;

/// <summary>
/// Captures an ESTIMATED (compile-only) execution plan via SET SHOWPLAN_XML ON
/// and persists it as a .sqlplan file openable in SSMS / Plan Explorer.
/// SHOWPLAN_XML is compile-only: the statement is never executed against the target server.
/// </summary>
public class EstimatedPlanService(string connectionString, string plansFolder)
{
    /// <summary>
    /// Pure/offline: writes <paramref name="showplanXml"/> to
    /// <c>&lt;plansFolder&gt;/&lt;planId&gt;.sqlplan</c>, creating the folder if needed.
    /// </summary>
    /// <returns>Absolute path to the written file.</returns>
    public string Save(string planId, string showplanXml)
    {
        if (string.IsNullOrEmpty(planId)
            || planId.Contains('/')
            || planId.Contains('\\')
            || planId.Contains("..")
            || Path.GetFileName(planId) != planId)
            throw new ArgumentException("Invalid planId: must be a bare file name component", nameof(planId));

        Directory.CreateDirectory(plansFolder);
        var path = Path.Combine(plansFolder, $"{planId}.sqlplan");
        File.WriteAllText(path, showplanXml);
        return path;
    }

    /// <summary>
    /// Integration: builds the T-SQL batch from <paramref name="ev"/> via
    /// <see cref="ReplayBuilder"/>, opens a <see cref="SqlConnection"/>,
    /// optionally switches database, enables SET SHOWPLAN_XML ON (compile-only —
    /// the statement produces a plan without being executed), reads the XML result,
    /// then delegates to <see cref="Save"/> to persist the .sqlplan file.
    /// </summary>
    public async Task<string> CaptureAsync(ExecutionEvent ev, string planId, CancellationToken ct = default)
    {
        ReplayScript script = ReplayBuilder.Build(ev);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        if (!string.IsNullOrWhiteSpace(ev.DatabaseName))
        {
            await using var use = conn.CreateCommand();
            use.CommandText = $"USE [{ev.DatabaseName!.Replace("]", "]]")}];";
            await use.ExecuteNonQueryAsync(ct);
        }

        // SET SHOWPLAN_XML ON makes SQL Server return the estimated plan XML
        // instead of executing the statement — compile-only, no data mutation.
        await using (var on = conn.CreateCommand())
        {
            on.CommandText = "SET SHOWPLAN_XML ON;";
            await on.ExecuteNonQueryAsync(ct);
        }

        var xml = new StringBuilder();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = script.Sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                xml.Append(reader.GetString(0));
        }

        var xmlString = xml.ToString();
        if (string.IsNullOrWhiteSpace(xmlString))
            throw new InvalidOperationException("No estimated plan returned (empty SHOWPLAN_XML result)");

        return Save(planId, xmlString);
    }
}
