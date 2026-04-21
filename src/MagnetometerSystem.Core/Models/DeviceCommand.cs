namespace MagnetometerSystem.Core.Models;

public enum CommandParameterType
{
    String,
    Int,
    Double,
    Enum,
}

/// <summary>
/// 设备命令参数定义
/// </summary>
public class CommandParameter
{
    /// <summary>参数显示名（如"采样率"）</summary>
    public string Name { get; set; } = "";

    /// <summary>模板占位符 key（如"rate"，对应 {rate}）</summary>
    public string Key { get; set; } = "";

    public CommandParameterType Type { get; set; } = CommandParameterType.String;

    /// <summary>默认值（字符串形式）</summary>
    public string DefaultValue { get; set; } = "";

    /// <summary>单位显示（如"Hz"，可选）</summary>
    public string? Unit { get; set; }

    /// <summary>数值范围最小（仅 Int/Double 生效）</summary>
    public double? Min { get; set; }

    /// <summary>数值范围最大（仅 Int/Double 生效）</summary>
    public double? Max { get; set; }

    /// <summary>枚举候选项（仅 Enum 生效）</summary>
    public List<string> EnumOptions { get; set; } = new();
}

/// <summary>
/// 设备命令定义
/// </summary>
public class DeviceCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>命令名称（UI 显示）</summary>
    public string Name { get; set; } = "";

    /// <summary>命令描述（UI 详情面板显示）</summary>
    public string Description { get; set; } = "";

    /// <summary>是否为 Hex 模式（模板渲染后按十六进制解析）</summary>
    public bool IsHex { get; set; }

    /// <summary>ASCII 模式下是否追加 \r\n</summary>
    public bool AppendNewline { get; set; } = true;

    /// <summary>模板：ASCII 示例 "SET_RATE {rate}"；Hex 示例 "AA 55 {id} 55 AA"</summary>
    public string Template { get; set; } = "";

    /// <summary>命令参数列表（个数可自定义）</summary>
    public List<CommandParameter> Parameters { get; set; } = new();
}

/// <summary>
/// 命令组 — 用户可按功能分组命令
/// </summary>
public class CommandGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public List<DeviceCommand> Commands { get; set; } = new();
}

/// <summary>
/// 命令目录：顶层容器，包含多个命令组，可整体导入/导出 JSON
/// </summary>
public class CommandCatalog
{
    public List<CommandGroup> Groups { get; set; } = new();

    public static CommandCatalog CreateDefault()
    {
        return new CommandCatalog
        {
            Groups = new List<CommandGroup>
            {
                new()
                {
                    Name = "基本命令",
                    Commands = new List<DeviceCommand>
                    {
                        new() { Name = "查询状态", Description = "查询设备状态", Template = "GET_STATUS" },
                        new() { Name = "读取序列号", Description = "读取设备序列号", Template = "GET_SN" },
                        new() { Name = "软件复位", Description = "设备软复位", Template = "RESET" },
                    }
                },
                new()
                {
                    Name = "采集控制",
                    Commands = new List<DeviceCommand>
                    {
                        new() { Name = "开始采集", Description = "启动数据采集", Template = "START" },
                        new() { Name = "停止采集", Description = "停止数据采集", Template = "STOP" },
                        new()
                        {
                            Name = "设置采样率",
                            Description = "设置设备采样率 (Hz)",
                            Template = "SET_RATE {rate}",
                            Parameters = new List<CommandParameter>
                            {
                                new()
                                {
                                    Name = "采样率", Key = "rate",
                                    Type = CommandParameterType.Double,
                                    DefaultValue = "100", Unit = "Hz",
                                    Min = 0.1, Max = 500,
                                }
                            }
                        },
                    }
                }
            }
        };
    }
}
