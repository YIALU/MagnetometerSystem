using System.Text;
using System.Text.RegularExpressions;
using MagnetometerSystem.Core.Feedback;

namespace MagnetometerSystem.Infrastructure.Feedback;

/// <summary>
/// 收集最近几天的程序日志随反馈发送。从最新的文件往前取，总量不超过
/// <see cref="FeedbackLogPayload.MaxTextBytes"/>，超出时保留最新部分。
/// 发送前去掉 Windows 用户名（含用户目录路径）和计算机名；局域网 IP 等连接参数保留，便于排查连接问题。
/// </summary>
public sealed class FeedbackLogCollector : IFeedbackLogSource
{
    public const int DefaultDays = 3;
    private const string OmittedNotice = "（更早的日志超过大小上限，已省略）";

    private readonly Func<string?> _directory;
    private readonly int _maxBytes;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;
    private readonly string? _userName;
    private readonly string? _userProfile;
    private readonly string? _machineName;

    public FeedbackLogCollector(Func<string?> directory, int maxBytes = FeedbackLogPayload.MaxTextBytes, int days = DefaultDays,
        Func<DateTime>? utcNow = null, string? userName = null, string? userProfile = null, string? machineName = null)
    {
        _directory = directory;
        _maxBytes = Math.Min(maxBytes, FeedbackLogPayload.MaxTextBytes);
        _window = TimeSpan.FromDays(days);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _userName = userName ?? Environment.UserName;
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _machineName = machineName ?? Environment.MachineName;
    }

    public async Task<string?> CollectAsync(CancellationToken cancellationToken = default)
    {
        var directory = _directory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        var since = _utcNow() - _window;
        var files = new DirectoryInfo(directory).GetFiles("app-*.log")
            .Where(f => f.LastWriteTimeUtc >= since)
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return null;

        // 脱敏后长度会变，最后再按字节上限截一次。
        var budget = _maxBytes;
        var parts = new List<string>();
        var truncated = false;
        foreach (var file in files)
        {
            if (budget <= 0) { truncated = true; break; }
            var (text, partial) = await ReadTailAsync(file.FullName, budget, cancellationToken).ConfigureAwait(false);
            if (text is null) continue;
            var part = $"==== {file.Name} ===={Environment.NewLine}{text}";
            parts.Add(part);
            budget -= Encoding.UTF8.GetByteCount(part);
            if (partial) { truncated = true; break; }
        }
        if (parts.Count == 0) return null;
        parts.Reverse();
        var body = Redact(string.Join(Environment.NewLine, parts));
        var notice = OmittedNotice + Environment.NewLine;
        var noticeBytes = Encoding.UTF8.GetByteCount(notice);
        if (!truncated && Encoding.UTF8.GetByteCount(body) <= _maxBytes) return body;
        return notice + TrimTail(body, _maxBytes - noticeBytes);
    }

    /// <summary>读取文件末尾不超过 <paramref name="maxBytes"/> 字节；从中间开始时丢掉第一行残行。</summary>
    private static async Task<(string? Text, bool Partial)> ReadTailAsync(string path, int maxBytes, CancellationToken ct)
    {
        try
        {
            // 日志器正在写当天的文件，必须允许共享读写。
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var partial = length > maxBytes;
            var start = partial ? length - maxBytes : 0;
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (start == 0 && text.Length > 0 && text[0] == '﻿') text = text[1..];
            if (partial)
            {
                var newline = text.IndexOf('\n');
                text = newline < 0 ? "" : text[(newline + 1)..];
            }
            return (text, partial);
        }
        catch (IOException) { return (null, false); }
        catch (UnauthorizedAccessException) { return (null, false); }
    }

    public string Redact(string text)
    {
        if (!string.IsNullOrEmpty(_userProfile) && _userProfile.Length > 3)
            text = text.Replace(_userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        // 其他盘符或非当前用户的用户目录也不留用户名。
        text = UserDirectory.Replace(text, m => m.Groups[1].Value + "<用户>");
        // 先替换计算机名：它常以用户名开头（如 ALICE-LAB07），先换用户名会让计算机名匹配不上。
        if (!string.IsNullOrEmpty(_machineName) && _machineName.Length >= 2)
            text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_-]){Regex.Escape(_machineName)}(?![\p{{L}}\p{{N}}_-])", "<计算机>", RegexOptions.IgnoreCase);
        // 太短的名字（如 "a"）会误伤正常文字，只处理 2 个字符以上的。
        if (!string.IsNullOrEmpty(_userName) && _userName.Length >= 2)
            text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(_userName)}(?![\p{{L}}\p{{N}}_])", "<用户>", RegexOptions.IgnoreCase);
        return text;
    }

    private static readonly Regex UserDirectory = new(@"([A-Za-z]:\\(?:Users|Documents and Settings)\\)(?!<用户>)[^\\/\r\n""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>超过上限时保留末尾（最新）内容，从截断处的下一行开始。</summary>
    private static string TrimTail(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) return text;
        var tail = Encoding.UTF8.GetString(bytes, bytes.Length - maxBytes, maxBytes);
        var newline = tail.IndexOf('\n');
        return newline < 0 ? "" : tail[(newline + 1)..];
    }
}
