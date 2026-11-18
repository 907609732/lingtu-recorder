using System.Text.Json;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Agent;

/// <summary>
/// The single command implementation shared by the CLI commands and the MCP
/// tools. Validation happens before the connection file is read, so malformed
/// arguments never reach the desktop and help/schema stay offline.
/// </summary>
public sealed class AgentToolOperations(AgentApiClient client)
{
    public Task<JsonElement> GetStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(AgentToolNames.GetStatus, new AgentToolRequest(), cancellationToken);

    public Task<JsonElement> ListRecordsAsync(
        string? trackingNo,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        if (!AgentInputRules.TryNormalizeTrackingNumber(trackingNo, out var normalizedTrackingNo, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryNormalizeLimit(limit, out var normalizedLimit, out error))
        {
            throw new AgentValidationException(error!);
        }
        return CallAsync(
            AgentToolNames.ListRecords,
            new AgentToolRequest
            {
                TrackingNo = normalizedTrackingNo,
                Limit = normalizedLimit
            },
            cancellationToken);
    }

    public Task<JsonElement> GetRecordAsync(string? recordId, CancellationToken cancellationToken = default)
    {
        if (!AgentInputRules.TryParseRecordId(recordId, out var id, out var error))
        {
            throw new AgentValidationException(error!);
        }
        return CallAsync(
            AgentToolNames.GetRecord,
            new AgentToolRequest { RecordId = id.ToString("D") },
            cancellationToken);
    }

    public Task<JsonElement> ListTagsAsync(CancellationToken cancellationToken = default) =>
        CallAsync(AgentToolNames.ListTags, new AgentToolRequest(), cancellationToken);

    public Task<JsonElement> SetRecordNoteAsync(
        string? recordId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        if (!AgentInputRules.TryParseRecordId(recordId, out var id, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryNormalizeNote(note, out var normalizedNote, out error))
        {
            throw new AgentValidationException(error!);
        }
        return CallAsync(
            AgentToolNames.SetRecordNote,
            new AgentToolRequest
            {
                RecordId = id.ToString("D"),
                Note = normalizedNote
            },
            cancellationToken);
    }

    public Task<JsonElement> AddRecordTagAsync(
        string? recordId,
        string? tagId,
        CancellationToken cancellationToken = default)
    {
        if (!AgentInputRules.TryParseRecordId(recordId, out var id, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryNormalizeTagId(tagId, out var normalizedTagId, out error))
        {
            throw new AgentValidationException(error!);
        }
        return CallAsync(
            AgentToolNames.AddRecordTag,
            new AgentToolRequest
            {
                RecordId = id.ToString("D"),
                TagId = normalizedTagId
            },
            cancellationToken);
    }

    public Task<JsonElement> RemoveRecordTagAsync(
        string? recordId,
        string? assignmentId,
        CancellationToken cancellationToken = default)
    {
        if (!AgentInputRules.TryParseRecordId(recordId, out var id, out var error))
        {
            throw new AgentValidationException(error!);
        }
        if (!AgentInputRules.TryParseAssignmentId(assignmentId, out var assignment, out error))
        {
            throw new AgentValidationException(error!);
        }
        return CallAsync(
            AgentToolNames.RemoveRecordTag,
            new AgentToolRequest
            {
                RecordId = id.ToString("D"),
                AssignmentId = assignment.ToString("D")
            },
            cancellationToken);
    }

    public Task<JsonElement> RetryExcelSyncAsync(CancellationToken cancellationToken = default) =>
        CallAsync(AgentToolNames.RetryExcelSync, new AgentToolRequest(), cancellationToken);

    private async Task<JsonElement> CallAsync(
        string tool,
        AgentToolRequest request,
        CancellationToken cancellationToken) =>
        (await client.CallToolAsync(tool, request, cancellationToken)).Result;
}
