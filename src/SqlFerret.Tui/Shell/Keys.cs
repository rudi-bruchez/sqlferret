// src/SqlFerret.Tui/Shell/Keys.cs
// Key constants used across TUI views.
// Key.Slash does NOT exist in Terminal.Gui 2.4.6 — use the cast (Key)'/'
using Terminal.Gui.Input;

namespace SqlFerret.Tui.Shell;

static class Keys
{
    public static readonly Key Filter = (Key)'/';
    public static readonly Key Sort = Key.S;
    public static readonly Key Cols = Key.C.WithShift;
    public static readonly Key Copy = Key.C;
    public static readonly Key Back = Key.Esc;
    public static readonly Key Quit = Key.Q;
}
