using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Infrastructure.Feedback;
using MagnetometerSystem.Feedback.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MagnetometerSystem.Feedback.Server.Tests;

public sealed class FeedbackFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedback-test-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "feedback.db");
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Feedback:DatabasePath"] = Database, ["Feedback:GitHubTokenFile"] = Path.Combine(_directory, "not-configured") })));
    private static FeedbackSubmission Request() => new(Guid.NewGuid(), "串口采集后导出", "导出的时间区间不完整", "私有姓名", "private@example.invalid", "0.5.0");

    [Fact]
    public async Task DesktopHttpClientUsesActualServerRouteAndSqlitePersistence()
    {
        using var factory = Factory();
        using var client = new FeedbackClient(factory.CreateClient(new() { BaseAddress = new("https://localhost/") }), new("https://localhost/api/feedback"));
        var request = Request(); var first = await client.SubmitAsync(request); var retry = await client.SubmitAsync(request);
        Assert.Equal(first.FeedbackId, retry.FeedbackId);
        Assert.Equal("pending", retry.State);
        Assert.Equal(request.FeedbackId, (await new FeedbackStore(Database).ReceiptAsync(request.FeedbackId))!.FeedbackId);
    }

    [Fact]
    public async Task AnonymousSubmissionPersistsWithoutExposingPersonalFields()
    {
        using var factory = Factory(); using var client = factory.CreateClient(); var request = Request();
        using var result = await client.PostAsJsonAsync("/api/feedback", request);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        var receipt = await result.Content.ReadFromJsonAsync<FeedbackReceipt>(); Assert.Equal(request.FeedbackId, receipt!.FeedbackId);
        var publicJson = await client.GetStringAsync($"/api/feedback/{request.FeedbackId}");
        Assert.DoesNotContain(request.Name!, publicJson); Assert.DoesNotContain(request.Contact!, publicJson);
        Assert.DoesNotContain(request.Description, publicJson);
        using var connection = new SqliteConnection($"Data Source={Database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM feedback";
        var stored = (string)command.ExecuteScalar()!; Assert.Contains(request.Contact!, stored);
        Assert.DoesNotContain(request.Contact!, GitHubFeedbackWorker.IssueBody(request));
        Assert.DoesNotContain(request.Name!, GitHubFeedbackWorker.IssueBody(request));
    }

    [Fact]
    public async Task RetryAfterLostResponseAndServerRestartReturnsSameReceipt()
    {
        var request = Request() with { Name = null, Contact = null };
        using (var factory = Factory()) { using var client = factory.CreateClient(); Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/feedback", request)).StatusCode); }
        using (var factory = Factory())
        {
            using var client = factory.CreateClient(); var receipt = await (await client.PostAsJsonAsync("/api/feedback", request)).Content.ReadFromJsonAsync<FeedbackReceipt>();
            Assert.Equal(request.FeedbackId, receipt!.FeedbackId);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/feedback", request with { Description = "different" })).StatusCode);
        }
        using var connection = new SqliteConnection($"Data Source={Database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM feedback"; Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData("", "内容")]
    [InlineData("场景", " ")]
    public async Task RejectsMissingRequiredContent(string scenario, string description)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/feedback", Request() with { Scenario = scenario, Description = description })).StatusCode);
    }

    [Fact]
    public async Task RejectsExcessiveTextAndRateLimitsAnonymousSpam()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/feedback", Request() with { Scenario = new string('a', 5001) })).StatusCode);
        for (var i = 0; i < 9; i++) Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/feedback", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/feedback", Request())).StatusCode);
    }

    [Fact]
    public async Task MaximumAllowedChineseContentPersistsWithoutTruncation()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var request = Request() with { Scenario = new string('场', 5000), Description = new string('述', 20000) };
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/feedback", request)).StatusCode);
        using var connection = new SqliteConnection($"Data Source={Database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM feedback";
        var stored = JsonSerializer.Deserialize<FeedbackSubmission>((string)command.ExecuteScalar()!)!;
        Assert.Equal(request.Scenario, stored.Scenario); Assert.Equal(request.Description, stored.Description);
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmissionsReturnOneDurableFeedback()
    {
        using var factory = Factory(); using var client = factory.CreateClient(); var request = Request();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/feedback", request)));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); response.Dispose(); }
        using var connection = new SqliteConnection($"Data Source={Database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM feedback";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task GithubAuthorizationFailureKeepsDurablePendingFeedback()
    {
        var store = new FeedbackStore(Database); var request = Request(); await store.AcceptAsync(request);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await Worker(store, handler).SyncOnceAsync(default);
        Assert.Equal("pending", (await store.ReceiptAsync(request.FeedbackId))!.State);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Methods));
    }

    [Fact]
    public async Task UnknownPostOutcomeIsReconciledWithoutAnotherCreate()
    {
        var store = new FeedbackStore(Database); var request = Request(); await store.AcceptAsync(request);
        var failed = new Handler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("lost response")));
        await Worker(store, failed).SyncOnceAsync(default);
        Assert.Equal("unknown", (await store.ReceiptAsync(request.FeedbackId))!.State);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE feedback SET next_utc='2000-01-01'"; command.ExecuteNonQuery(); }
        var reconcile = new Handler(_ => Json(new[] { new { body = GitHubFeedbackWorker.IssueBody(request), html_url = "https://github.com/YIALU/MagnetometerSystem/issues/999" } }));
        await Worker(store, reconcile).SyncOnceAsync(default);
        Assert.Equal("synced", (await store.ReceiptAsync(request.FeedbackId))!.State);
        Assert.Equal(HttpMethod.Get, Assert.Single(reconcile.Methods));
    }

    [Fact]
    public async Task GithubIssueContainsScenarioDescriptionAndVersionButNoPersonalFields()
    {
        var store = new FeedbackStore(Database); var request = Request() with { Description = "```\n@someone\n希望导出更方便" }; await store.AcceptAsync(request);
        var handler = new Handler(async message =>
        {
            var body = await message.Content!.ReadAsStringAsync();
            Assert.DoesNotContain(request.Contact!, body); Assert.DoesNotContain(request.Name!, body);
            using var json = JsonDocument.Parse(body); var text = json.RootElement.GetProperty("body").GetString()!;
            Assert.Contains(request.Description, text); Assert.Contains(request.Version, text); Assert.Contains("````text", text);
            return Json(new { html_url = "https://github.com/YIALU/MagnetometerSystem/issues/999" });
        });
        await Worker(store, handler).SyncOnceAsync(default);
        Assert.Equal("synced", (await store.ReceiptAsync(request.FeedbackId))!.State);
    }

    private GitHubFeedbackWorker Worker(FeedbackStore store, Handler handler)
    {
        var file = Path.Combine(_directory, "fake-token"); File.WriteAllText(file, "test-only-not-a-credential");
        return new(store, new FactoryClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Feedback:GitHubTokenFile"] = file }).Build(), NullLogger<GitHubFeedbackWorker>.Instance);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class FactoryClient(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new("https://api.github.com/") }; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _asyncResponse;
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : this((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException())) { _asyncResponse = response; }
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/labels/")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            Methods.Add(request.Method); return _asyncResponse is not null ? _asyncResponse(request) : Task.FromResult(response(request));
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
