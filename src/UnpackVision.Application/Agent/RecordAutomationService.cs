using UnpackVision.Core;

namespace UnpackVision.Application.Agent;

/// <summary>
/// Record read/write use cases exposed to local agents. The class intentionally
/// depends only on Core: the desktop HTTP host, CLI and MCP adapter are edges.
/// Writable records are restricted to archived completion/import states so a
/// request can never race the in-memory recording coordinator.
/// </summary>
public sealed class RecordAutomationService
{
    public const int DefaultListLimit = 50;
    public const int MaxListLimit = 200;
    public const int MaxNoteLength = 2000;
    private const string ExcelConnectorId = "excel";
    private const string SyncSemantics =
        "A queued Excel delivery is not a completed Excel write; queued does not mean synced.";
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyList<string> ToolNames =
    [
        "get_status",
        "list_records",
        "get_record",
        "list_tags",
        "set_record_note",
        "add_record_tag",
        "remove_record_tag",
        "retry_excel_sync"
    ];

    private static readonly IReadOnlyList<string> ExcludedCapabilities =
    [
        "start_recording",
        "stop_recording",
        "delete_record",
        "delete_records",
        "pair_device",
        "unpair_device",
        "shell_command",
        "arbitrary_file_read",
        "excel_write"
    ];

    private readonly IScanRecordRepository _repository;
    private readonly Func<IReadOnlyList<IssueTagDefinition>> _tagCatalog;
    private readonly IClock _clock;
    private readonly IRecordMutationGate _mutationGate;
    private readonly Func<Guid?>? _currentRecordIdProvider;

    public RecordAutomationService(
        IScanRecordRepository repository,
        Func<IReadOnlyList<IssueTagDefinition>> tagCatalog,
        IClock clock,
        IRecordMutationGate? mutationGate = null,
        Func<Guid?>? currentRecordIdProvider = null)
    {
        _repository = repository;
        _tagCatalog = tagCatalog;
        _clock = clock;
        _mutationGate = mutationGate ?? new RecordMutationGate();
        _currentRecordIdProvider = currentRecordIdProvider;
    }

