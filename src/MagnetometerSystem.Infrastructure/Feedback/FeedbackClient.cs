using System.Net.Http.Json;
using System.Text.Json;
using MagnetometerSystem.Core.Feedback;

namespace MagnetometerSystem.Infrastructure.Feedback;

public sealed class FeedbackClient(HttpClient http, Uri endpoint) : IFeedbackClient, IDisposable
{
    public void Dispose() => http.Dispose();
    public async Task<FeedbackReceipt> SubmitAsync(FeedbackSubmission submission, CancellationToken cancellationToken = default)
    {
        var error = FeedbackValidation.Error(submission);
        if (error is not null) throw new InvalidOperationException(error);
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("反馈服务必须使用 HTTPS。");
        using var response = await http.PostAsJsonAsync(endpoint, submission, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => "提交过于频繁，请稍后重试。",
                System.Net.HttpStatusCode.Conflict => "反馈内容与原回执不一致，请重新打开反馈窗口。",
                _ => "反馈服务暂时无法接收，请稍后重试。"
            });
        var receipt = await response.Content.ReadFromJsonAsync<FeedbackReceipt>(cancellationToken).ConfigureAwait(false);
        if (receipt is null || receipt.FeedbackId != submission.FeedbackId || receipt.State is not ("pending" or "syncing" or "unknown" or "synced"))
            throw new JsonException("反馈回执无效，请保留内容并重试。");
        if (receipt.IssueUrl is not null && (!Uri.TryCreate(receipt.IssueUrl, UriKind.Absolute, out var issue)
            || issue.Scheme != "https" || issue.Host != "github.com" || !issue.AbsolutePath.StartsWith("/YIALU/MagnetometerSystem/issues/", StringComparison.Ordinal)))
            throw new JsonException("反馈链接无效。");
        return receipt;
    }
}
