using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnpackVision.Agent.Protocol;

/// <summary>
/// Wire-level limits shared by the desktop host, CLI and MCP adapter. Keeping
/// these values in one place prevents one entry point from accepting arguments
/// that another entry point rejects.
/// </summary>
public static class AgentApiContract
{
    public const int Version = 1;
    public const string ToolsPathPrefix = "/v1/tools/";
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    public const int DefaultListLimit = 50;
    public const int MaxListLimit = 200;
    public const int MaxNoteLength = 2000;
    public const int MaxTrackingNumberLength = 64;
    public const int MaxTagIdLength = 64;
    public const int MaxConnectionFileBytes = 16 * 1024;
    public const string ConnectionPathEnvironmentVariable = "UNPACKVISION_AGENT_CONNECTION";
    public const string ExcelConnectorId = "excel";
}

public static class AgentToolNames
{
    public const string GetStatus = "get_status";
    public const string ListRecords = "list_records";
    public const string GetRecord = "get_record";
    public const string ListTags = "list_tags";
    public const string SetRecordNote = "set_record_note";
    public const string AddRecordTag = "add_record_tag";
    public const string RemoveRecordTag = "remove_record_tag";
    public const string RetryExcelSync = "retry_excel_sync";

    public static IReadOnlyList<string> All { get; } =
    [
        GetStatus,
        ListRecords,
        GetRecord,
        ListTags,
        SetRecordNote,
        AddRecordTag,
        RemoveRecordTag,
        RetryExcelSync
    ];
}

public static class AgentErrorCodes
{
    public const string InvalidArgument = "invalid_argument";
    public const string Unavailable = "unavailable";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string LengthRequired = "length_required";
    public const string RequestTooLarge = "request_too_large";
    public const string RequestTimeout = "request_timeout";
    public const string UnexpectedResponse = "unexpected_response";
    public const string ServerError = "server_error";
    public const string InternalError = "internal_error";
}

public sealed record AgentToolRequest
{
    public string? RecordId { get; init; }
    public string? AssignmentId { get; init; }
    public string? TagId { get; init; }
    public string? Note { get; init; }
    public string? TrackingNo { get; init; }
    public int? Limit { get; init; }
}

public sealed record AgentToolCallResult(string Tool, JsonElement Result);

public sealed record AgentErrorEnvelope(string Code, string Message);

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
