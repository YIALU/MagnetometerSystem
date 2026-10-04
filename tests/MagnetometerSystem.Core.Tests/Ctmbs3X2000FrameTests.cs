using System.Text;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// CTMBS-3-X2000 请求帧构建器测试。
/// 黄金请求串取自《Windows 与 Linux 板通信协议》§12 速查表（deviceId = X122PWZK0000），
/// 自参考长度算法见 §3.3。登录默认账户 administrator / 01234567。
/// </summary>
public class Ctmbs3X2000FrameTests
{
    private static readonly Dictionary<string, string> Empty = new();

    private static DeviceCommand Find(string name) =>
        ProtocolConfig.CreateCtmbs3X2000().Commands
            .SelectMany(g => g.Commands)
            .FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException($"未找到命令: {name}");

    private static string Render(string name, Dictionary<string, string>? args = null) =>
        Ctmbs3X2000FrameBuilder.RenderRequest(Find(name), args ?? Empty);

    private static byte[] Bytes(string name, Dictionary<string, string>? args = null) =>
        Ctmbs3X2000FrameBuilder.BuildRequestBytes(Find(name), args ?? Empty);

    // ---- 命令集装配 ----

    [Fact]
    public void Protocol_ShipsRealtimeCommandGroup()
    {
        var groups = ProtocolConfig.CreateCtmbs3X2000().Commands;

        Assert.Equal(["实时数据"], groups.Select(g => g.Name));
        Assert.All(groups, g => Assert.True(g.IsBuiltIn));
        Assert.Equal(["登录", "启动实时推送", "停止实时推送"],
            groups.Single().Commands.Select(c => c.Name));
    }

    [Fact]
    public void Commands_UseCtmbsRequestEncoding()
    {
        var commands = ProtocolConfig.CreateCtmbs3X2000().Commands.SelectMany(g => g.Commands);
        Assert.All(commands, c => Assert.Equal(CommandEncoding.CtmbsRequest, c.Encoding));
    }

    [Fact]
    public void Login_DefaultCredentialsAreAdministratorPassword()
    {
        var login = Find("登录");
        var p = login.Parameters.Single(x => x.Key == "username");
        Assert.Equal("administrator", p.DefaultValue);
        var pwd = login.Parameters.Single(x => x.Key == "password");
        Assert.Equal("01234567", pwd.DefaultValue);
    }

    // ---- §12 黄金请求串 ----

    [Fact]
    public void Login_AdminAdmin_MatchesDocGoldenString()
    {
        // 文档示例：content = 31+X122PWZK0000+lin+admin+admin（恰好 31 字节）
        Assert.Equal("GET /31+X122PWZK0000+lin+admin+admin /http/1.1",
            Render("登录", new() { ["username"] = "admin", ["password"] = "admin" }));
    }

    [Fact]
    public void Login_DefaultCredentials_MatchesComputedLength()
    {
        // inner = X122PWZK0000+lin+administrator+01234567 = 39 字节；
        // L = 39 + 1 + digits(42)=2 = 42 → content = 42+inner = 42 字节
        Assert.Equal("GET /42+X122PWZK0000+lin+administrator+01234567 /http/1.1",
            Render("登录"));
    }

    [Fact]
    public void StartRealtime_Dat0_MatchesDocGoldenString()
    {
        Assert.Equal("GET /21+X122PWZK0000+dat+0 /http/1.1", Render("启动实时推送"));
    }

    [Fact]
    public void StopRealtime_Stp_MatchesDocGoldenString()
    {
        Assert.Equal("GET /19+X122PWZK0000+stp /http/1.1", Render("停止实时推送"));
    }

    // ---- 自参考长度算法（§3.3）的独立验证 ----

    [Fact]
    public void Length_SelfReferentialEquationHolds()
    {
        // content = L + "+" + inner，且必须满足 len(content) == L
        var frame = Render("启动实时推送");
        // 去掉 "GET /" 前缀与 " /http/1.1" 后缀，得 content
        var content = frame["GET /".Length..^" /http/1.1".Length];
        var plus = content.IndexOf('+');
        var L = int.Parse(content[..plus]);
        Assert.Equal(L, content.Length);
    }

    [Fact]
    public void BuildRequestBytes_AppendsLineFeedTerminator()
    {
        var bytes = Bytes("停止实时推送");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal("GET /19+X122PWZK0000+stp /http/1.1\n", text);
    }

    [Fact]
    public void DeviceId_IsAlwaysSecondSegment()
    {
        // 换一个 deviceId，确认它出现在 length 与 mnemonic 之间
        var frame = Render("启动实时推送", new() { ["deviceId"] = "X122ABCD1234" });
        Assert.Equal("GET /21+X122ABCD1234+dat+0 /http/1.1", frame);
    }

    [Fact]
    public void MissingDeviceId_Throws()
    {
        // 直接构造一个无 deviceId 参数的命令，构建器必须报错而非发出空帧
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.CtmbsRequest,
            Template = "stp",
            Parameters = new() { new() { Name = "p", Key = "other", DefaultValue = "x" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => Ctmbs3X2000FrameBuilder.RenderRequest(cmd, Empty));
        Assert.Contains("deviceId", ex.Message);
    }
}
