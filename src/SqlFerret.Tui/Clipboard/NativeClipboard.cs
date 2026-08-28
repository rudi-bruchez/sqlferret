// src/SqlFerret.Tui/Clipboard/NativeClipboard.cs
//
// Adapts Terminal.Gui 2.4.6's instance-based clipboard API.
//
// In TG 2.4.6 the static Clipboard class is [Obsolete]; the correct API is
// IApplication.Clipboard (returns Terminal.Gui.App.IClipboard).  We accept a
// Func<string, bool>? delegate so the caller can pass
//   app.Clipboard.TrySetClipboardData
// without this class having to hold a reference to the full IApplication.
// When the delegate is null (e.g. in tests or when no app is running) we fall
// straight through to the file-fallback.
namespace SqlFerret.Tui.Clipboard;

public class NativeClipboard(IClipboard fallback, Func<string, bool>? trySetSystemClipboard = null) : IClipboard
{
    public ClipboardResult Copy(string text, string suggestedFileBaseName)
    {
        if (trySetSystemClipboard is not null)
        {
            try
            {
                if (trySetSystemClipboard(text))
                    return new ClipboardResult(true, null, "copied to clipboard");
            }
            catch
            {
                // fall through to file-fallback
            }
        }

        return fallback.Copy(text, suggestedFileBaseName);
    }
}
