// tests/SqlFerret.Tui.Tests/SmokePlaceholderTests.cs
using Xunit;
public class SmokePlaceholderTests
{
    [Fact] public void Tui_project_is_wired() => Assert.True(typeof(Terminal.Gui.Views.Window) is not null);
}
