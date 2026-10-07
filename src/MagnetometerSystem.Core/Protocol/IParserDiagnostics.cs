namespace MagnetometerSystem.Core.Protocol;

/// <summary>无效报文已被消费，诊断不会阻止后续有效报文继续解析。</summary>
public interface IParserDiagnostics
{
    long RejectedFrameCount { get; }
    string? LastError { get; }

    /// <summary>逐帧解析记录（通过 / 拒绝 / 丢弃字节），有界；未实现的解析器返回 null。</summary>
    ParseRecordLog? Records => null;
}
