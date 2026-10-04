using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// "磁梯度数采卡-pt" 内置命令集测试。
/// 期望字节序列直接取自《ZDZ_C08 采集模块通信协议》指令表。
/// </summary>
public class ZdzC08CommandTests
{
    private static readonly Dictionary<string, string> NoParams = new();

    private static DeviceCommand Find(string name)
    {
        var cmd = ProtocolConfig.CreateZdzC08().Commands
            .SelectMany(g => g.Commands)
            .FirstOrDefault(c => c.Name == name);
        Assert.NotNull(cmd);
        return cmd;
    }

    private static string Build(string name, Dictionary<string, string>? args = null)
    {
        var cmd = Find(name);
        var frame = CommandFrameBuilder.BuildBinaryFrame(cmd, args ?? NoParams);
        return CommandFrameBuilder.ToHexString(frame.FullBytes);
    }

    [Fact]
    public void Protocol_ShipsCommandGroups()
    {
        var groups = ProtocolConfig.CreateZdzC08().Commands;

        Assert.Equal(["采集控制", "参数标定", "存储管理"], groups.Select(g => g.Name));
        Assert.All(groups, g => Assert.True(g.IsBuiltIn));

        // 文档两张指令表共 17 行，其中"读取目录完成"(FF ED 0D 0A) 与
        // "读取存储完成"(ED 9F 00 00) 只有响应、无发送字节，属于设备上报而非可发指令，
        // 故内置 15 条。
        Assert.Equal(15, groups.Sum(g => g.Commands.Count));
    }

    [Fact]
    public void MagneticOnlyVariant_ShipsSameCommands()
    {
        Assert.Equal(
            ProtocolConfig.CreateZdzC08().Commands.Sum(g => g.Commands.Count),
            ProtocolConfig.CreateZdzC08MagneticOnly().Commands.Sum(g => g.Commands.Count));
    }

    [Theory]
    [InlineData("启动采集", "80 8F")]
    [InlineData("停止采集", "00 8F")]
    [InlineData("打开滤波", "FE EF")]
    [InlineData("关闭滤波", "FE 00")]
    [InlineData("读取正交度系数", "CC 52 5A 4A 00 00")]
    [InlineData("读取存储目录", "CD 00 6D 6C")]
    [InlineData("读取存储数据", "90 9F")]
    [InlineData("停止读取存储", "00 9F")]
    public void RawCommands_MatchDocumentBytes(string name, string expected)
    {
        Assert.Equal(expected, Build(name));
    }

    [Theory]
    [InlineData("1 Hz", "CC F4 00 00 00 01")]
    [InlineData("25 Hz", "CC F4 00 00 00 19")]
    [InlineData("125 Hz", "CC F4 00 00 00 7D")]
    [InlineData("500 Hz", "CC F4 00 00 00 FF")]   // 500Hz 的标识是 255，不是 500
    public void SetSampleRate_EncodesEnumLabelToIdentifier(string label, string expected)
    {
        Assert.Equal(expected, Build("修改采样率", new() { ["rate"] = label }));
    }

    [Theory]
    [InlineData("9600", "CC F5 00 00 00 01")]
    [InlineData("115200", "CC F5 00 00 00 03")]
    [InlineData("2000000", "CC F5 00 00 00 FF")]
    public void SetBaudRate_EncodesEnumLabelToIdentifier(string label, string expected)
    {
        Assert.Equal(expected, Build("修改波特率", new() { ["baud"] = label }));
    }

    [Fact]
    public void SampleRateTable_MatchesDocument()
    {
        // 文档原表写作 "1250HZ ↔ 125"，经确认为 125HZ 笔误
        Assert.Equal(
            [("1 Hz", 1L), ("10 Hz", 10L), ("25 Hz", 25L), ("50 Hz", 50L),
             ("125 Hz", 125L), ("250 Hz", 250L), ("500 Hz", 255L)],
            ZdzC08Commands.SampleRates);
    }

    [Fact]
    public void BaudRateTable_MatchesDocument()
    {
        Assert.Equal(
            [("9600", 1L), ("38400", 2L), ("115200", 3L), ("230400", 4L), ("2000000", 255L)],
            ZdzC08Commands.BaudRates);
    }

