using System.Text;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// CTMBS-3-X2000 请求帧构建器。
/// 构造 GET /&lt;len&gt;+&lt;deviceId&gt;+&lt;mnemonic&gt;[+&lt;param&gt;...] /http/1.1，
/// &lt;len&gt; 为自参考长度（整串字节数等于长度值自身）。
/// 算法依据《Windows 与 Linux 板通信协议》§3.1 请求帧 + §3.3 自参考长度。
/// </summary>
public static class Ctmbs3X2000FrameBuilder
{
    private const string EnvelopePrefix = "GET /";
    private const string EnvelopeSuffix = " /http/1.1";

    /// <summary>
    /// 渲染请求帧文本（不含行终止符），供预览与日志。
    /// </summary>
    public static string RenderRequest(DeviceCommand cmd, IReadOnlyDictionary<string, string> paramValues)
    {
        var deviceId = ResolveDeviceId(cmd, paramValues);
        var mnemonic = cmd.Template ?? "";
        var trailing = cmd.Parameters
            .Where(p => p.Key != "deviceId")
            .Select(p => ResolveValue(p, paramValues));

        var inner = deviceId + "+" + mnemonic;
        foreach (var t in trailing)
            inner += "+" + t;

        var len = SolveSelfReferentialLength(inner);
        var content = len + "+" + inner;
        return EnvelopePrefix + content + EnvelopeSuffix;
    }

    /// <summary>
    /// 构造最终发送字节：帧文本 + （AppendNewline 时追加 \n）。
    /// 选用 LF：对端为嵌入式 Linux 板，协议以"文本行"为单位读取。
    /// </summary>
    public static byte[] BuildRequestBytes(DeviceCommand cmd, IReadOnlyDictionary<string, string> paramValues)
    {
        var text = RenderRequest(cmd, paramValues);
        if (cmd.AppendNewline) text += "\n";
        return Encoding.UTF8.GetBytes(text);
    }

    // ---- 参数解析 ----

    private static string ResolveDeviceId(DeviceCommand cmd, IReadOnlyDictionary<string, string> paramValues)
    {
        var def = cmd.Parameters.FirstOrDefault(p => p.Key == "deviceId")
            ?? throw new InvalidOperationException(
                $"CTMBS 请求缺少 deviceId 参数（命令 '{cmd.Name}' 需定义 Key=deviceId 的参数）");

        var raw = ResolveValue(def, paramValues);
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException(
                $"CTMBS 请求的 deviceId 为空（命令 '{cmd.Name}'）");
        return raw;
    }

    /// <summary>取参数运行时值，缺失时回落到默认值（与 CommandFrameBuilder 一致）。</summary>
    private static string ResolveValue(CommandParameter p, IReadOnlyDictionary<string, string> paramValues)
    {
        if (paramValues.TryGetValue(p.Key, out var v) && !string.IsNullOrEmpty(v))
            return v;
        return p.DefaultValue ?? "";
    }

    // ---- 自参考长度（§3.3） ----

    /// <summary>
    /// 求 L 使 len("L" + "+" + inner) == L，
    /// 等价于 L = len(inner) + 1（'+' 分隔符） + digits(L)。
    /// 迭代至多 ~3 次收敛；为防病态输入设上限 16 次后抛异常。
    /// </summary>
    internal static int SolveSelfReferentialLength(string inner)
    {
        // 先按 1 位估：L = len(inner) + 1（'+'） + 1（位数）
        int innerBytes = Encoding.UTF8.GetByteCount(inner);
        int len = innerBytes + 2;
        for (int guard = 0; guard < 16; guard++)
        {
            int digits = len.ToString().Length;
            int computed = innerBytes + 1 + digits;
            if (computed == len)
                return len;
            len = computed;
        }
        throw new InvalidOperationException("自参考长度未收敛（输入异常）");
    }
}
