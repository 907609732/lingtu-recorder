using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Agent;

/// <summary>
/// MCP is a thin adapter over the same <see cref="AgentToolOperations"/> used by
/// the CLI. Tool discovery is reflection-only and therefore works while the
/// desktop is offline.
/// </summary>
[McpServerToolType]
public sealed class UnpackVisionAgentTools(AgentToolOperations operations)
{
    [McpServerTool(
        Name = AgentToolNames.GetStatus,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("Return desktop readiness, recording state and Excel queue semantics. Does not start the desktop app.")]
    public Task<CallToolResult> GetStatus(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.GetStatusAsync(cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.ListRecords,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("List recent records. limit is 1..200; hasMore means matching records were not returned.")]
    public Task<CallToolResult> ListRecords(
        [Description("Optional tracking-number contains filter.")] string? trackingNo = null,
        [Description("Maximum rows to return, 1..200. Default 50.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.ListRecordsAsync(trackingNo, limit, cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.GetRecord,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("Return one record with its owned media paths, tags and latest Excel delivery state.")]
    public Task<CallToolResult> GetRecord(
        [Description("Record GUID.")] string recordId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.GetRecordAsync(recordId, cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.ListTags,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("List configured issue tag definitions. No credentials are returned.")]
    public Task<CallToolResult> ListTags(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.ListTagsAsync(cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.SetRecordNote,
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("Set a note on a completed/imported record and enqueue Excel sync. Queued is not synced.")]
    public Task<CallToolResult> SetRecordNote(
        [Description("Record GUID.")] string recordId,
        [Description("Note text, at most 2000 characters.")] string note,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.SetRecordNoteAsync(recordId, note, cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.AddRecordTag,
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("Add an enabled tag to a completed/imported record and enqueue Excel sync.")]
    public Task<CallToolResult> AddRecordTag(
        [Description("Record GUID.")] string recordId,
        [Description("Configured tag id.")] string tagId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.AddRecordTagAsync(recordId, tagId, cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.RemoveRecordTag,
        ReadOnly = false,
        Idempotent = false,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("Remove an active tag assignment from a completed/imported record and enqueue Excel sync.")]
    public Task<CallToolResult> RemoveRecordTag(
        [Description("Record GUID.")] string recordId,
        [Description("Tag assignment GUID from get_record.")] string assignmentId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.RemoveRecordTagAsync(recordId, assignmentId, cancellationToken));

    [McpServerTool(
        Name = AgentToolNames.RetryExcelSync,
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true)]
    [Description("Requeue all Excel deliveries that have not succeeded. It does not force a workbook write.")]
    public Task<CallToolResult> RetryExcelSync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => operations.RetryExcelSyncAsync(cancellationToken));

    private static async Task<CallToolResult> ExecuteAsync(Func<Task<JsonElement>> action)
    {
        try
        {
            var result = await action();
            return Success(result);
        }
        catch (AgentException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return Failure(AgentErrorCodes.InternalError, "The agent request was cancelled.");
        }
        catch (Exception)
        {
            return Failure(
                AgentErrorCodes.InternalError,
                "The UnpackVision agent tool could not complete the request.");
        }
    }

    private static CallToolResult Success(JsonElement result) => new()
    {
        Content = [new TextContentBlock { Text = result.GetRawText() }],
        StructuredContent = result,
        IsError = false
    };

    private static CallToolResult Failure(string code, string message)
    {
        var error = JsonSerializer.SerializeToElement(new
        {
            ok = false,
            error = new { code, message }
        }, AgentJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = error.GetRawText() }],
            StructuredContent = error,
            IsError = true
        };
    }
}
