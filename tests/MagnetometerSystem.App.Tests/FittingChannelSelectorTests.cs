using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Tests;

/// <summary>拟合通道选择：按来源建立选项并按名称建议；自动建议只发 Changed，用户修改和“自动识别”还发 Edited（父视图模型据此重新读取会话）。</summary>
public class FittingChannelSelectorTests
{
    private FittingChannelSource _source = new("", [], [], "");
    private int _groups = 1;
    private readonly List<string> _events = new();

    private FittingChannelSelectorViewModel Create()
    {
        var vm = new FittingChannelSelectorViewModel(() => _source, () => _groups);
        vm.Changed += () => _events.Add("Changed");
        vm.Edited += () => _events.Add("Edited");
        return vm;
    }

    [Fact]
    public void RefreshListsTheSourceChannelsAndSuggestsWithoutReportingAnEdit()
    {
        _source = new("live", ["Temp", "Bx", "", "By", "Bz"], ["°C", "nT", "nT", "nT", "nT"], "");
        var vm = Create();
        vm.Refresh();

        Assert.Equal("live", vm.OptionsSourceKey);
        Assert.Equal(new[] { "Temp (°C)", "Bx (nT)", "通道 2 (nT)", "By (nT)", "Bz (nT)" }, vm.Options.Select(o => o.Label));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, vm.Options.Select(o => o.Index));
        Assert.Equal(new[] { 1, 3, 4 }, vm.SelectedMap());
        Assert.True(vm.IsValid, vm.Hint);
        Assert.Equal("单位 nT", vm.Hint);
        Assert.Equal(new[] { "Changed" }, _events);
    }

    [Fact]
    public void EditingASelectionUpdatesTheHintAndReportsAnEdit()
    {
        _source = new("live", ["Bx", "By", "Bz", "B2"], ["nT", "nT", "nT", "uT"], "");
        var vm = Create();
        vm.Refresh();
        _events.Clear();

        vm.FitZ1 = 3;   // 单位不同
        Assert.False(vm.IsValid);
        Assert.Contains("同一磁场单位", vm.Hint);
        Assert.Equal(new[] { "Changed", "Edited" }, _events);

        vm.FitZ1 = 0;   // 与 X 重复
        Assert.False(vm.IsValid);
        Assert.Contains("不能重复", vm.Hint);

        // “自动识别”恢复建议，只算一次修改。
        _events.Clear();
        vm.AutoDetectCommand.Execute(null);
        Assert.Equal(new[] { 0, 1, 2 }, vm.SelectedMap());
        Assert.True(vm.IsValid, vm.Hint);
        Assert.Equal("单位 nT", vm.Hint);
        Assert.Equal(new[] { "Changed", "Edited" }, _events);
    }

    [Fact]
    public void WithoutChannelsTheSourceHintIsShownAndNothingIsSelected()
    {
        _source = new("", [], [], "连接设备后，按当前协议的通道选择 X、Y、Z。");
        var vm = Create();
        vm.Refresh();
        Assert.Empty(vm.Options);
        Assert.Equal("", vm.OptionsSourceKey);
        Assert.Equal(new[] { -1, -1, -1 }, vm.SelectedMap());
        Assert.False(vm.IsValid);
        Assert.Equal("连接设备后，按当前协议的通道选择 X、Y、Z。", vm.Hint);

        // 名称与单位数量不一致：不列出选项，也不给建议。
        _source = new("live", ["Bx", "By", "Bz"], ["nT", "nT"], "");
        vm.Refresh();
        Assert.Empty(vm.Options);
        Assert.False(vm.IsValid);
        Assert.Contains("拟合通道", vm.Hint);
    }

    [Fact]
    public void TheGroupCountIsReadEachTime()
    {
        _source = new("session\u00011", ["X1", "X2", "Y1", "Y2", "Z1", "Z2"], ["nT", "nT", "nT", "nT", "nT", "nT"], "");
        _groups = 2;
        var vm = Create();
        vm.Refresh();
        Assert.Equal(new[] { 0, 2, 4, 1, 3, 5 }, vm.SelectedMap());
        Assert.True(vm.IsValid, vm.Hint);

        // 父视图模型改为单三轴：所选通道按新的组数读取，重新建议时清掉第二组。
        _groups = 1;
        Assert.Equal(new[] { 0, 2, 4 }, vm.SelectedMap());
        vm.ApplySuggestion();
        Assert.Equal(new[] { 0, 2, 4 }, vm.SelectedMap());
        Assert.Equal(-1, vm.FitX2);
        Assert.True(vm.IsValid, vm.Hint);
    }
}
