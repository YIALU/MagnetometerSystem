using System.Text.Json;
using MagnetometerSystem.Core.Feedback;

namespace MagnetometerSystem.Infrastructure.Feedback;

/// <summary>独立文件保存草稿，不触及采集数据库；替换前先完成新文件写入。</summary>
public sealed class FeedbackDraftStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<FeedbackSubmission?> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<FeedbackSubmission>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(FeedbackSubmission value)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value)).ConfigureAwait(false);
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
