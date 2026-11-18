namespace UnpackVision.Agent.Protocol;

/// <summary>
/// The CLI, MCP adapter and desktop HTTP host all pass through these rules.
/// Arguments are deliberately validated before any connection file is read so
/// that help/schema and obviously invalid requests never touch user state.
/// </summary>
public static class AgentInputRules
{
    public static bool TryParseRecordId(string? value, out Guid recordId, out string? error) =>
        TryParseId(value, "recordId", out recordId, out error);

    public static bool TryParseAssignmentId(string? value, out Guid assignmentId, out string? error) =>
        TryParseId(value, "assignmentId", out assignmentId, out error);

    public static bool TryNormalizeTagId(string? value, out string tagId, out string? error)
    {
        tagId = (value ?? string.Empty).Trim();
        if (tagId.Length == 0)
        {
            error = "tagId is required.";
            return false;
        }
        if (tagId.Length > AgentApiContract.MaxTagIdLength)
        {
            error = $"tagId must be {AgentApiContract.MaxTagIdLength} characters or fewer.";
            return false;
        }
        if (tagId.Any(char.IsControl))
        {
            error = "tagId must not contain control characters.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryNormalizeTrackingNumber(string? value, out string? trackingNo, out string? error)
    {
        trackingNo = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trackingNo is not null && trackingNo.Length > AgentApiContract.MaxTrackingNumberLength)
        {
            error = $"trackingNo must be {AgentApiContract.MaxTrackingNumberLength} characters or fewer.";
            return false;
        }
        if (trackingNo is not null && trackingNo.Any(char.IsControl))
        {
            error = "trackingNo must not contain control characters.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryNormalizeNote(string? value, out string note, out string? error)
    {
        if (value is null)
        {
            note = string.Empty;
            error = "note is required. Use an explicit empty string to clear the note.";
            return false;
        }

        note = value;
        if (note.Length > AgentApiContract.MaxNoteLength)
        {
            error = $"note must be {AgentApiContract.MaxNoteLength} characters or fewer.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryNormalizeLimit(int? value, out int limit, out string? error)
    {
        limit = value ?? AgentApiContract.DefaultListLimit;
        if (limit is < 1 or > AgentApiContract.MaxListLimit)
        {
            error = $"limit must be between 1 and {AgentApiContract.MaxListLimit}.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseId(string? value, string fieldName, out Guid id, out string? error)
    {
        if (!Guid.TryParse(value, out id) || id == Guid.Empty)
        {
            error = $"{fieldName} must be a non-empty GUID.";
            return false;
        }

        error = null;
        return true;
    }
}
