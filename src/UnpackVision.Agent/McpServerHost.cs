using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Agent;

public static class McpServerHost
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder([]);
        // MCP stdout is reserved for JSON-RPC framing. Clearing providers also
        // avoids the console logger accidentally writing a line into the stream.
        builder.Logging.ClearProviders();
        var operations = new AgentToolOperations(new AgentApiClient());
        var tools = new UnpackVisionAgentTools(operations);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools(tools);
        await builder.Build().RunAsync(cancellationToken);
    }
}
