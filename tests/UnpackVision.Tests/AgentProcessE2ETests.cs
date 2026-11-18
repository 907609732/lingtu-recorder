using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using UnpackVision.Agent.Protocol;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class AgentProcessE2ETests
{
    [Fact]
    public async Task CliHelpAndSchema_DoNotConnectOrCreateConnectionFile()
    {
        using var directory = new TemporaryDirectory();
        var connectionPath = Path.Combine(directory.Path, "connection.json");

        var help = await AgentProcess.RunAsync(connectionPath, "--help");
        var schema = await AgentProcess.RunAsync(connectionPath, "schema");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("get-status", help.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, schema.ExitCode);
        using var schemaJson = schema.ParseJson();
        Assert.Equal(8, schemaJson.RootElement.GetProperty("tools").GetArrayLength());
        Assert.False(File.Exists(connectionPath));
        Assert.DoesNotContain("\"token\"", schema.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_ReadCommandsUseStructuredJsonAndExitZero()
    {
        await using var host = await AgentTestHost.StartAsync();
        var record = host.Repository.Records[0];

        var status = await AgentProcess.RunAsync(host.ConnectionPath, "get-status");
        var list = await AgentProcess.RunAsync(host.ConnectionPath, "list-records", "--limit", "1");
        var detail = await AgentProcess.RunAsync(host.ConnectionPath, "get-record", "--record-id", record.Id.ToString("D"));
        var tags = await AgentProcess.RunAsync(host.ConnectionPath, "list-tags");

        Assert.Equal(0, status.ExitCode);
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(0, detail.ExitCode);
        Assert.Equal(0, tags.ExitCode);
        using var statusJson = status.ParseJson();
        using var listJson = list.ParseJson();
        using var detailJson = detail.ParseJson();
        using var tagsJson = tags.ParseJson();
        Assert.True(statusJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("get_status", statusJson.RootElement.GetProperty("tool").GetString());
        Assert.Equal(1, listJson.RootElement.GetProperty("result").GetProperty("returned").GetInt32());
        Assert.Equal(record.Id, detailJson.RootElement.GetProperty("result").GetProperty("recordId").GetGuid());
        Assert.True(tagsJson.RootElement.GetProperty("result").GetProperty("count").GetInt32() >= 1);
        Assert.DoesNotContain("token", status.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, status.StandardError);
        Assert.Equal(string.Empty, list.StandardError);
    }

    [Fact]
    public async Task Cli_WriteCommandsReturnJsonAndQueueExcelWork()
    {
        await using var host = await AgentTestHost.StartAsync();
        var record = host.Repository.Records[0];

        var note = await AgentProcess.RunAsync(
            host.ConnectionPath,
            "set-record-note",
            "--record-id",
            record.Id.ToString("D"),
            "--note",
            "CLI synthetic note");
        var add = await AgentProcess.RunAsync(
            host.ConnectionPath,
            "add-record-tag",
            "--record-id",
            record.Id.ToString("D"),
            "--tag-id",
            "DAMAGE01");
        using var addJson = add.ParseJson();
        var assignmentId = addJson.RootElement
            .GetProperty("result")
            .GetProperty("assignment")
            .GetProperty("assignmentId")
            .GetGuid();
        var remove = await AgentProcess.RunAsync(
            host.ConnectionPath,
            "remove-record-tag",
            "--record-id",
            record.Id.ToString("D"),
            "--assignment-id",
            assignmentId.ToString("D"));
        var retry = await AgentProcess.RunAsync(host.ConnectionPath, "retry-excel-sync");

        Assert.Equal(0, note.ExitCode);
        Assert.Equal(0, add.ExitCode);
        Assert.Equal(0, remove.ExitCode);
        Assert.Equal(0, retry.ExitCode);
        using var noteJson = note.ParseJson();
        using var removeJson = remove.ParseJson();
        using var retryJson = retry.ParseJson();
        Assert.True(noteJson.RootElement.GetProperty("result").GetProperty("changed").GetBoolean());
        Assert.True(removeJson.RootElement.GetProperty("result").GetProperty("changed").GetBoolean());
        Assert.True(retryJson.RootElement.GetProperty("result").GetProperty("queued").GetBoolean());
        Assert.Contains(host.Repository.Deliveries, delivery =>
            delivery.ConnectorId == "excel" && delivery.Status == SyncStatus.Pending);
    }

    [Fact]
    public async Task CliParserErrors_AreStructuredJsonWithExitOne()
    {
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "missing.json");
        var cases = new[]
        {
            new[] { "unknown-command" },
            new[] { "list-records", "--limit", "nope" },
            new[] { "get-status", "--bogus" },
            new[] { "set-record-note", "--record-id", Guid.NewGuid().ToString("D") }
        };

        foreach (var arguments in cases)
        {
            var result = await AgentProcess.RunAsync(missing, arguments);
            Assert.Equal(1, result.ExitCode);
            using var json = result.ParseJson();
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                AgentErrorCodes.InvalidArgument,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(string.Empty, result.StandardError);
        }
    }

    [Fact]
    public async Task Cli_ReportsStableExitCodesForOfflineAuthInvalidIdAndOversizeNote()
    {
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "missing.json");
        var offline = await AgentProcess.RunAsync(missing, "get-status");

        await using var host = await AgentTestHost.StartAsync();
        var tampered = Path.Combine(directory.Path, "tampered.json");
        var own = await host.ReadConnectionAsync();
        await AgentConnectionFile.WriteAtomicAsync(
            tampered,
            new AgentConnectionInfo
            {
                BaseUrl = own.BaseUrl,
                Token = "wrong-token-0123456789",
                Pid = Environment.ProcessId,
                StartedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);
        var unauthorized = await AgentProcess.RunAsync(tampered, "get-status");
        // Invalid arguments are rejected before the connection file is read.
        var invalidId = await AgentProcess.RunAsync(missing, "get-record", "--record-id", "not-a-guid");
        var oversize = await AgentProcess.RunAsync(
            missing,
            "set-record-note",
            "--record-id",
            host.Repository.Records[0].Id.ToString("D"),
            "--note",
            new string('x', 2001));

        Assert.Equal(2, offline.ExitCode);
        Assert.Equal(3, unauthorized.ExitCode);
        Assert.Equal(1, invalidId.ExitCode);
        Assert.Equal(1, oversize.ExitCode);
        using var offlineJson = offline.ParseJson();
        using var unauthorizedJson = unauthorized.ParseJson();
        using var invalidIdJson = invalidId.ParseJson();
        using var oversizeJson = oversize.ParseJson();
        Assert.Equal(AgentErrorCodes.Unavailable, ErrorCode(offlineJson));
        Assert.Equal(AgentErrorCodes.Unauthorized, ErrorCode(unauthorizedJson));
        Assert.Equal(AgentErrorCodes.InvalidArgument, ErrorCode(invalidIdJson));
        Assert.Equal(AgentErrorCodes.InvalidArgument, ErrorCode(oversizeJson));
        Assert.DoesNotContain(own.Token, unauthorized.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_OfficialSdkInitializeListToolsAndCallTools()
    {
        await using var host = await AgentTestHost.StartAsync();
        var record = host.Repository.Records[0];
        var errors = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = AgentProcess.ExecutablePath,
            Arguments = ["mcp"],
            Name = "unpackvision-e2e",
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["UNPACKVISION_AGENT_CONNECTION"] = host.ConnectionPath
            },
            StandardErrorLines = line => errors.Enqueue(line)
        });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var status = await client.CallToolAsync(
            AgentToolNames.GetStatus,
            new Dictionary<string, object?>());
        var list = await client.CallToolAsync(
            AgentToolNames.ListRecords,
            new Dictionary<string, object?> { ["limit"] = 1 });
        var note = await client.CallToolAsync(
            AgentToolNames.SetRecordNote,
            new Dictionary<string, object?>
            {
                ["recordId"] = record.Id.ToString("D"),
                ["note"] = "MCP synthetic note"
            });
        var invalid = await client.CallToolAsync(
            AgentToolNames.GetRecord,
            new Dictionary<string, object?> { ["recordId"] = "not-a-guid" });

        Assert.Equal(8, tools.Count);
        Assert.Contains(tools, tool => tool.Name == AgentToolNames.GetStatus);
        Assert.Contains(tools, tool => tool.Name == AgentToolNames.RetryExcelSync);
        Assert.False(status.IsError);
        Assert.True(status.StructuredContent!.Value.GetProperty("desktopReady").GetBoolean());
        Assert.False(list.IsError);
        Assert.Equal(1, list.StructuredContent!.Value.GetProperty("returned").GetInt32());
        Assert.False(note.IsError);
        Assert.True(note.StructuredContent!.Value.GetProperty("changed").GetBoolean());
        Assert.True(invalid.IsError);
        Assert.Equal(
            AgentErrorCodes.InvalidArgument,
            invalid.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Mcp_ToolDiscoveryWorksWhileDesktopIsOffline()
    {
        using var directory = new TemporaryDirectory();
        var errors = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = AgentProcess.ExecutablePath,
            Arguments = ["mcp"],
            Name = "unpackvision-offline",
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["UNPACKVISION_AGENT_CONNECTION"] = Path.Combine(directory.Path, "missing.json")
            },
            StandardErrorLines = line => errors.Enqueue(line)
        });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var status = await client.CallToolAsync(
            AgentToolNames.GetStatus,
            new Dictionary<string, object?>());

        Assert.Equal(8, tools.Count);
        Assert.True(status.IsError);
        Assert.Equal(
            AgentErrorCodes.Unavailable,
            status.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(errors);
    }

    private static string? ErrorCode(JsonDocument document) =>
        document.RootElement.GetProperty("error").GetProperty("code").GetString();
}
