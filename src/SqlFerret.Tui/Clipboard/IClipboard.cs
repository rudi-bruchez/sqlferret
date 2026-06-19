// src/SqlFerret.Tui/Clipboard/IClipboard.cs
namespace SqlFerret.Tui.Clipboard;

public record ClipboardResult(bool ToClipboard, string? FilePath, string Description);

public interface IClipboard
{
    ClipboardResult Copy(string text, string suggestedFileBaseName);
}
