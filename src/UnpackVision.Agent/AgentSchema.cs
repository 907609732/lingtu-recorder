using System.Text.Json;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Agent;

/// <summary>
/// Offline description of the CLI/MCP surface. It intentionally contains no
/// connection details and is safe to call while the desktop is stopped.
/// </summary>
public static class AgentSchema
{
    public static string CreateJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        program = "UnpackVision.Agent",
        transport = new
        {
            cli = "JSON on stdout; diagnostics are not written to stdout",
            mcp = "local stdio only; initialize/listTools do not read the desktop connection file",
            desktop = "UnpackVision must already be running and finished initialization for tool calls"
        },
        connection = new
        {
            defaultPath = @"%LOCALAPPDATA%\UnpackVision\ai\connection.json",
            environmentVariable = AgentApiContract.ConnectionPathEnvironmentVariable,
            loopbackOnly = true,
            proxies = false,
            redirects = false,
            automaticRetryForWrites = false
        },
        exitCodes = new object[]
        {
            new { code = AgentExitCodes.Success, meaning = "success" },
            new { code = AgentExitCodes.UsageOrValidation, meaning = "usage, validation or invalid argument" },
            new { code = AgentExitCodes.Unavailable, meaning = "desktop not running or not initialized" },
            new { code = AgentExitCodes.Unauthorized, meaning = "stale or incorrect connection token" },
            new { code = AgentExitCodes.NotFound, meaning = "record, tag or assignment not found" },
            new { code = AgentExitCodes.Conflict, meaning = "record is not completed/imported or state conflict" },
            new { code = AgentExitCodes.ServerError, meaning = "transport or unexpected server response" },
            new { code = AgentExitCodes.Unexpected, meaning = "unexpected local failure" }
        },
        scope = new
        {
            reads = new[] { "status", "records", "owned media paths", "tags" },
            writes = new[]
            {
                "note on completed/imported records",
                "tags on completed/imported records",
                "retry all non-succeeded Excel deliveries"
            },
            excluded = new[]
            {
                "start or stop recording",
                "delete records",
                "pair or unpair devices",
                "arbitrary shell command",
                "arbitrary file read",
                "direct Excel workbook write"
            },
            syncSemantics = "Enqueuing an Excel delivery does not mean the workbook was updated."
        },
        tools = new object[]
        {
            Tool(
                AgentToolNames.GetStatus,
                "get-status",
                "Return desktop readiness, recording state and Excel queue semantics.",
                [],
                readOnly: true),
            Tool(
                AgentToolNames.ListRecords,
                "list-records",
                "List recent records. limit is 1..200 and hasMore means matching records were not returned.",
                [
                    Argument("trackingNo", "string", required: false, "Optional tracking-number contains filter."),
                    Argument("limit", "integer", required: false, "Maximum rows to return, 1..200. Default 50.")
                ],
                readOnly: true),
            Tool(
                AgentToolNames.GetRecord,
                "get-record",
                "Return one record with its owned media paths, tags and latest Excel delivery state.",
                [Argument("recordId", "string", required: true, "Record GUID.")],
                readOnly: true),
            Tool(
                AgentToolNames.ListTags,
                "list-tags",
                "List configured issue tag definitions. No credentials are returned.",
                [],
                readOnly: true),
            Tool(
                AgentToolNames.SetRecordNote,
                "set-record-note",
                "Set a note on a completed/imported record and enqueue Excel sync.",
                [
                    Argument("recordId", "string", required: true, "Record GUID."),
                    Argument("note", "string", required: true, "Note text, at most 2000 characters.")
                ],
                readOnly: false),
            Tool(
                AgentToolNames.AddRecordTag,
                "add-record-tag",
                "Add an enabled tag to a completed/imported record and enqueue Excel sync.",
                [
                    Argument("recordId", "string", required: true, "Record GUID."),
                    Argument("tagId", "string", required: true, "Configured tag id.")
                ],
                readOnly: false),
            Tool(
                AgentToolNames.RemoveRecordTag,
                "remove-record-tag",
                "Remove an active tag assignment from a completed/imported record and enqueue Excel sync.",
                [
                    Argument("recordId", "string", required: true, "Record GUID."),
                    Argument("assignmentId", "string", required: true, "Tag assignment GUID from get_record.")
                ],
                readOnly: false),
            Tool(
                AgentToolNames.RetryExcelSync,
                "retry-excel-sync",
                "Requeue all Excel deliveries that have not succeeded. It does not force a workbook write.",
                [],
                readOnly: false)
        }
    }, AgentJson.Options);

    private static object Tool(
        string name,
        string cli,
        string description,
        IReadOnlyList<object> arguments,
        bool readOnly) => new
    {
        name,
        cli,
        description,
        arguments,
        effects = new
        {
            readOnly,
            writesExcelQueue = !readOnly,
            queuedDoesNotMeanSynced = !readOnly,
            recordingControl = false,
            recordDeletion = false,
            devicePairing = false,
            shellExecution = false,
            arbitraryFileRead = false
        }
    };

    private static object Argument(
        string name,
        string type,
        bool required,
        string description) => new
    {
        name,
        type,
        required,
        description
    };
}