    [Fact]
    public void EnumParameter_AcceptsRawNumericInput()
    {
        // 用户直接键入数值（未命中标签）时按原值编码，不静默变成 0
        Assert.Equal("CC F4 00 00 00 32", Build("修改采样率", new() { ["rate"] = "50" }));
    }

    [Fact]
    public void SetMagneticSensitivity_EncodesChannelThenTwoDoubles()
    {
        // CC 57 4D 4B 00 + 通道号(1B) + 灵敏度(double LE) + 零点(double LE) = 21 字节
        var hex = Build("设置磁场灵敏度和零点", new()
        {
            ["ch"] = "2 - Z1",
            ["sensitivity"] = "1",
            ["zero"] = "0",
        });

        Assert.Equal(
            "CC 57 4D 4B 00 02 00 00 00 00 00 00 F0 3F 00 00 00 00 00 00 00 00",
            hex);
    }

    [Fact]
    public void QueryMagneticSensitivity_EncodesChannelOnly()
    {
        Assert.Equal("CC 52 4D 4B 00 05",
            Build("查询磁场灵敏度和零点", new() { ["ch"] = "5 - Z2" }));
    }

    [Fact]
    public void MagneticChannelLabels_MapToSequentialIndexes()
    {
        var param = Find("查询磁场灵敏度和零点").Parameters.Single();

        Assert.Equal(
            ["0 - X1", "1 - Y1", "2 - Z1", "3 - X2", "4 - Y2", "5 - Z2"],
            param.EnumMap.Select(c => c.Label));
        Assert.Equal([0L, 1L, 2L, 3L, 4L, 5L], param.EnumMap.Select(c => c.Value));
    }

    [Fact]
    public void SelectTimeRange_ConcatenatesBothUtcStamps()
    {
        Assert.Equal("DA 00 6D 6C 02 DC 15 D2 02 DC 15 D2",
            Build("选取数据时间", new()
            {
                ["start"] = "02DC15D2",
                ["end"] = "02DC15D2",
            }));
    }

    [Fact]
    public void SelectTimeRange_RejectsWrongLengthUtc()
    {
        var ex = Assert.Throws<FormatException>(() =>
            Build("选取数据时间", new() { ["start"] = "02DC15", ["end"] = "02DC15D2" }));

        Assert.Contains("4 字节", ex.Message);
    }

