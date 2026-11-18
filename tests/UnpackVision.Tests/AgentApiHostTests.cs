using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnpackVision.Agent.Protocol;
using UnpackVision.Application.Agent;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class AgentApiHostTests
{
    [Fact]
    public async Task ValidRequest_ReturnsStatusJsonWithNoStoreHeaders()
    {
        await using var testHost = await AgentTestHost.StartAsync();
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();
        using var request = AgentTestHost.CreateToolRequest(connection, AgentToolNames.GetStatus, "{}");

        using var response = await client.SendAsync(request, CancellationToken.None);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("get_status", document.RootElement.GetProperty("tool").GetString());
        Assert.True(document.RootElement.GetProperty("result").GetProperty("desktopReady").GetBoolean());
        Assert.Equal("excel", document.RootElement.GetProperty("result").GetProperty("excelSync").GetProperty("connectorId").GetString());
    }

    [Fact]
    public async Task WrongBearerToken_IsRejectedWithoutLeakingExpectedToken()
    {
        await using var testHost = await AgentTestHost.StartAsync();
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();
        using var request = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.GetStatus,
            "{}",
            token: "wrong-token");

        var (status, document) = await AgentTestHost.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AgentErrorCodes.Unauthorized, AgentTestHost.ErrorCode(document));
        Assert.DoesNotContain(connection.Token, document.RootElement.GetRawText(), StringComparison.Ordinal);
        document.Dispose();
    }

    [Fact]
    public async Task ForeignHostOriginAndContentType_AreRejected()
    {
        await using var testHost = await AgentTestHost.StartAsync();
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();

        using var hostRequest = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.GetStatus,
            "{}",
            host: "localhost:12345");
        var hostResult = await AgentTestHost.SendAsync(client, hostRequest);

        using var originRequest = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.GetStatus,
            "{}",
            origin: "https://evil.example");
        var originResult = await AgentTestHost.SendAsync(client, originRequest);

        using var contentTypeRequest = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.GetStatus,
            "{}",
            contentType: "text/plain");
        var contentTypeResult = await AgentTestHost.SendAsync(client, contentTypeRequest);

        Assert.Equal(HttpStatusCode.Forbidden, hostResult.Status);
        Assert.Equal(AgentErrorCodes.Forbidden, AgentTestHost.ErrorCode(hostResult.Json));
        Assert.Equal(HttpStatusCode.Forbidden, originResult.Status);
        Assert.Equal(AgentErrorCodes.Forbidden, AgentTestHost.ErrorCode(originResult.Json));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, contentTypeResult.Status);
        Assert.Equal(AgentErrorCodes.UnsupportedMediaType, AgentTestHost.ErrorCode(contentTypeResult.Json));
        hostResult.Json.Dispose();
        originResult.Json.Dispose();
        contentTypeResult.Json.Dispose();
    }

    [Fact]
    public async Task NonPostAndChunkedOrOversizedBodies_AreRejectedBeforeDispatch()
    {
        await using var testHost = await AgentTestHost.StartAsync();
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();

        using var getRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(connection.BaseUrl), "v1/tools/get_status"));
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        using var getResponse = await client.SendAsync(getRequest, CancellationToken.None);

        using var chunkedRequest = AgentTestHost.CreateToolRequest(connection, AgentToolNames.GetStatus, "{}");
        chunkedRequest.Content = new UnknownLengthContent(Encoding.UTF8.GetBytes("{}"));
        chunkedRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        chunkedRequest.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await client.SendAsync(chunkedRequest, CancellationToken.None);

        using var oversizedRequest = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            $$"""{"recordId":"{{Guid.NewGuid()}}","note":"{{new string('x', 70_000)}}"}""");
        using var oversizedResponse = await client.SendAsync(oversizedRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.LengthRequired, chunkedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
    }

    [Fact]
    public async Task InvalidIdAndOversizeNote_ReturnStructuredValidationErrors()
    {
        await using var testHost = await AgentTestHost.StartAsync();
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();

        using var invalidId = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.GetRecord,
            """{"recordId":"not-a-guid"}""");
        var invalidResult = await AgentTestHost.SendAsync(client, invalidId);

        var recordId = testHost.Repository.Records[0].Id;
        using var longNote = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            JsonSerializer.Serialize(new { recordId, note = new string('x', 2001) }));
        var longNoteResult = await AgentTestHost.SendAsync(client, longNote);

        Assert.Equal(HttpStatusCode.BadRequest, invalidResult.Status);
        Assert.Equal(AgentErrorCodes.InvalidArgument, AgentTestHost.ErrorCode(invalidResult.Json));
        Assert.Equal(HttpStatusCode.BadRequest, longNoteResult.Status);
        Assert.Equal(AgentErrorCodes.InvalidArgument, AgentTestHost.ErrorCode(longNoteResult.Json));
        invalidResult.Json.Dispose();
        longNoteResult.Json.Dispose();
    }

    [Fact]
    public async Task MissingNote_IsRejectedWithoutClearingExistingNoteOrQueueing()
    {
        await using var testHost = await AgentTestHost.StartAsync();
        var record = testHost.Repository.Records[0];
        record.Note = "keep me";
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();
        using var request = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            JsonSerializer.Serialize(new { recordId = record.Id }));

        var (status, document) = await AgentTestHost.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(AgentErrorCodes.InvalidArgument, AgentTestHost.ErrorCode(document));
        Assert.Equal("keep me", record.Note);
        Assert.Empty(testHost.Repository.Deliveries);
        document.Dispose();
    }

    [Fact]
    public async Task CurrentRecordId_IsRejectedEvenWhenDatabaseStateIsCompleted()
    {
        var record = AgentRecordAutomationTests.CreateRecord(
            RecordingState.Completed,
            "SF-TAIL",
            DateTimeOffset.UtcNow);
        var runtime = new RecordAutomationRuntime(
            DesktopReady: true,
            DatabaseReady: true,
            RecordingState: RecordingState.Completed,
            CurrentRecordId: record.Id,
            CurrentTrackingNo: record.TrackingNo,
            AppVersion: "2.6.3-test",
            InitializedAt: DateTimeOffset.UtcNow);
        await using var testHost = await AgentTestHost.StartAsync(
            repository =>
            {
                repository.Records.Clear();
                repository.Records.Add(record);
            },
            () => runtime);
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();
        using var request = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            JsonSerializer.Serialize(new { recordId = record.Id, note = "blocked" }));

        var (status, document) = await AgentTestHost.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(AgentErrorCodes.Conflict, AgentTestHost.ErrorCode(document));
        Assert.Equal(string.Empty, record.Note);
        Assert.Empty(testHost.Repository.Deliveries);
        document.Dispose();
    }

    [Fact]
    public async Task SuccessfulMutation_InvokesRefreshCallbackButConflictDoesNot()
    {
        var callbacks = 0;
        await using var testHost = await AgentTestHost.StartAsync(
            recordChanged: () => Interlocked.Increment(ref callbacks));
        var record = testHost.Repository.Records[0];
        var active = AgentRecordAutomationTests.CreateRecord(
            RecordingState.Recording,
            "SF-ACTIVE-CALLBACK",
            DateTimeOffset.UtcNow);
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();

        using var success = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            JsonSerializer.Serialize(new { recordId = record.Id, note = "refresh me" }));
        var successResult = await AgentTestHost.SendAsync(client, success);

        testHost.Repository.Records.Add(active);
        using var conflict = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            JsonSerializer.Serialize(new { recordId = active.Id, note = "blocked" }));
        var conflictResult = await AgentTestHost.SendAsync(client, conflict);

        Assert.Equal(HttpStatusCode.OK, successResult.Status);
        Assert.Equal(HttpStatusCode.Conflict, conflictResult.Status);
        Assert.Equal(1, Volatile.Read(ref callbacks));
        successResult.Json.Dispose();
        conflictResult.Json.Dispose();
    }

    [Fact]
    public async Task SlowRequestBody_TimesOutAndShutdownIsBounded()
    {
        var testHost = await AgentTestHost.StartAsync(
            requestBodyTimeout: TimeSpan.FromMilliseconds(200),
            shutdownTimeout: TimeSpan.FromMilliseconds(400));
        try
        {
            var connection = await testHost.ReadConnectionAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, new Uri(connection.BaseUrl).Port);
            await using var stream = client.GetStream();
            var bodyPrefix = "{";
            var header = $"POST /v1/tools/get_status HTTP/1.1\r\n" +
                         $"Host: 127.0.0.1:{new Uri(connection.BaseUrl).Port}\r\n" +
                         $"Authorization: Bearer {connection.Token}\r\n" +
                         "Content-Type: application/json\r\n" +
                         "Content-Length: 100\r\n" +
                         "Connection: close\r\n\r\n" +
                         bodyPrefix;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            var response = await ReadResponseAsync(stream, TimeSpan.FromSeconds(3));
            Assert.Contains("408", response, StringComparison.Ordinal);

            using var secondClient = new TcpClient();
            await secondClient.ConnectAsync(IPAddress.Loopback, new Uri(connection.BaseUrl).Port);
            await using var secondStream = secondClient.GetStream();
            await secondStream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await Task.Delay(50);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await testHost.Host.DisposeAsync();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {stopwatch.Elapsed}");
        }
        finally
        {
            await testHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task WriteToActiveRecording_ReturnsConflictAndDoesNotChangeRepository()
    {
        var activeRecord = AgentRecordAutomationTests.CreateRecord(
            RecordingState.Recording,
            "SF-ACTIVE",
            DateTimeOffset.UtcNow);
        await using var testHost = await AgentTestHost.StartAsync(repository =>
        {
            repository.Records.Clear();
            repository.Records.Add(activeRecord);
        });
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();
        using var request = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.SetRecordNote,
            JsonSerializer.Serialize(new { recordId = activeRecord.Id, note = "blocked" }));

        var (status, document) = await AgentTestHost.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(AgentErrorCodes.Conflict, AgentTestHost.ErrorCode(document));
        Assert.Equal(string.Empty, activeRecord.Note);
        Assert.Empty(testHost.Repository.Deliveries);
        document.Dispose();
    }

    private static async Task<string> ReadResponseAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellation.Token);
            if (read == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, read);
            var text = Encoding.ASCII.GetString(buffer.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task StopAsync_RemovesOnlyItsOwnConnectionFile()
    {
        var own = await AgentTestHost.StartAsync();
        var ownConnection = await own.ReadConnectionAsync();
        await own.Host.StopAsync(CancellationToken.None);
        Assert.False(File.Exists(own.ConnectionPath));

        var rotated = await AgentTestHost.StartAsync();
        var replacement = new AgentConnectionInfo
        {
            BaseUrl = rotated.Host.BaseUrl,
            Token = "another-instance-token",
            Pid = Environment.ProcessId + 1,
            StartedAt = DateTimeOffset.UtcNow
        };
        await AgentConnectionFile.WriteAtomicAsync(rotated.ConnectionPath, replacement, CancellationToken.None);
        await rotated.Host.StopAsync(CancellationToken.None);
        Assert.True(File.Exists(rotated.ConnectionPath));
        var current = await AgentConnectionFile.ReadAsync(rotated.ConnectionPath, CancellationToken.None);
        Assert.Equal(replacement.Token, current.Token);

        await own.DisposeAsync();
        await rotated.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_CancelsAndWaitsForInFlightRepositoryWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var testHost = await AgentTestHost.StartAsync();
        testHost.Repository.GetAsyncOverride = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };
        var connection = await testHost.ReadConnectionAsync();
        using var client = AgentTestHost.CreateRawHttpClient();
        using var request = AgentTestHost.CreateToolRequest(
            connection,
            AgentToolNames.GetRecord,
            JsonSerializer.Serialize(new { recordId = Guid.NewGuid() }));

        var requestTask = client.SendAsync(request, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await testHost.Host.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() => requestTask);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {stopwatch.Elapsed}");
    }
}
