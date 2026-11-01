using System.Net;
using Microsoft.AspNetCore.Http;
using UnpackVision.Service;

namespace UnpackVision.Tests;

public sealed class LocalApiBoundaryTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5189")]
    [InlineData("http://localhost:5179;http://[::1]:5180")]
    public void Local_ports_remain_configurable(string urls) =>
        Assert.NotEmpty(LocalApiBoundary.ValidateUrls(urls));

    [Theory]
    [InlineData("http://0.0.0.0:5179")]
    [InlineData("http://*:5179")]
    [InlineData("http://192.168.1.2:5179")]
    [InlineData("http://example.com:5179")]
    [InlineData("http://127.0.0.1:5179;http://[::]:5179")]
    public void Network_listener_overrides_are_rejected(string urls) =>
        Assert.Throws<InvalidOperationException>(() => LocalApiBoundary.ValidateUrls(urls));

    [Theory]
    [InlineData("127.0.0.1", "localhost", true)]
    [InlineData("::1", "[::1]", true)]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", true)]
    [InlineData("192.168.1.2", "localhost", false)]
    [InlineData("127.0.0.1", "attacker.example", false)]
    [InlineData(null, "localhost", false)]
    public async Task Middleware_rejects_network_peers_and_rebound_hosts(string? peer, string host, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        context.Request.Host = new HostString(host, 5179);
        var called = false;
        var middleware = new LocalApiBoundary(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        Assert.Equal(allowed, called);
        if (!allowed) Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
