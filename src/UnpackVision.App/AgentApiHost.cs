using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnpackVision.Agent.Protocol;
using UnpackVision.Application.Agent;
using UnpackVision.Core;

namespace UnpackVision.App;

/// <summary>
/// Authenticated loopback HTTP edge for local agents. It is started only after
/// the desktop has initialized its repository and sync queue. All business
/// orchestration stays in <see cref="RecordAutomationService"/>; this type owns
/// transport security, token rotation and connection-file lifetime.
/// </summary>
public sealed class AgentApiHost : IAsyncDisposable
{
    private readonly RecordAutomationService _service;
    private readonly Func<RecordAutomationRuntime> _runtimeProvider;
    private static readonly TimeSpan DefaultRequestBodyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(12);
    private readonly string? _connectionPathOverride;
    private readonly Action? _recordChanged;
    private readonly TimeSpan _requestBodyTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _requestLifetime = new();
    private WebApplication? _app;
    private string _token = string.Empty;
    private string _expectedHost = string.Empty;
    private string _expectedOrigin = string.Empty;
    private int _port;
    private bool _started;
    private bool _disposed;

    public AgentApiHost(
        RecordAutomationService service,
        Func<RecordAutomationRuntime> runtimeProvider,
        string? connectionPath = null,
        Action? recordChanged = null,
        TimeSpan? requestBodyTimeout = null,
        TimeSpan? shutdownTimeout = null)
    {
        _service = service;
        _runtimeProvider = runtimeProvider;
        _connectionPathOverride = connectionPath;
        _recordChanged = recordChanged;
        _requestBodyTimeout = requestBodyTimeout ?? DefaultRequestBodyTimeout;
        _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
    }

