using System.Net;

namespace UnpackVision.Service;

/// <summary>Confines the full-trust desktop file API to the owning machine.</summary>
public sealed class LocalApiBoundary(RequestDelegate next)
{
    public static string[] ValidateUrls(string? value)
    {
        var urls = (value ?? "http://127.0.0.1:5179").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0 || urls.Any(url =>
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !IsLoopbackHost(uri.Host) || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0))
        {
            throw new InvalidOperationException("兼容服务只允许回环监听，不支持网络或反向代理部署。");
        }
        return urls;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !IsLoopbackAddress(peer) || !IsLoopbackHost(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopbackAddress(address);

    private static bool IsLoopbackAddress(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
