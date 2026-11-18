using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UnpackVision.Agent.Protocol;

/// <summary>
/// Shared CLI/MCP transport. The connection file is reread for every call so a
/// desktop restart rotates the port and bearer token without restarting the
/// agent process. Redirects, proxies, cookies and automatic retries are
/// deliberately disabled: writes must never be replayed to an unknown target.
/// </summary>
public sealed class AgentApiClient
{
    private readonly string? _connectionPath;
    private readonly HttpMessageHandler _handler;
    private readonly TimeSpan _timeout;

    public AgentApiClient(
        string? connectionPath = null,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        // Resolution is deferred until a tool call. Help/schema and MCP
        // discovery must remain usable even when no connection file exists.
        _connectionPath = connectionPath;
        _handler = handler ?? CreateSecureHandler();
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public static SocketsHttpHandler CreateSecureHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    };

    public async Task<AgentToolCallResult> CallToolAsync(
        string tool,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!AgentToolNames.All.Contains(tool, StringComparer.Ordinal))
        {
            throw new AgentValidationException("Unknown agent tool.");
        }

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(_timeout);
        var operationToken = operationTimeout.Token;
        var connection = await AgentConnectionFile.ReadAsync(
            ResolveConnectionPath(),
            operationToken);
        var baseUri = new Uri(connection.BaseUrl, UriKind.Absolute);
        var requestUri = new Uri(baseUri, $"{AgentApiContract.ToolsPathPrefix.TrimStart('/')}{tool}");
        if (!AgentConnectionFile.TryNormalizeLoopbackBaseUrl(
                $"{requestUri.Scheme}://{requestUri.Host}:{requestUri.Port}/",
                out _,
                out _))
        {
            throw new AgentUnavailableException(
                "The UnpackVision agent connection is not a literal loopback address.");
        }

        using var http = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Options));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            using var response = await http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                operationToken);
            var bytes = await ReadLimitedAsync(response.Content, operationToken);
            var result = ParseResponse(response.StatusCode, bytes);
            return result;
        }
        catch (AgentException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AgentUnavailableException(
                "The UnpackVision desktop did not respond before the agent timeout.");
        }
        catch (HttpRequestException exception)
        {
            throw new AgentUnavailableException(
                "UnpackVision desktop is not running or has not finished initialization.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException(
                AgentErrorCodes.UnexpectedResponse,
                "The UnpackVision agent returned an invalid response.",
                innerException: exception);
        }
    }

    private string ResolveConnectionPath()
    {
        try
        {
            return AgentConnectionFile.ResolvePath(_connectionPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentUnavailableException(
                "The UnpackVision agent connection path is invalid.",
                exception);
        }
    }

    private static AgentToolCallResult ParseResponse(HttpStatusCode statusCode, byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException(
                AgentErrorCodes.UnexpectedResponse,
                "The UnpackVision agent returned an invalid response.",
                statusCode,
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AgentApiException(
                    AgentErrorCodes.UnexpectedResponse,
                    "The UnpackVision agent returned an invalid response.",
                    statusCode);
            }

            if (root.TryGetProperty("ok", out var okElement) &&
                okElement.ValueKind == JsonValueKind.False)
            {
                var error = ReadError(root);
                throw new AgentApiException(
                    error.Code,
                    error.Message,
                    statusCode);
            }

            if (!IsSuccess(statusCode) ||
                !root.TryGetProperty("ok", out okElement) ||
                okElement.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("tool", out var toolElement) ||
                !root.TryGetProperty("result", out var resultElement))
            {
                var error = TryReadError(root) ?? new AgentErrorEnvelope(
                    statusCode == HttpStatusCode.Unauthorized
                        ? AgentErrorCodes.Unauthorized
                        : AgentErrorCodes.UnexpectedResponse,
                    "The UnpackVision agent returned an unexpected response.");
                throw new AgentApiException(error.Code, error.Message, statusCode);
            }

            return new AgentToolCallResult(
                toolElement.GetString() ?? string.Empty,
                resultElement.Clone());
        }
    }

    private static AgentErrorEnvelope ReadError(JsonElement root) =>
        TryReadError(root) ?? new AgentErrorEnvelope(
            AgentErrorCodes.UnexpectedResponse,
            "The UnpackVision agent returned an unexpected response.");

    private static AgentErrorEnvelope? TryReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.String ||
            !error.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var codeValue = code.GetString();
        var messageValue = message.GetString();
        if (string.IsNullOrWhiteSpace(codeValue) || string.IsNullOrWhiteSpace(messageValue))
        {
            return null;
        }

        return new AgentErrorEnvelope(
            codeValue.Length > 64 ? AgentErrorCodes.UnexpectedResponse : codeValue,
            messageValue.Length > 1000 ? messageValue[..1000] : messageValue);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > AgentApiContract.MaxResponseBytes)
        {
            throw new AgentApiException(
                AgentErrorCodes.UnexpectedResponse,
                "The UnpackVision agent response was too large.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > AgentApiContract.MaxResponseBytes)
            {
                throw new AgentApiException(
                    AgentErrorCodes.UnexpectedResponse,
                    "The UnpackVision agent response was too large.");
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsSuccess(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and <= 299;
}
