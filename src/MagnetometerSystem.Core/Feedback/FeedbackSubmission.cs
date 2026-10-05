namespace MagnetometerSystem.Core.Feedback;

public sealed record FeedbackSubmission(Guid FeedbackId, string Scenario, string Description,
    string? Name = null, string? Contact = null, string Version = "");

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
        return null;
    }
}

public interface IFeedbackClient
{
    Task<FeedbackReceipt> SubmitAsync(FeedbackSubmission submission, CancellationToken cancellationToken = default);
}
