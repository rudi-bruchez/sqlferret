// src/SqlFerret.Tui/Clipboard/FileFallbackClipboard.cs
namespace SqlFerret.Tui.Clipboard;

public class FileFallbackClipboard(string folder) : IClipboard
{
    public ClipboardResult Copy(string text, string suggestedFileBaseName)
    {
        Directory.CreateDirectory(folder);
        var safe = string.Concat(suggestedFileBaseName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0) safe = "script";
        var path = Path.Combine(folder, $"{safe}.sql");
        File.WriteAllText(path, text);
        return new ClipboardResult(false, path, $"wrote {path}");
    }
}
