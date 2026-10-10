using System.IO.Compression;
using MagnetometerSystem.Core.Feedback;

namespace MagnetometerSystem.Core.Tests;

public sealed class FeedbackLogPayloadTests
{
    private static FeedbackSubmission Request(string? logs) => new(Guid.NewGuid(), "场景", "描述", Logs: logs);

    [Fact]
    public void RoundTripKeepsChineseText()
    {
        var text = "2026-10-10 12:00:00.000 [ERR] 保存失败，保留 500 条待重试\r\n连接状态: 已断开 TCP 192.168.1.100:5000\r\n";
        Assert.True(FeedbackLogPayload.TryDecode(FeedbackLogPayload.Encode(text), out var decoded));
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void IncompressibleMaximumFitsEncodedLimit()
    {
        var random = new Random(1);
        var text = new string(Enumerable.Range(0, FeedbackLogPayload.MaxTextBytes).Select(_ => (char)random.Next(33, 127)).ToArray());
        var encoded = FeedbackLogPayload.Encode(text);
        Assert.True(encoded.Length <= FeedbackLogPayload.MaxEncodedLength);
        Assert.Null(FeedbackValidation.Error(Request(encoded)));
    }

    [Fact]
    public void RejectsTextOverLimitAndDecompressionBomb()
    {
        Assert.Throws<ArgumentException>(() => FeedbackLogPayload.Encode(new string('a', FeedbackLogPayload.MaxTextBytes + 1)));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(new byte[FeedbackLogPayload.MaxTextBytes + 1]);
        var bomb = Convert.ToBase64String(output.ToArray());
        Assert.True(bomb.Length < 10_000);
        Assert.False(FeedbackLogPayload.TryDecode(bomb, out _));
        Assert.NotNull(FeedbackValidation.Error(Request(bomb)));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("aGVsbG8=")] // 合法 Base64，但不是 gzip
    public void RejectsMalformedLogs(string logs) => Assert.NotNull(FeedbackValidation.Error(Request(logs)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingOrEmptyLogsAreValid(string? logs) => Assert.Null(FeedbackValidation.Error(Request(logs)));
}
