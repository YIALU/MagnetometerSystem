using System.Text;
using System.Text.RegularExpressions;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>解析测试的输入格式。</summary>
public enum ParseTestInputKind { Hex, Text }

/// <summary>解析测试中解出的一帧（通道值按协议顺序）。</summary>
public sealed record ParseTestFrame(int Index, double[] Values);

/// <summary>解析测试结果：帧、被拒绝的数量、解析器给出的原因和逐帧解析记录。</summary>
public sealed record ParseTestResult(
    int InputByteCount,
    IReadOnlyList<ParseTestFrame> Frames,
    long RejectedCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ChannelNames,
    IReadOnlyList<string> ChannelUnits,
    IReadOnlyList<ParseRecord> Records);

/// <summary>
/// 不连接设备，用当前协议解析一段样例数据。
/// 与实时采集使用同一个 <see cref="ParserFactory"/> 创建的解析器，因此分包、粘包、噪声和重新同步的行为一致；
/// 解析器是独立实例，不触及正在运行的连接或数据流。
/// </summary>
public static class ProtocolParseTester
{
    public const int MaxInputBytes = 64 * 1024;
    public const int MaxFrames = 500;

    /// <summary>把用户输入转换为字节。HEX 允许空格、换行、“-”和 0x 前缀；文本支持 \r \n \t \\ 转义。</summary>
    public static byte[] ReadInput(string text, ParseTestInputKind kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (kind == ParseTestInputKind.Hex)
        {
            var cleaned = Regex.Replace(text, @"0[xX]", " ");
            if (Regex.IsMatch(cleaned, @"[^0-9A-Fa-f\s\-,]"))
                throw new FormatException("HEX 只能包含 0-9、A-F 和分隔空格。");
            return CommandFrameBuilder.ParseHexBytes(cleaned.Replace(",", " "));
        }
        var unescaped = text.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        // 从文本框粘贴的多行内容统一成设备常用的 \n 行尾，避免 \r\n 与 \n 混杂。
        return Encoding.ASCII.GetBytes(unescaped.Replace("\r\n", "\n"));
    }

    public static ParseTestResult Run(ProtocolConfig protocol, byte[] input)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxInputBytes)
            throw new ArgumentException($"样例数据超过 {MaxInputBytes / 1024} KB，请截取一部分再测试。");
        // 使用副本，避免解析器预处理改动正在编辑的协议对象。
        var config = ProtocolConfig.FromJson(protocol.ToJson()) ?? throw new InvalidOperationException("协议配置无效");
        config.Validate();

        var parser = ParserFactory.Create(config);
        var frames = new List<ParseTestFrame>();
        var errors = new List<string>();
        long lastRejected = 0;

        void CollectDiagnostics()
        {
            if (parser is not IParserDiagnostics d || d.RejectedFrameCount == lastRejected) return;
            lastRejected = d.RejectedFrameCount;
            if (d.LastError is { } e && (errors.Count == 0 || errors[^1] != e)) errors.Add(e);
        }

        parser.Feed(input, 0, input.Length);
        while (frames.Count < MaxFrames && parser.TryParse(out var reading))
        {
            CollectDiagnostics();
            if (reading is null) continue;
            frames.Add(new ParseTestFrame(frames.Count + 1, (double[])reading.ChannelValues.Clone()));
        }
        CollectDiagnostics();
        if (frames.Count >= MaxFrames)
            errors.Add($"只显示前 {MaxFrames} 帧。");

        return new ParseTestResult(input.Length, frames,
            parser is IParserDiagnostics diag ? diag.RejectedFrameCount : 0, errors,
            config.DerivedChannelNames, config.DerivedChannelUnits,
            parser is IParserDiagnostics { Records: { } log } ? log.Drain() : []);
    }
}
