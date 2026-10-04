namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 《Windows 与 Linux 板通信协议》CTMBS-3-X2000 台站式三分量数采（eq_precursors）的内置命令集。
/// 请求帧 GET /&lt;len&gt;+&lt;deviceId&gt;+&lt;mnemonic&gt;[+params] /http/1.1 由
/// <see cref="Communication.Ctmbs3X2000FrameBuilder"/> 构造，本类只定义命令与参数。
/// 实时数据接收由 <see cref="Protocol.Ctmbs3X2000Parser"/> 负责，不在此处。
/// </summary>
public static class Ctmbs3X2000Commands
{
    /// <summary>登录默认账户（生产环境以设备 datas.db 为准）</summary>
    public const string DefaultUsername = "administrator";
    public const string DefaultPassword = "01234567";

    /// <summary>示例仪器 ID：X + 测项代码(3) + 厂家标志(4) + 序列号(4)</summary>
    public const string DefaultDeviceId = "X122PWZK0000";

    /// <summary>构建实时数据流所需的内置命令组（登录 / 启动推送 / 停止推送）。</summary>
    public static List<CommandGroup> CreateGroups() =>
    [
        new()
        {
            Name = "实时数据",
            IsBuiltIn = true,
            Commands =
            [
                new()
                {
                    Name = "登录",
                    Description = "lin 登录。成功响应 $ack\\n<仪器ID>；失败 $err\\n。"
                                + "会话绑定到当前 TCP 连接，断开后需重新登录。",
                    Encoding = CommandEncoding.CtmbsRequest,
                    Template = "lin",
                    AppendNewline = true,
                    Parameters =
                    [
                        new() { Name = "设备ID", Key = "deviceId",
                                Type = CommandParameterType.String, DefaultValue = DefaultDeviceId },
                        new() { Name = "用户名", Key = "username",
                                Type = CommandParameterType.String, DefaultValue = DefaultUsername },
                        new() { Name = "口令", Key = "password",
                                Type = CommandParameterType.String, DefaultValue = DefaultPassword },
                    ],
                },
                new()
                {
                    Name = "启动实时推送",
                    Description = "dat+0 启动 1Hz 实时数据推送（须先登录）。"
                                + "服务端每秒主动推送一帧 $<L>\\n<L digits><payload>\\nack\\n 数据帧。",
                    Encoding = CommandEncoding.CtmbsRequest,
                    Template = "dat",
                    AppendNewline = true,
                    Parameters =
                    [
                        new() { Name = "设备ID", Key = "deviceId",
                                Type = CommandParameterType.String, DefaultValue = DefaultDeviceId },
                        new() { Name = "模式", Key = "mode",
                                Type = CommandParameterType.String, DefaultValue = "0" },
                    ],
                },
                new()
                {
                    Name = "停止实时推送",
                    Description = "stp 停止实时数据推送，服务端回 $ack\\n。",
                    Encoding = CommandEncoding.CtmbsRequest,
                    Template = "stp",
                    AppendNewline = true,
                    Parameters =
                    [
                        new() { Name = "设备ID", Key = "deviceId",
                                Type = CommandParameterType.String, DefaultValue = DefaultDeviceId },
                    ],
                },
            ],
        },
    ];
}
