using System.IO.Compression;
using System.Text;

namespace MagnetometerSystem.Core.Feedback;

/// <param name="Logs">选填：本地日志文本经 gzip 压缩后的 Base64（见 <see cref="FeedbackLogPayload"/>）。
/// null 表示未勾选附带日志，空字符串表示勾选了但没有可用日志。只保存在反馈服务器私有数据库，不进入公开 Issue。</param>
public sealed record FeedbackSubmission(Guid FeedbackId, string Scenario, string Description,
    string? Name = null, string? Contact = null, string Version = "", string? Logs = null);

public sealed record FeedbackReceipt(Guid FeedbackId, string State, string? IssueUrl = null);

public static class FeedbackValidation
{
    public static string? Error(FeedbackSubmission? value)
    {
        if (value is null || value.FeedbackId == Guid.Empty) return "反馈编号无效。";
        if (string.IsNullOrWhiteSpace(value.Scenario)) return "请填写使用场景。";
        if (string.IsNullOrWhiteSpace(value.Description)) return "请填写问题或需求描述。";
        if (value.Scenario.Length > 5000 || value.Description.Length > 20000) return "内容过长：场景最多 5000 字，描述最多 20000 字。";
        if (value.Name?.Length > 100 || value.Contact?.Length > 250 || value.Version?.Length > 200) return "姓名、联系方式或版本信息过长。";
        if (!string.IsNullOrEmpty(value.Logs) && !FeedbackLogPayload.TryDecode(value.Logs, out _)) return "附带日志无效或过大。";
        return null;
    }
}

/// <summary>
/// 反馈附带日志的编码：UTF-8 文本 → gzip → Base64。客户端最多取 <see cref="MaxTextBytes"/> 字节文本；
/// 服务端解码时同样按上限检查，防止压缩炸弹。
/// </summary>
public static class FeedbackLogPayload
{
    /// <summary>客户端截取的日志文本上限（UTF-8 字节）。</summary>
    public const int MaxTextBytes = 512 * 1024;
    /// <summary>编码后长度上限：不可压缩的最坏情况（gzip 头尾 + Base64 膨胀）也放得下。</summary>
    public const int MaxEncodedLength = (MaxTextBytes + 1024) / 3 * 4 + 4;

    public static string Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes) throw new ArgumentException("日志文本超过上限。", nameof(text));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes);
        return Convert.ToBase64String(output.ToArray());
    }

    public static bool TryDecode(string encoded, out string text)
    {
        text = "";
        if (encoded.Length == 0 || encoded.Length > MaxEncodedLength) return false;
        var compressed = new byte[encoded.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(encoded, compressed, out var length)) return false;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(compressed, 0, length), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxTextBytes) return false;
                output.Write(buffer, 0, read);
            }
            text = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }
}

public interface IFeedbackClient
{
    Task<FeedbackReceipt> SubmitAsync(FeedbackSubmission submission, CancellationToken cancellationToken = default);
}

/// <summary>提供可随反馈发送的本地日志：已脱敏的纯文本，没有日志时返回 null。</summary>
public interface IFeedbackLogSource
{
    Task<string?> CollectAsync(CancellationToken cancellationToken = default);
}
