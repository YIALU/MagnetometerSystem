using System.Text.Json;
using System.Text.Json.Nodes;
using MagnetometerSystem.Core.Feedback;

namespace MagnetometerSystem.Infrastructure.Feedback;

/// <summary>独立文件保存草稿，不触及采集数据库；替换前先完成新文件写入。</summary>
public sealed class FeedbackDraftStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<FeedbackSubmission?> LoadAsync() => (await LoadDraftAsync().ConfigureAwait(false)).Submission;

    /// <summary>读取草稿及用户的“附带日志”选择；旧版本草稿没有该选择时为 null。</summary>
    public async Task<(FeedbackSubmission? Submission, bool? IncludeLogs)> LoadDraftAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return (null, null);
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var submission = JsonSerializer.Deserialize<FeedbackSubmission>(json);
            // 选择单独保存：Logs 为 null 既可能是取消了勾选，也可能是还没点过提交。
            bool? includeLogs = JsonNode.Parse(json) is JsonObject node && node[IncludeLogsKey] is JsonValue value
                && value.TryGetValue<bool>(out var flag) ? flag : null;
            return (submission, includeLogs);
        }
        finally { _gate.Release(); }
    }

    private const string IncludeLogsKey = "IncludeLogs";

    public async Task SaveAsync(FeedbackSubmission value, bool? includeLogs = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var node = JsonSerializer.SerializeToNode(value)!.AsObject();
            if (includeLogs.HasValue) node[IncludeLogsKey] = includeLogs.Value;
            await File.WriteAllTextAsync(temporary, node.ToJsonString()).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (File.Exists(path)) File.Delete(path); }
        finally { _gate.Release(); }
    }
}
