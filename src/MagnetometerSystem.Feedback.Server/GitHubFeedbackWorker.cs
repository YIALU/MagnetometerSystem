using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MagnetometerSystem.Core.Feedback;

namespace MagnetometerSystem.Feedback.Server;

public sealed class GitHubFeedbackWorker(FeedbackStore store, IHttpClientFactory clients,
    IConfiguration configuration, ILogger<GitHubFeedbackWorker> logger) : BackgroundService
{
    public const string Repository = "YIALU/MagnetometerSystem";
    public static string Marker(Guid id) => $"<!-- feedback-id:{id} -->";
    private static string Literal(string value)
    {
        var longest = Regex.Matches(value, "`+").Select(x => x.Length).DefaultIfEmpty(2).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}text\n{value}\n{fence}";
    }
    public static string IssueBody(FeedbackSubmission request) =>
        $"## 使用场景\n{Literal(request.Scenario)}\n\n## 问题或需求描述\n{Literal(request.Description)}\n\n## 软件版本\n{Literal(request.Version ?? "")}\n\n{Marker(request.FeedbackId)}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("反馈同步暂时不可用；任务保留等待重试。"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var tokenPath = configuration["Feedback:GitHubTokenFile"];
        var token = tokenPath is not null && File.Exists(tokenPath) ? (await File.ReadAllTextAsync(tokenPath, ct)).Trim() : "";
        if (string.IsNullOrEmpty(token)) return;
        var item = await store.NextAsync(ct); if (item is null) return;
        var (request, state) = item.Value;
        using var http = clients.CreateClient("github");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (state == "unknown")
        {
            // 未知结果只核对，不自动重新 POST。分页读取，避免搜索索引延迟导致重复。
            for (var page = 1; page <= 20; page++)
            {
                using var response = await http.GetAsync($"repos/{Repository}/issues?state=all&per_page=100&page={page}", ct);
                if (!response.IsSuccessStatusCode) { await store.UpdateAsync(request.FeedbackId, "unknown", error: "reconcile_unavailable", ct: ct); return; }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var issue in json.RootElement.EnumerateArray())
                    if (issue.TryGetProperty("body", out var body) && body.GetString()?.Contains(Marker(request.FeedbackId), StringComparison.Ordinal) == true)
                    { await store.UpdateAsync(request.FeedbackId, "synced", issue.GetProperty("html_url").GetString(), ct: ct); return; }
                if (json.RootElement.GetArrayLength() < 100) break;
            }
            await store.UpdateAsync(request.FeedbackId, "unknown", error: "manual_review_required", ct: ct); return;
        }
        foreach (var label in new[] { "feedback", "needs-triage" })
        {
            using var existing = await http.GetAsync($"repos/{Repository}/labels/{label}", ct);
            if (existing.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                using var created = await http.PostAsJsonAsync($"repos/{Repository}/labels", new { name = label, color = "1D76DB" }, ct);
                if (!created.IsSuccessStatusCode && (int)created.StatusCode != 422)
                { await store.UpdateAsync(request.FeedbackId, "pending", error: "label_setup_failed", ct: ct); return; }
            }
            else if (!existing.IsSuccessStatusCode)
            { await store.UpdateAsync(request.FeedbackId, "pending", error: "github_authorization_unavailable", ct: ct); return; }
        }
        // 在发出请求之前记录状态；崩溃后启动恢复为 unknown，而非盲目重发。
        await store.UpdateAsync(request.FeedbackId, "syncing", ct: ct);
        try
        {
            var title = "用户反馈：" + new string(request.Scenario.Replace('@', '＠').Where(c => !char.IsControl(c)).Take(65).ToArray());
            using var response = await http.PostAsJsonAsync($"repos/{Repository}/issues",
                new { title, body = IssueBody(request), labels = new[] { "feedback", "needs-triage" } }, ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                await store.UpdateAsync(request.FeedbackId, "synced", json.RootElement.GetProperty("html_url").GetString(), ct: ct);
            }
            else
            {
                var safeRetry = (int)response.StatusCode is 401 or 403 or 422 or 429;
                await store.UpdateAsync(request.FeedbackId, safeRetry ? "pending" : "unknown", error: $"github_{(int)response.StatusCode}", ct: ct);
            }
        }
        catch { await store.UpdateAsync(request.FeedbackId, "unknown", error: "outcome_unknown", ct: CancellationToken.None); }
    }
}