    public Task<AgentStatusView> GetStatusAsync(
        RecordAutomationRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentStatusView(
            ApiVersion: "1",
            DesktopReady: runtime.DesktopReady,
            DatabaseReady: runtime.DatabaseReady,
            AppVersion: runtime.AppVersion,
            ServerTime: _clock.Now,
            DesktopInitializedAt: runtime.InitializedAt,
            Recording: new RecordingStatusView(
                runtime.RecordingState.ToString(),
                runtime.CurrentRecordId,
                runtime.CurrentTrackingNo),
            ExcelSync: new ExcelSyncSemantics(
                ConnectorId: ExcelConnectorId,
                QueuedIsSynced: false,
                WritesEnqueueDelivery: true,
                RetryScope: "all_non_succeeded",
                DeliveryStates: ["Pending", "Processing", "Failed", "Succeeded"]),
            Scope: new AgentScopeView(ToolNames, ExcludedCapabilities)));
    }

    public async Task<RecordListPage> ListRecordsAsync(
        string? trackingNo,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var normalizedTrackingNo = string.IsNullOrWhiteSpace(trackingNo) ? null : trackingNo.Trim();
        var records = await _repository.QueryPageAsync(
            normalizedTrackingNo,
            offset: 0,
            limit: limit + 1,
            cancellationToken);
        var hasMore = records.Count > limit;
        var selected = records.Take(limit).ToArray();
        var deliveries = await _repository.GetLatestDeliveriesAsync(
            selected.Select(record => record.Id).ToArray(),
            ExcelConnectorId,
            cancellationToken);
        return new RecordListPage(
            Returned: selected.Length,
            Limit: limit,
            HasMore: hasMore,
            TrackingNo: normalizedTrackingNo,
            Records: selected.Select(record => MapSummary(
                record,
                deliveries.GetValueOrDefault(record.Id))).ToArray());
    }

    public async Task<RecordDetailView> GetRecordAsync(
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecordId(recordId);
        var record = await _repository.GetAsync(recordId, cancellationToken)
            ?? throw new RecordAutomationException(
                RecordAutomationFailureKind.NotFound,
                "Record not found.");
        var tags = await _repository.GetTagsAsync(recordId, includeRemoved: false, cancellationToken);
        var mediaAssets = await _repository.GetMediaAssetsAsync(recordId, cancellationToken);
        var mediaGaps = await _repository.GetMediaGapsAsync(recordId, cancellationToken);
        var delivery = await _repository.GetDeliveryAsync(recordId, ExcelConnectorId, cancellationToken);
        return MapDetail(record, tags, mediaAssets, mediaGaps, delivery);
    }

    public Task<TagCatalogView> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definitions = _tagCatalog()
            .OrderBy(tag => tag.SortOrder)
            .ThenBy(tag => tag.Name, StringComparer.Ordinal)
            .Select(tag => new TagDefinitionView(
                tag.Id,
                tag.Name,
                tag.ColorHex,
                tag.BarcodeValue,
                tag.Enabled,
                tag.SortOrder))
            .ToArray();
        return Task.FromResult(new TagCatalogView(definitions.Length, definitions));
    }

    public async Task<NoteWriteResult> SetRecordNoteAsync(
        Guid recordId,
        string note,
        CancellationToken cancellationToken = default,
        Guid? currentRecordId = null)
    {
        ValidateRecordId(recordId);
        if (note is null)
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Validation,
                "note is required. Use an explicit empty string to clear the note.");
        }
        if (note.Length > MaxNoteLength)
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Validation,
                $"note must be {MaxNoteLength} characters or fewer.");
        }

        using var lease = await _mutationGate.EnterAsync(cancellationToken);
        // Once the durable write begins, finish the note+enqueue pair even if
        // the HTTP client disconnects. The 10s bound prevents a stuck adapter
        // from blocking the desktop forever.
        using var persistence = new CancellationTokenSource(PersistenceTimeout);
        var persistenceToken = persistence.Token;
        var record = await GetWritableRecordAsync(recordId, currentRecordId, persistenceToken);
        var normalized = note.Trim();
        if (string.Equals(record.Note, normalized, StringComparison.Ordinal))
        {
            return new NoteWriteResult(
                recordId,
                Changed: false,
                record.Note,
                record.NoteUpdatedAt,
                await EnsureDeliveryQueuedAsync(recordId, persistenceToken),
                SyncSemantics);
        }

        var now = _clock.Now;
        await _repository.UpdateNoteAsync(recordId, normalized, now, persistenceToken);
        await _repository.EnqueueDeliveryAsync(recordId, ExcelConnectorId, persistenceToken);
        var delivery = await _repository.GetDeliveryAsync(recordId, ExcelConnectorId, persistenceToken);
        return new NoteWriteResult(
            recordId,
            Changed: true,
            normalized,
            now,
            MapSync(delivery),
            SyncSemantics);
    }

    public async Task<TagWriteResult> AddRecordTagAsync(
        Guid recordId,
        string tagId,
        CancellationToken cancellationToken = default,
        Guid? currentRecordId = null)
    {
        ValidateRecordId(recordId);
        if (string.IsNullOrWhiteSpace(tagId) || tagId.Length > 64)
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Validation,
                "tagId is required and must be 64 characters or fewer.");
        }

        var definition = _tagCatalog().FirstOrDefault(tag =>
            tag.Enabled && string.Equals(tag.Id, tagId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new RecordAutomationException(
                RecordAutomationFailureKind.NotFound,
                "Tag not found or disabled.");
        using var lease = await _mutationGate.EnterAsync(cancellationToken);
        using var persistence = new CancellationTokenSource(PersistenceTimeout);
        var persistenceToken = persistence.Token;
        _ = await GetWritableRecordAsync(recordId, currentRecordId, persistenceToken);
        var active = await _repository.GetTagsAsync(recordId, includeRemoved: false, persistenceToken);
        var existing = active.FirstOrDefault(tag =>
            string.Equals(tag.TagId, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return new TagWriteResult(
                recordId,
                Changed: false,
                MapTag(existing),
                active.Select(MapTag).ToArray(),
                await EnsureDeliveryQueuedAsync(recordId, persistenceToken),
                SyncSemantics);
        }

        var assignment = await _repository.AddTagAsync(
            recordId,
            definition,
            _clock.Now,
            source: "agent",
            persistenceToken);
        await _repository.EnqueueDeliveryAsync(recordId, ExcelConnectorId, persistenceToken);
        active = await _repository.GetTagsAsync(recordId, includeRemoved: false, persistenceToken);
        var delivery = await _repository.GetDeliveryAsync(recordId, ExcelConnectorId, persistenceToken);
        return new TagWriteResult(
            recordId,
            Changed: true,
            MapTag(assignment),
            active.Select(MapTag).ToArray(),
            MapSync(delivery),
            SyncSemantics);
    }

    public async Task<TagWriteResult> RemoveRecordTagAsync(
        Guid recordId,
        Guid assignmentId,
        CancellationToken cancellationToken = default,
        Guid? currentRecordId = null)
    {
        ValidateRecordId(recordId);
        if (assignmentId == Guid.Empty)
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Validation,
                "assignmentId must be a non-empty GUID.");
        }

        using var lease = await _mutationGate.EnterAsync(cancellationToken);
        using var persistence = new CancellationTokenSource(PersistenceTimeout);
        var persistenceToken = persistence.Token;
        _ = await GetWritableRecordAsync(recordId, currentRecordId, persistenceToken);
        var removed = await _repository.RemoveTagAsync(
            recordId,
            assignmentId,
            _clock.Now,
            persistenceToken);
        if (removed is null)
        {
            // A previous request may have removed the assignment and then lost
            // the enqueue step. Treat the same assignment as an idempotent
            // retry and repair the queue instead of returning a misleading 404.
            var includingRemoved = await _repository.GetTagsAsync(
                recordId,
                includeRemoved: true,
                persistenceToken);
            var alreadyRemoved = includingRemoved.FirstOrDefault(tag => tag.Id == assignmentId)
                ?? throw new RecordAutomationException(
                    RecordAutomationFailureKind.NotFound,
                    "Tag assignment not found.");
            var activeAfterRetry = includingRemoved.Where(tag => tag.IsActive).OrderBy(tag => tag.TaggedAt).ToArray();
            return new TagWriteResult(
                recordId,
                Changed: false,
                MapTag(alreadyRemoved),
                activeAfterRetry.Select(MapTag).ToArray(),
                await EnsureDeliveryQueuedAsync(recordId, persistenceToken),
                SyncSemantics);
        }

        await _repository.EnqueueDeliveryAsync(recordId, ExcelConnectorId, persistenceToken);
        var active = await _repository.GetTagsAsync(recordId, includeRemoved: false, persistenceToken);
        var delivery = await _repository.GetDeliveryAsync(recordId, ExcelConnectorId, persistenceToken);
        return new TagWriteResult(
            recordId,
            Changed: true,
            MapTag(removed),
            active.Select(MapTag).ToArray(),
            MapSync(delivery),
            SyncSemantics);
    }

    public async Task<RetrySyncResult> RetryExcelSyncAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _mutationGate.EnterAsync(cancellationToken);
        using var persistence = new CancellationTokenSource(PersistenceTimeout);
        await _repository.RetryConnectorAsync(ExcelConnectorId, persistence.Token);
        return new RetrySyncResult(
            ConnectorId: ExcelConnectorId,
            Queued: true,
            Scope: "all_non_succeeded",
            SyncSemantics);
    }

    private async Task<RecordSyncView?> EnsureDeliveryQueuedAsync(
        Guid recordId,
        CancellationToken cancellationToken)
    {
        // Succeeded may describe the version before a partially committed
        // mutation. Without delivery versioning, even an unchanged retry must
        // enqueue the current record rather than trust that older status.
        await _repository.EnqueueDeliveryAsync(recordId, ExcelConnectorId, cancellationToken);
        var delivery = await _repository.GetDeliveryAsync(recordId, ExcelConnectorId, cancellationToken);
        return MapSync(delivery);
    }

    private async Task<ScanRecord> GetWritableRecordAsync(
        Guid recordId,
        Guid? currentRecordId,
        CancellationToken cancellationToken)
    {
        var record = await _repository.GetAsync(recordId, cancellationToken)
            ?? throw new RecordAutomationException(
                RecordAutomationFailureKind.NotFound,
                "Record not found.");
        // Evaluate the displayed current record inside the mutation gate. A
        // snapshot captured when the HTTP request started may predate a new
        // recording/archive transition.
        currentRecordId ??= _currentRecordIdProvider?.Invoke();
        // The coordinator clears CurrentRecord only after completion is
        // persisted. Reject the same id even when the DB row already says
        // Completed so the in-memory completion tail cannot be edited.
        if (currentRecordId == recordId ||
            record.State is not (RecordingState.Completed or RecordingState.Imported))
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Conflict,
                "The current recording record or a non-completed record cannot be modified.");
        }

        return record;
    }

    private static void ValidateRecordId(Guid recordId)
    {
        if (recordId == Guid.Empty)
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Validation,
                "recordId must be a non-empty GUID.");
        }
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaxListLimit)
        {
            throw new RecordAutomationException(
                RecordAutomationFailureKind.Validation,
                $"limit must be between 1 and {MaxListLimit}.");
        }
    }

    private static RecordSummaryView MapSummary(ScanRecord record, SyncDelivery? delivery) =>
        new(
            record.Id,
            record.TrackingNo,
            record.Workflow.ToString(),
            record.State.ToString(),
            record.ScannedAt,
            record.RecordingEndedAt,
            record.VideoPath,
            record.Note,
            MapTags(record.Tags),
            MapSync(delivery));

    private static RecordDetailView MapDetail(
        ScanRecord record,
        IReadOnlyList<RecordTagAssignment> tags,
        IReadOnlyList<RecordMediaAsset> mediaAssets,
        IReadOnlyList<MediaGap> mediaGaps,
        SyncDelivery? delivery) =>
        new(
            record.Id,
            record.TrackingNo,
            record.Workflow.ToString(),
            record.State.ToString(),
            record.ScannedAt,
            record.RecordingStartedAt,
            record.RecordingEndedAt,
            record.VideoPath,
            record.Note,
            record.NoteUpdatedAt,
            record.MediaIntegrity.ToString(),
            record.Snapshots.ToArray(),
            MapTags(tags),
            mediaAssets.Select(MapMediaAsset).ToArray(),
            mediaGaps.Select(MapMediaGap).ToArray(),
            MapSync(delivery));

    private static IReadOnlyList<RecordTagView> MapTags(IEnumerable<RecordTagAssignment> tags) =>
        tags.Where(tag => tag.IsActive)
            .OrderBy(tag => tag.TaggedAt)
            .Select(MapTag)
            .ToArray();

    private static RecordTagView MapTag(RecordTagAssignment tag) =>
        new(tag.Id, tag.TagId, tag.TagName, tag.ColorHex, tag.TaggedAt, tag.Source);

    private static RecordMediaAssetView MapMediaAsset(RecordMediaAsset asset) =>
        new(
            asset.Id,
            asset.CameraId,
            asset.DisplayName,
            asset.Role.ToString(),
            asset.VideoPath,
            asset.RelativeVideoPath,
            asset.Codec,
            asset.EncoderName,
            asset.HardwareAccelerated,
            asset.Width,
            asset.Height,
            asset.FramesPerSecond,
            asset.StartOffset,
            asset.Duration,
            asset.Integrity.ToString(),
            asset.Segments.Select(segment => new RecordMediaSegmentView(
                segment.Id,
                segment.Sequence,
                segment.VideoPath,
                segment.StartOffset,
                segment.Duration,
                segment.Recovered)).ToArray());

    private static RecordMediaGapView MapMediaGap(MediaGap gap) =>
        new(
            gap.Id,
            gap.MediaAssetId,
            gap.CameraId,
            gap.StartedAt,
            gap.EndedAt,
            gap.Recovered);

    private static RecordSyncView? MapSync(SyncDelivery? delivery) =>
        delivery is null
            ? null
            : new RecordSyncView(
                delivery.ConnectorId,
                delivery.Status.ToString(),
                delivery.Status == SyncStatus.Succeeded,
                delivery.AttemptCount,
                delivery.NextRetryAt == DateTimeOffset.MaxValue ? null : delivery.NextRetryAt,
                delivery.UpdatedAt);
}
