using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Tests;

public sealed class AgentClientTests
{
    [Fact]
    public async Task ConnectionFile_RoundTripsWithoutExposingTokenInToString()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ai", "connection.json");
        var info = new AgentConnectionInfo
        {
            Version = 1,
            BaseUrl = "http://127.0.0.1:43210/",
            Token = "super-secret-token",
            Pid = 1234,
            StartedAt = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero)
        };

        await AgentConnectionFile.WriteAtomicAsync(path, info, CancellationToken.None);
        var read = await AgentConnectionFile.ReadAsync(path, CancellationToken.None);

        Assert.Equal(info.BaseUrl, read.BaseUrl);
        Assert.Equal(info.Token, read.Token);
        Assert.Equal(info.Pid, read.Pid);
        Assert.DoesNotContain("super-secret-token", read.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task Client_ReReadsRotatedConnectionFileForEveryCall()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var handler = new CaptureHandler();
        var client = new AgentApiClient(path, handler, TimeSpan.FromSeconds(2));

        await WriteConnectionAsync(path, "http://127.0.0.1:41001/", "token-one-0123456789");
        await client.CallToolAsync(AgentToolNames.GetStatus, new AgentToolRequest(), CancellationToken.None);
        await WriteConnectionAsync(path, "http://127.0.0.1:41002/", "token-two-0123456789");
        await client.CallToolAsync(AgentToolNames.GetStatus, new AgentToolRequest(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://127.0.0.1:41001/v1/tools/get_status", handler.Requests[0].Uri);
        Assert.Equal("Bearer token-one-0123456789", handler.Requests[0].Authorization);
        Assert.Equal("http://127.0.0.1:41002/v1/tools/get_status", handler.Requests[1].Uri);
        Assert.Equal("Bearer token-two-0123456789", handler.Requests[1].Authorization);
    }

    [Theory]
    [InlineData("https://127.0.0.1:41001/")]
    [InlineData("http://localhost:41001/")]
    [InlineData("http://127.0.0.2:41001/")]
    [InlineData("http://[::1]:41001/")]
    [InlineData("http://127.0.0.1:41001/evil")]
    [InlineData("http://user:pass@127.0.0.1:41001/")]
    public async Task Client_RejectsAnythingExceptLiteralHttpLoopback(string baseUrl)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var handler = new CaptureHandler();
        var client = new AgentApiClient(path, handler, TimeSpan.FromSeconds(2));
        await WriteConnectionAsync(path, baseUrl, "token");

        var exception = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            client.CallToolAsync(AgentToolNames.GetStatus, new AgentToolRequest(), CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("token", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_SecureHandlerDisablesProxyRedirectsAndCookies()
    {
        using var handler = AgentApiClient.CreateSecureHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task Client_DoesNotFollowRedirectOrLeakTheRedirectTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var handler = new CaptureHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://192.0.2.10:41001/v1/tools/get_status");
            response.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            return response;
        });
        var client = new AgentApiClient(path, handler, TimeSpan.FromSeconds(2));
        await WriteConnectionAsync(path, "http://127.0.0.1:41001/", "sensitive-token-0123456789");

        var exception = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.CallToolAsync(AgentToolNames.GetStatus, new AgentToolRequest(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("192.0.2.10", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_MissingConnectionIsOfflineAndDoesNotLeakItsPath()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "missing.json");
        var client = new AgentApiClient(path, new CaptureHandler(), TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            client.CallToolAsync(AgentToolNames.GetStatus, new AgentToolRequest(), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Unavailable, exception.Code);
        Assert.Contains("not running", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_TimeoutIsUnavailableAndIsNotRetried()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var handler = new DelayedHandler();
        var client = new AgentApiClient(path, handler, TimeSpan.FromMilliseconds(100));
        await WriteConnectionAsync(path, "http://127.0.0.1:41001/", "test-token-0123456789");

        var exception = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            client.CallToolAsync(AgentToolNames.SetRecordNote, new AgentToolRequest(), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Unavailable, exception.Code);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ClientTimeoutCoversAResponseBodyThatStallsAfterHeaders()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var client = new AgentApiClient(
            path,
            new StallBodyHandler(),
            TimeSpan.FromMilliseconds(150));
        await WriteConnectionAsync(path, "http://127.0.0.1:41001/", "test-token-0123456789");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            client.CallToolAsync(AgentToolNames.GetStatus, new AgentToolRequest(), CancellationToken.None));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Timeout took {stopwatch.Elapsed}");
        Assert.DoesNotContain("test-token-0123456789", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_UserCancellationRemainsCancellationNotTimeout()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var client = new AgentApiClient(
            path,
            new StallBodyHandler(),
            TimeSpan.FromSeconds(5));
        await WriteConnectionAsync(path, "http://127.0.0.1:41001/", "test-token-0123456789");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CallToolAsync(
                AgentToolNames.GetStatus,
                new AgentToolRequest(),
                cancellation.Token));
    }

    [Fact]
    public async Task Client_MapsServerErrorEnvelopeWithoutRawDetails()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var handler = new CaptureHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            """{"ok":false,"error":{"code":"conflict","message":"Only completed or imported records can be modified."}}"""));
        var client = new AgentApiClient(path, handler, TimeSpan.FromSeconds(2));
        await WriteConnectionAsync(path, "http://127.0.0.1:41001/", "test-token-0123456789");

        var exception = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.CallToolAsync(AgentToolNames.SetRecordNote, new AgentToolRequest(), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Conflict, exception.Code);
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("Only completed or imported records can be modified.", exception.Message);
    }

    private static async Task WriteConnectionAsync(string path, string baseUrl, string token)
    {
        // Tests deliberately simulate a tampered file, so they bypass the
        // writer's loopback validation and let the reader reject it.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new AgentConnectionInfo
                {
                    BaseUrl = baseUrl,
                    Token = token,
                    Pid = Environment.ProcessId,
                    StartedAt = DateTimeOffset.UtcNow
                },
                AgentJson.Options));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.ProxyAuthorization?.ToString()));
            var response = responder?.Invoke(request) ?? JsonResponse(
                HttpStatusCode.OK,
                """{"ok":true,"tool":"get_status","result":{"desktopReady":true}}""");
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(string Uri, string? Authorization, string? ProxyAuthorization);

    private sealed class StallBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallContent()
            });
    }

    private sealed class StallContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new StallStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("stall content is read through CreateContentReadStreamAsync");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "unpackvision-agent-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