    [Fact]
    public void SetOrthogonalityMatrix_Requires240Bytes()
    {
        var ex = Assert.Throws<FormatException>(() =>
            Build("设置正交度系数", new() { ["matrix"] = "00 11 22" }));

        Assert.Contains("240 字节", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageDownloadRequiresIsolationAfterProtocolAndCatalogRoundTrips(bool magneticOnly)
    {
        var preset = magneticOnly ? ProtocolConfig.CreateZdzC08MagneticOnly() : ProtocolConfig.CreateZdzC08();
        var protocol = ProtocolConfig.FromJson(preset.ToJson())!;
        var catalogJson = System.Text.Json.JsonSerializer.Serialize(new CommandCatalog { Groups = protocol.Commands });
        var catalog = System.Text.Json.JsonSerializer.Deserialize<CommandCatalog>(catalogJson)!;
        var commands = catalog.Groups.SelectMany(g => g.Commands).ToList();
        var download = Assert.Single(commands.Where(c => c.RequiresIsolatedTransfer));
        Assert.Equal("读取存储数据", download.Name);
        Assert.Equal("90 9F", CommandFrameBuilder.ToHexString(CommandFrameBuilder.BuildBinaryFrame(download, NoParams).FullBytes));
        Assert.Contains("暂不可用", download.Description);
        Assert.All(commands.Where(c => c != download), c => Assert.False(c.RequiresIsolatedTransfer));
        // Existing custom definitions are not classified by a name or device type.
        var custom = System.Text.Json.JsonSerializer.Deserialize<DeviceCommand>("{\"Name\":\"读取存储数据\",\"Template\":\"STATUS\"}")!;
        Assert.False(custom.RequiresIsolatedTransfer);
    }

    [Fact]
    public void AllBuiltInCommands_UseNoChecksum()
    {
        // 文档指令均为裸字节，加校验会让设备无法识别
        var commands = ProtocolConfig.CreateZdzC08().Commands.SelectMany(g => g.Commands);

        Assert.All(commands, c =>
        {
            Assert.Equal(CommandEncoding.BinaryFrame, c.Encoding);
            Assert.Equal(ChecksumKind.None, c.Checksum);
            Assert.Equal("", c.FrameTail);
        });
    }

    [Fact]
    public void CommandGroups_AreNotSerializedAsBuiltIn()
    {
        // IsBuiltIn 带 [JsonIgnore]：用户目录文件里不该出现该标志，
        // 否则导入的组会被误判为只读且无法编辑。
        var json = System.Text.Json.JsonSerializer.Serialize(
            new CommandCatalog { Groups = [.. ProtocolConfig.CreateZdzC08().Commands] });

        Assert.DoesNotContain("IsBuiltIn", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize<CommandCatalog>(json);
        Assert.NotNull(restored);
        Assert.All(restored.Groups, g => Assert.False(g.IsBuiltIn));
    }

    [Fact]
    public void EnumMap_SurvivesCatalogRoundTrip()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new CommandCatalog { Groups = [.. ProtocolConfig.CreateZdzC08().Commands] });
        var restored = System.Text.Json.JsonSerializer.Deserialize<CommandCatalog>(json);

        var rate = restored!.Groups.SelectMany(g => g.Commands)
            .Single(c => c.Name == "修改采样率").Parameters.Single();

        Assert.Equal(7, rate.EnumMap.Count);
        Assert.Equal("500 Hz", rate.EnumMap[^1].Label);
        Assert.Equal(255, rate.EnumMap[^1].Value);
    }

    [Fact]
    public void AsciiTemplate_AlsoResolvesEnumLabels()
    {
        // EnumMap 对 ASCII 模板同样生效，避免把 "25 Hz" 原样拼进命令串
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.AsciiTemplate,
            Template = "SET_RATE {rate}",
            Parameters =
            [
                new()
                {
                    Name = "采样率", Key = "rate", Type = CommandParameterType.U8,
                    EnumMap = [new("25 Hz", 25), new("500 Hz", 255)],
                    DefaultValue = "25 Hz",
                },
            ],
        };

        Assert.Equal("SET_RATE 255",
            CommandFrameBuilder.RenderAsciiTemplate(cmd, new Dictionary<string, string>
            {
                ["rate"] = "500 Hz",
            }));
    }

    [Fact]
    public void ProtocolWithCommands_SurvivesProtocolConfigJsonRoundTrip()
    {
        // 命令内嵌进协议后，最深的一条链是
        // root → Commands[] → CommandGroup → Commands[] → DeviceCommand
        //      → Parameters[] → CommandParameter → EnumMap[] → EnumChoice = 9 层，
        // 超过 FromJson 原本的 MaxDepth=8，会让保存过的协议加载时直接抛异常。
        var restored = ProtocolConfig.FromJson(ProtocolConfig.CreateZdzC08().ToJson());

        Assert.NotNull(restored);
        Assert.Equal(15, restored.Commands.Sum(g => g.Commands.Count));

        var rate = restored.Commands.SelectMany(g => g.Commands)
            .Single(c => c.Name == "修改采样率").Parameters.Single();
        Assert.Equal(255, rate.EnumMap.Single(c => c.Label == "500 Hz").Value);

        // 往返后仍能编码出正确字节
        Assert.Equal("CC F4 00 00 00 FF",
            CommandFrameBuilder.ToHexString(
                CommandFrameBuilder.BuildBinaryFrame(
                    restored.Commands.SelectMany(g => g.Commands).Single(c => c.Name == "修改采样率"),
                    new Dictionary<string, string> { ["rate"] = "500 Hz" }).FullBytes));
    }

    [Fact]
    public void ProtocolsWithoutCommands_ExposeEmptyList()
    {
        // 未绑定命令的协议不应为 null，UI 直接遍历
        Assert.Empty(ProtocolConfig.CreateCct5Gradiometer().Commands);
        Assert.Empty(ProtocolConfig.CreateDefaultAsciiTriaxial().Commands);
    }
}
