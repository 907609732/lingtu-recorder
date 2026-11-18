using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnpackVision.Agent.Protocol;
using UnpackVision.App;
using UnpackVision.Application.Agent;
using UnpackVision.Core;

namespace UnpackVision.Tests;

internal sealed class AgentTestHost : IAsyncDisposable
{
    private readonly TemporaryDirectory _directory;

    private AgentTestHost(
        TemporaryDirectory directory,
        InMemoryRepository repository,
        AgentApiHost host,
        string connectionPath)
    {
        _directory = directory;
        Repository = repository;
        Host = host;
        ConnectionPath = connectionPath;
    }

    public InMemoryRepository Repository { get; }
    public AgentApiHost Host { get; }
    public string ConnectionPath { get; }

    public static async Task<AgentTestHost> StartAsync(
        Action<InMemoryRepository>? configure = null,
        Func<RecordAutomationRuntime>? runtime = null,
        IReadOnlyList<IssueTagDefinition>? tags = null,
        Action? recordChanged = null,
        TimeSpan? requestBodyTimeout = null,
        TimeSpan? shutdownTimeout = null)
    {
        var directory = new TemporaryDirectory();
        var connectionPath = Path.Combine(directory.Path, "ai", "connection.json");
        var repository = new InMemoryRepository();
        var record = AgentRecordAutomationTests.CreateRecord(
            RecordingState.Completed,
            "SF1234567890",
            new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(8)));
        record.MediaAssets = record.MediaAssets
            .Select(asset => new RecordMediaAsset
            {
                Id = asset.Id,
                RecordId = record.Id,
                CameraId = asset.CameraId,
                DisplayName = asset.DisplayName,
                Role = asset.Role,
                VideoPath = asset.VideoPath,
                RelativeVideoPath = asset.RelativeVideoPath,
                Codec = asset.Codec,
                EncoderName = asset.EncoderName,
                Width = asset.Width,
                Height = asset.Height,
                FramesPerSecond = asset.FramesPerSecond,
                Duration = asset.Duration,
                StartOffset = asset.StartOffset,
                Integrity = asset.Integrity,
                CreatedAt = asset.CreatedAt,
                UpdatedAt = asset.UpdatedAt
            })
            .ToArray();
        repository.Records.Add(record);
        configure?.Invoke(repository);

        var runtimeValue = new RecordAutomationRuntime(
            DesktopReady: true,
            DatabaseReady: true,
            RecordingState: RecordingState.Idle,
            CurrentRecordId: null,
            CurrentTrackingNo: null,
            AppVersion: "2.6.3-test",
            InitializedAt: new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(8)));
        var runtimeProvider = runtime ?? (() => runtimeValue);
        var service = new RecordAutomationService(
            repository,
            () => tags ?? IssueTagDefaults.Create(),
            new FakeClock(new DateTimeOffset(2026, 11, 19, 9, 30, 0, TimeSpan.FromHours(8))),
            currentRecordIdProvider: () => runtimeProvider().CurrentRecordId);
        var host = new AgentApiHost(
            service,
            runtimeProvider,
            connectionPath,
            recordChanged,
            requestBodyTimeout,
            shutdownTimeout);
        try
        {
            await host.StartAsync(CancellationToken.None);
            return new AgentTestHost(directory, repository, host, connectionPath);
        }
        catch
        {
            await host.DisposeAsync();
            directory.Dispose();
            throw;
        }
    }

    public Task<AgentConnectionInfo> ReadConnectionAsync() =>
        AgentConnectionFile.ReadAsync(ConnectionPath, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        _directory.Dispose();
    }

    public static HttpClient CreateRawHttpClient() =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

    public static HttpRequestMessage CreateToolRequest(
        AgentConnectionInfo connection,
        string tool,
        string json,
        string? token = null,
        string? host = null,
        string? origin = null,
        string? contentType = "application/json")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(connection.BaseUrl), $"v1/tools/{tool}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? connection.Token);
        if (host is not null)
        {
            request.Headers.Host = host;
        }
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        request.Content = content;
        return request;
    }

    public static async Task<(HttpStatusCode Status, JsonDocument Json)> SendAsync(
        HttpClient client,
        HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    public static string ErrorCode(JsonDocument document) =>
        document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
}

internal sealed class UnknownLengthContent(byte[] data) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(data).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