    public string ConnectionPath { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            // Resolve inside the guarded start path so a malformed test
            // override cannot fail desktop initialization before recording.
            ConnectionPath = AgentConnectionFile.ResolvePath(_connectionPathOverride);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = "UnpackVision.AgentApiHost",
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();
            builder.Services.Configure<HostOptions>(options =>
                options.ShutdownTimeout = _shutdownTimeout);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = AgentApiContract.MaxRequestBytes;
                options.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                });
            });

            var app = builder.Build();
            app.Run(HandleRequestAsync);
            _app = app;

            await app.StartAsync(cancellationToken);
            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var boundAddress = addresses?
                .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(uri => uri is not null)
                ?? throw new InvalidOperationException("The agent API did not publish a loopback address.");
            _port = boundAddress.Port;
            BaseUrl = $"http://127.0.0.1:{_port}/";
            _expectedHost = $"127.0.0.1:{_port}";
            _expectedOrigin = $"http://127.0.0.1:{_port}";
            _token = CreateToken();

            await AgentConnectionFile.WriteAtomicAsync(
                ConnectionPath,
                new AgentConnectionInfo
                {
                    BaseUrl = BaseUrl,
                    Token = _token,
                    Pid = Environment.ProcessId,
                    StartedAt = DateTimeOffset.Now
                },
                cancellationToken);
            _started = true;
        }
        catch
        {
            if (_app is not null)
            {
                try
                {
                    await _app.DisposeAsync();
                }
                catch (Exception)
                {
                }
                _app = null;
            }
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        var app = _app;
        try
        {
            if (app is null)
            {
                return;
            }

            _app = null;
            _started = false;
            await _requestLifetime.CancelAsync();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_shutdownTimeout);
                await app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                try
                {
                    await app.DisposeAsync();
                }
                catch (Exception)
                {
                }
                await AgentConnectionFile.TryDeleteIfOwnedAsync(
                    ConnectionPath,
                    _token,
                    Environment.ProcessId,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await StopAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Dispose is bounded and must not prevent desktop exit.
        }
        finally
        {
            _requestLifetime.Dispose();
            _lifecycleGate.Dispose();
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        PrepareResponse(context);
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(AgentApiContract.ToolsPathPrefix, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, 404, AgentErrorCodes.NotFound, "Unknown agent endpoint.");
            return;
        }
        if (!IsLoopbackRequest(context))
        {
            await WriteErrorAsync(context, 403, AgentErrorCodes.Forbidden, "Only literal loopback requests are allowed.");
            return;
        }
        if (!string.Equals(context.Request.Host.Value, _expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, 403, AgentErrorCodes.Forbidden, "The Host header is not allowed.");
            return;
        }
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !string.Equals(origin, _expectedOrigin, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, 403, AgentErrorCodes.Forbidden, "The Origin header is not allowed.");
            return;
        }
        if (!string.Equals(context.Request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            context.Response.Headers["Allow"] = "POST";
            await WriteErrorAsync(context, 405, AgentErrorCodes.MethodNotAllowed, "Only POST is allowed.");
            return;
        }
        if (!HasJsonContentType(context.Request))
        {
            await WriteErrorAsync(
                context,
                415,
                AgentErrorCodes.UnsupportedMediaType,
                "Content-Type must be application/json.");
            return;
        }
        if (context.Request.ContentLength is null)
        {
            await WriteErrorAsync(context, 411, AgentErrorCodes.LengthRequired, "Content-Length is required.");
            return;
        }
        if (context.Request.ContentLength > AgentApiContract.MaxRequestBytes)
        {
            await WriteErrorAsync(
                context,
                413,
                AgentErrorCodes.RequestTooLarge,
                "The request body is too large.");
            return;
        }
        if (!HasValidBearerToken(context.Request))
        {
            await WriteErrorAsync(context, 401, AgentErrorCodes.Unauthorized, "Authentication failed.");
            return;
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _requestLifetime.Token);
        byte[] body;
        try
        {
            body = await ReadBodyAsync(context.Request, requestCancellation.Token);
        }
        catch (TimeoutException)
        {
            await WriteErrorAsync(
                context,
                408,
                AgentErrorCodes.RequestTimeout,
                "Reading the request body timed out.");
            return;
        }
        catch (AgentApiException exception) when (exception.Code == AgentErrorCodes.RequestTooLarge)
        {
            await WriteErrorAsync(
                context,
                413,
                AgentErrorCodes.RequestTooLarge,
                exception.Message);
            return;
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            context.Abort();
            return;
        }

        AgentToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AgentToolRequest>(body, AgentJson.Options);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                context,
                400,
                AgentErrorCodes.InvalidArgument,
                "The request body must be a JSON object.");
            return;
        }
        if (request is null)
        {
            await WriteErrorAsync(
                context,
                400,
                AgentErrorCodes.InvalidArgument,
                "The request body must be a JSON object.");
            return;
        }

        var tool = path[AgentApiContract.ToolsPathPrefix.Length..];
        try
        {
            var runtime = _runtimeProvider();
            var result = await DispatchAsync(tool, request, runtime, requestCancellation.Token);
            if (IsMutationTool(tool))
            {
                NotifyRecordChanged();
            }
            await WriteJsonAsync(context, 200, new { ok = true, tool, result });
        }
        catch (AgentValidationException exception)
        {
            await WriteErrorAsync(context, 400, exception.Code, exception.Message);
        }
        catch (RecordAutomationException exception) when (exception.Kind == RecordAutomationFailureKind.Validation)
        {
            await WriteErrorAsync(context, 400, AgentErrorCodes.InvalidArgument, exception.Message);
        }
        catch (RecordAutomationException exception) when (exception.Kind == RecordAutomationFailureKind.NotFound)
        {
            await WriteErrorAsync(context, 404, AgentErrorCodes.NotFound, exception.Message);
        }
        catch (RecordAutomationException exception) when (exception.Kind == RecordAutomationFailureKind.Conflict)
        {
            await WriteErrorAsync(context, 409, AgentErrorCodes.Conflict, exception.Message);
        }
        catch (AgentApiException exception)
        {
            var statusCode = exception.Code == AgentErrorCodes.NotFound ? 404 : 500;
            await WriteErrorAsync(context, statusCode, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception)
        {
            await WriteErrorAsync(
                context,
                500,
                AgentErrorCodes.InternalError,
                "The UnpackVision agent API could not complete the request.");
        }
    }

    private async Task<object> DispatchAsync(
        string tool,
        AgentToolRequest request,
        RecordAutomationRuntime runtime,
        CancellationToken cancellationToken) =>
        tool switch
        {
            AgentToolNames.GetStatus => await _service.GetStatusAsync(runtime, cancellationToken),
            AgentToolNames.ListRecords => await ListRecordsAsync(request, cancellationToken),
            AgentToolNames.GetRecord => await GetRecordAsync(request, cancellationToken),
            AgentToolNames.ListTags => await _service.ListTagsAsync(cancellationToken),
            AgentToolNames.SetRecordNote => await SetRecordNoteAsync(
                request,
                currentRecordId: null,
                cancellationToken),
            AgentToolNames.AddRecordTag => await AddRecordTagAsync(
                request,
                currentRecordId: null,
                cancellationToken),
            AgentToolNames.RemoveRecordTag => await RemoveRecordTagAsync(
                request,
                currentRecordId: null,
                cancellationToken),
            AgentToolNames.RetryExcelSync => await _service.RetryExcelSyncAsync(cancellationToken),
            _ => throw new AgentApiException(AgentErrorCodes.NotFound, "Unknown agent tool.")
        };

    private static bool IsMutationTool(string tool) =>
        tool is AgentToolNames.SetRecordNote or
            AgentToolNames.AddRecordTag or
            AgentToolNames.RemoveRecordTag or
            AgentToolNames.RetryExcelSync;

    private void NotifyRecordChanged()
    {
        try
        {
            _recordChanged?.Invoke();
        }
        catch (Exception)
        {
            // Refresh is a UI convenience; it must never turn a committed
            // write into an API failure.
        }
    }

    private async Task<RecordListPage> ListRecordsAsync(
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!AgentInputRules.TryNormalizeTrackingNumber(request.TrackingNo, out var trackingNo, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryNormalizeLimit(request.Limit, out var limit, out error))
        {
            throw new AgentValidationException(error!);
        }
        return await _service.ListRecordsAsync(trackingNo, limit, cancellationToken);
    }

    private async Task<RecordDetailView> GetRecordAsync(
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!AgentInputRules.TryParseRecordId(request.RecordId, out var recordId, out var error))
        {
            throw new AgentValidationException(error!);
        }
        return await _service.GetRecordAsync(recordId, cancellationToken);
    }

    private async Task<NoteWriteResult> SetRecordNoteAsync(
        AgentToolRequest request,
        Guid? currentRecordId,
        CancellationToken cancellationToken)
    {
        if (!AgentInputRules.TryParseRecordId(request.RecordId, out var recordId, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryNormalizeNote(request.Note, out var note, out error))
        {
            throw new AgentValidationException(error!);
        }
        return await _service.SetRecordNoteAsync(
            recordId,
            note,
            cancellationToken,
            currentRecordId);
    }

    private async Task<TagWriteResult> AddRecordTagAsync(
        AgentToolRequest request,
        Guid? currentRecordId,
        CancellationToken cancellationToken)
    {
        if (!AgentInputRules.TryParseRecordId(request.RecordId, out var recordId, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryNormalizeTagId(request.TagId, out var tagId, out error))
        {
            throw new AgentValidationException(error!);
        }
        return await _service.AddRecordTagAsync(
            recordId,
            tagId,
            cancellationToken,
            currentRecordId);
    }

    private async Task<TagWriteResult> RemoveRecordTagAsync(
        AgentToolRequest request,
        Guid? currentRecordId,
        CancellationToken cancellationToken)
    {
        if (!AgentInputRules.TryParseRecordId(request.RecordId, out var recordId, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryParseAssignmentId(request.AssignmentId, out var assignmentId, out error))
        {
            throw new AgentValidationException(error!);
        }
        return await _service.RemoveRecordTagAsync(
            recordId,
            assignmentId,
            cancellationToken,
            currentRecordId);
    }

    private bool IsLoopbackRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is { AddressFamily: AddressFamily.InterNetwork } remote &&
        remote.Equals(IPAddress.Loopback);

    private bool HasValidBearerToken(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(_token);
        var actual = Encoding.UTF8.GetBytes(header.Parameter);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestBodyTimeout);
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await request.Body.ReadAsync(chunk, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > AgentApiContract.MaxRequestBytes)
                {
                    throw new AgentApiException(
                        AgentErrorCodes.RequestTooLarge,
                        "The request body is too large.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The request body was not received in time.");
        }
    }

    private static bool HasJsonContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (contentType.CharSet is not { } charset)
        {
            return true;
        }

        return string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrepareResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        await WriteJsonAsync(context, statusCode, new
        {
            ok = false,
            error = new { code, message }
        });
    }

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, object value)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            AgentJson.Options,
            context.RequestAborted);
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class NoopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
