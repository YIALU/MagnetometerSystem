using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Tests;

public class WorkspaceLayoutTests
{
    [Fact]
    public void FocusRestoresOnlyPreviouslyExpandedPanelsAndPersistsNormalLayout()
    {
        var layout = new WorkspaceLayoutViewModel { ConnectionExpanded = true, AnalysisExpanded = true };
        layout.ToggleFocusCommand.Execute(null);
        Assert.True(layout.IsFocused);
        Assert.False(layout.ConnectionExpanded);
        Assert.False(layout.AnalysisExpanded);
        Assert.True(layout.GetPersistedPanels()["analysis"]);
        layout.ToggleFocusCommand.Execute(null);
        Assert.False(layout.IsFocused);
        Assert.True(layout.ConnectionExpanded);
        Assert.True(layout.AnalysisExpanded);
        Assert.False(layout.TerminalExpanded);
    }

    [Fact]
    public void OpeningOnePanelExitsFocusWithoutOpeningOthers()
    {
        var layout = new WorkspaceLayoutViewModel { AnalysisExpanded = true };
        layout.ToggleFocusCommand.Execute(null);
        layout.TerminalExpanded = true;
        Assert.False(layout.IsFocused);
        Assert.False(layout.AnalysisExpanded);
        Assert.True(layout.GetPersistedPanels()["terminal"]);
    }
}
