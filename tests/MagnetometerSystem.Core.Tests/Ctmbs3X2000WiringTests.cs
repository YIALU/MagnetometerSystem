using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// CTMBS-3-X2000 协议装配接线测试：ParserFactory 分发、协议 JSON 往返、
/// 既有可能与默认协议共存。
/// </summary>
public class Ctmbs3X2000WiringTests
{
    [Fact]
    public void ParserFactory_DispatchesToCtmbsParser()
    {
        // Category=Ascii，但 ParserKind=Ctmbs3X2000 必须优先生效，
        // 否则会退回到 ConfigurableAsciiParser（无法解析 $<L> 帧）
        var parser = ParserFactory.Create(ProtocolConfig.CreateCtmbs3X2000());
        Assert.IsType<Ctmbs3X2000Parser>(parser);
    }

    [Fact]
    public void Protocol_ReportsFourChannelsDhz()
    {
        var config = ProtocolConfig.CreateCtmbs3X2000();
        Assert.Equal(4, config.DerivedChannelCount);
        Assert.Equal(["D", "H", "Z", "T"], config.DerivedChannelNames);
    }

    [Fact]
    public void Protocol_SurvivesJsonRoundTrip()
    {
        var restored = ProtocolConfig.FromJson(ProtocolConfig.CreateCtmbs3X2000().ToJson());
        Assert.NotNull(restored);
        Assert.Equal(ParserKind.Ctmbs3X2000, restored!.ParserKind);
        Assert.Equal(4, restored.DerivedChannelCount);
        // 命令组随协议往返（结构保留）
        Assert.Equal(3, restored.Commands.Sum(g => g.Commands.Count));
        Assert.Equal(["登录", "启动实时推送", "停止实时推送"],
            restored.Commands.Single().Commands.Select(c => c.Name));
        // IsBuiltIn 带 [JsonIgnore]：往返后丢失（与 ZDZ_C08 一致，由装配时重新赋予）
        Assert.All(restored.Commands, g => Assert.False(g.IsBuiltIn));
    }

    [Fact]
    public void ParserKind_DefaultsToAuto_ForExistingProtocols()
    {
        // 既有协议未显式设置 ParserKind，必须保持 Auto（按 Category 分发），零回归
        Assert.Equal(ParserKind.Auto, ProtocolConfig.CreateZdzC08().ParserKind);
        Assert.Equal(ParserKind.Auto, ProtocolConfig.CreateDefaultAsciiTriaxial().ParserKind);
        Assert.Equal(ParserKind.Auto, ProtocolConfig.CreateCct5Gradiometer().ParserKind);
    }
}
