using System.Threading.RateLimiting;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Feedback.Server;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
// JSON 中中文可能编码为六字节 \u 转义；覆盖所有允许字段的最大编码尺寸。
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 200_000);
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1; // 保留默认 loopback 信任列表，部署时只监听 loopback。
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("global", _ => new() { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new() { PermitLimit = 10, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton(_ => new FeedbackStore(builder.Configuration["Feedback:DatabasePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "feedback.db")));
builder.Services.AddHttpClient("github", client =>
{
    client.BaseAddress = new Uri("https://api.github.com/"); client.Timeout = TimeSpan.FromSeconds(30);
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"MagnetometerSystem-Feedback/{version}");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});
builder.Services.AddHostedService<GitHubFeedbackWorker>();
var app = builder.Build();
app.UseForwardedHeaders(); app.UseExceptionHandler(); app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/feedback", async (FeedbackSubmission request, FeedbackStore store, CancellationToken ct) =>
{
    var error = FeedbackValidation.Error(request);
    if (error is not null) return Results.Problem(error, statusCode: 400);
    try
    {
        var receipt = await store.AcceptAsync(request, ct);
        return Results.Json(receipt, statusCode: receipt.State == "synced" ? 200 : 202);
    }
    catch (FeedbackConflictException) { return Results.Problem("反馈编号已存在且内容不同。", statusCode: 409); }
}).RequireRateLimiting("submit");
app.MapGet("/api/feedback/{id:guid}", async (Guid id, FeedbackStore store, CancellationToken ct) =>
    await store.ReceiptAsync(id, ct) is { } receipt ? Results.Ok(receipt) : Results.NotFound());
app.Run();
public partial class Program;
