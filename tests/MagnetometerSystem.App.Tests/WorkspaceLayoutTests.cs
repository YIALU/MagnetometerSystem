using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Tests;

public class WorkspaceLayoutTests
{
    [Fact]
    public void FocusHidesPanelsAndRestoresOnlyPreviouslyOpenOnes()
    {
        var layout = new WorkspaceLayoutViewModel { SidePanelOpen = true, DockOpen = false };
        layout.ToggleFocusCommand.Execute(null);
        Assert.True(layout.IsFocused);
        Assert.False(layout.SidePanelOpen);
        Assert.False(layout.DockOpen);
        // 专注期间保存设置时，记录的是专注前的布局。
        Assert.True(layout.GetPersistedPanels()["sidePanel"]);
        Assert.False(layout.GetPersistedPanels()["dock"]);
        layout.ToggleFocusCommand.Execute(null);
        Assert.False(layout.IsFocused);
        Assert.True(layout.SidePanelOpen);
        Assert.False(layout.DockOpen);
    }

    [Fact]
    public void OpeningOnePanelExitsFocusWithoutOpeningOthers()
    {
        var layout = new WorkspaceLayoutViewModel();
        layout.ToggleFocusCommand.Execute(null);
        layout.ShowDock(WorkspaceLayoutViewModel.DockRawFrames);
        Assert.False(layout.IsFocused);
        Assert.True(layout.DockOpen);
        Assert.False(layout.SidePanelOpen);
        Assert.Equal(WorkspaceLayoutViewModel.DockRawFrames, layout.DockTab);
        Assert.True(layout.GetPersistedPanels()["dock"]);
    }

    [Fact]
    public void LegacyPanelKeysKeepNewPanelsOpen()
    {
        var layout = new WorkspaceLayoutViewModel { SidePanelOpen = false, DockOpen = false };
        layout.Restore(new Dictionary<string, bool> { ["connection"] = true, ["terminal"] = false });
        Assert.True(layout.SidePanelOpen);
        Assert.True(layout.DockOpen);
        layout.Restore(new Dictionary<string, bool> { ["sidePanel"] = false, ["dock"] = true });
        Assert.False(layout.SidePanelOpen);
        Assert.True(layout.DockOpen);
    }
}
