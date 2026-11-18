using UnpackVision.Core;

namespace UnpackVision.Application.Agent;

public sealed record RecordAutomationRuntime(
    bool DesktopReady,
    bool DatabaseReady,
    RecordingState RecordingState,
    Guid? CurrentRecordId,
    string? CurrentTrackingNo,
    string AppVersion,
    DateTimeOffset? InitializedAt)
{
    public static RecordAutomationRuntime NotReady { get; } = new(
        DesktopReady: false,
        DatabaseReady: false,
        RecordingState: RecordingState.Idle,
        CurrentRecordId: null,
        CurrentTrackingNo: null,
        AppVersion: "unknown",
        InitializedAt: null);
}

public enum RecordAutomationFailureKind
{
    Validation,
    NotFound,
    Conflict
}

public sealed class RecordAutomationException(
    RecordAutomationFailureKind kind,
    string message) : Exception(message)
{
    public RecordAutomationFailureKind Kind { get; } = kind;
}

public sealed record AgentStatusView(
    string ApiVersion,
    bool DesktopReady,
    bool DatabaseReady,
    string AppVersion,
    DateTimeOffset ServerTime,
    DateTimeOffset? DesktopInitializedAt,
    RecordingStatusView Recording,
    ExcelSyncSemantics ExcelSync,
    AgentScopeView Scope);

public sealed record RecordingStatusView(
    string State,
    Guid? RecordId,
    string? TrackingNo);

public sealed record ExcelSyncSemantics(
    string ConnectorId,
    bool QueuedIsSynced,
    bool WritesEnqueueDelivery,
    string RetryScope,
    IReadOnlyList<string> DeliveryStates);

public sealed record AgentScopeView(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> ExcludedCapabilities);

public sealed record RecordListPage(
    int Returned,
    int Limit,
    bool HasMore,
    string? TrackingNo,
    IReadOnlyList<RecordSummaryView> Records);

public sealed record RecordSummaryView(
    Guid RecordId,
    string TrackingNo,
    string Workflow,
    string State,
    DateTimeOffset ScannedAt,
    DateTimeOffset? RecordingEndedAt,
    string? VideoPath,
    string Note,
    IReadOnlyList<RecordTagView> Tags,
    RecordSyncView? ExcelSync);

public sealed record RecordDetailView(
    Guid RecordId,
    string TrackingNo,
    string Workflow,
    string State,
    DateTimeOffset ScannedAt,
    DateTimeOffset? RecordingStartedAt,
    DateTimeOffset? RecordingEndedAt,
    string? VideoPath,
    string Note,
    DateTimeOffset? NoteUpdatedAt,
    string MediaIntegrity,
    IReadOnlyList<string> Snapshots,
    IReadOnlyList<RecordTagView> Tags,
    IReadOnlyList<RecordMediaAssetView> MediaAssets,
    IReadOnlyList<RecordMediaGapView> MediaGaps,
    RecordSyncView? ExcelSync);

public sealed record RecordTagView(
    Guid AssignmentId,
    string TagId,
    string Name,
    string ColorHex,
    DateTimeOffset TaggedAt,
    string Source);

public sealed record RecordMediaAssetView(
    Guid AssetId,
    string CameraId,
    string DisplayName,
    string Role,
    string VideoPath,
    string RelativeVideoPath,
    string Codec,
    string EncoderName,
    bool HardwareAccelerated,
    int Width,
    int Height,
    double FramesPerSecond,
    TimeSpan StartOffset,
    TimeSpan Duration,
    string Integrity,
    IReadOnlyList<RecordMediaSegmentView> Segments);

public sealed record RecordMediaSegmentView(
    Guid SegmentId,
    int Sequence,
    string VideoPath,
    TimeSpan StartOffset,
    TimeSpan Duration,
    bool Recovered);

public sealed record RecordMediaGapView(
    Guid GapId,
    Guid? MediaAssetId,
    string CameraId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool Recovered);

public sealed record RecordSyncView(
    string ConnectorId,
    string Status,
    bool Succeeded,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset? UpdatedAt);

public sealed record TagCatalogView(
    int Count,
    IReadOnlyList<TagDefinitionView> Tags);

public sealed record TagDefinitionView(
    string TagId,
    string Name,
    string ColorHex,
    string BarcodeValue,
    bool Enabled,
    int SortOrder);

public sealed record NoteWriteResult(
    Guid RecordId,
    bool Changed,
    string Note,
    DateTimeOffset? NoteUpdatedAt,
    RecordSyncView? ExcelSync,
    string SyncSemantics);

public sealed record TagWriteResult(
    Guid RecordId,
    bool Changed,
    RecordTagView? Assignment,
    IReadOnlyList<RecordTagView> ActiveTags,
    RecordSyncView? ExcelSync,
    string SyncSemantics);

public sealed record RetrySyncResult(
    string ConnectorId,
    bool Queued,
    string Scope,
    string SyncSemantics);
