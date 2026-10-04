namespace MagnetometerSystem.Core.Protocol;

/// <summary>无效报文已被消费，诊断不会阻止后续有效报文继续解析。</summary>
public interface IParserDiagnostics
{
    long RejectedFrameCount { get; }
    string? LastError { get; }
}
