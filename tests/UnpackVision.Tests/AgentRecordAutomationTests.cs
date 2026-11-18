using UnpackVision.Application.Agent;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class AgentRecordAutomationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task GetStatus_ReportsDesktopReadinessAndExcelQueueSemantics()
    {
        var repository = new InMemoryRepository();
        var service = CreateService(repository);
        var runtime = new RecordAutomationRuntime(
            DesktopReady: true,
            DatabaseReady: true,
            RecordingState: RecordingState.Recording,
            CurrentRecordId: Guid.NewGuid(),
            CurrentTrackingNo: "SF1234567890",
            AppVersion: "2.6.3-test",
            InitializedAt: Now.AddMinutes(-1));

        var status = await service.GetStatusAsync(runtime, CancellationToken.None);

        Assert.True(status.DesktopReady);
        Assert.True(status.DatabaseReady);
        Assert.Equal("2.6.3-test", status.AppVersion);
        Assert.Equal(RecordingState.Recording.ToString(), status.Recording.State);
        Assert.Equal("SF1234567890", status.Recording.TrackingNo);
        Assert.Equal("excel", status.ExcelSync.ConnectorId);
        Assert.False(status.ExcelSync.QueuedIsSynced);
        Assert.True(status.ExcelSync.WritesEnqueueDelivery);
        Assert.Equal("all_non_succeeded", status.ExcelSync.RetryScope);
        Assert.Contains("Pending", status.ExcelSync.DeliveryStates);
        Assert.Contains("Succeeded", status.ExcelSync.DeliveryStates);
        Assert.Equal(8, status.Scope.Tools.Count);
        Assert.Contains("start_recording", status.Scope.ExcludedCapabilities);
        Assert.Contains("delete_record", status.Scope.ExcludedCapabilities);
        Assert.Contains("arbitrary_file_read", status.Scope.ExcludedCapabilities);
    }

    [Fact]
    public async Task ListRecords_BoundsResultsAndReportsThatMoreExist()
    {
        var repository = new InMemoryRepository();
        repository.Records.AddRange(
        [
            CreateRecord(RecordingState.Completed, "SF0000000001", Now.AddMinutes(-1)),
            CreateRecord(RecordingState.Completed, "SF0000000002", Now.AddMinutes(-2)),
            CreateRecord(RecordingState.Completed, "SF0000000003", Now.AddMinutes(-3))
        ]);
        var service = CreateService(repository);

        var page = await service.ListRecordsAsync(null, 2, CancellationToken.None);

        Assert.Equal(2, page.Returned);
        Assert.Equal(2, page.Limit);
        Assert.True(page.HasMore);
        Assert.Equal(2, page.Records.Count);
        Assert.DoesNotContain(page.Records, item => item.TrackingNo == "SF0000000003");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task ListRecords_RejectsLimitsOutsideOneToTwoHundred(int limit)
    {
        var service = CreateService(new InMemoryRepository());

        var exception = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.ListRecordsAsync(null, limit, CancellationToken.None));

        Assert.Equal(RecordAutomationFailureKind.Validation, exception.Kind);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("200", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecord_ReturnsOnlyOwnedRecordMediaPathsAndTags()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        record.Note = "外箱有压痕";
        record.Tags =
        [
            new RecordTagAssignment
            {
                RecordId = record.Id,
                TagId = "DAMAGE01",
                TagName = "破损",
                ColorHex = "#FF3B30",
                TaggedAt = Now.AddSeconds(2)
            }
        ];
        repository.Records.Add(record);
        repository.Deliveries.Add(new SyncDelivery
        {
            RecordId = record.Id,
            ConnectorId = "excel",
            Status = SyncStatus.Succeeded,
            AttemptCount = 1,
            UpdatedAt = Now.AddMinutes(1)
        });
        var service = CreateService(repository);

        var detail = await service.GetRecordAsync(record.Id, CancellationToken.None);

        Assert.Equal(record.Id, detail.RecordId);
        Assert.Equal("外箱有压痕", detail.Note);
        Assert.Single(detail.MediaAssets);
        Assert.Equal(record.VideoPath, detail.MediaAssets[0].VideoPath);
        Assert.Single(detail.Tags);
        Assert.Equal("DAMAGE01", detail.Tags[0].TagId);
        Assert.True(detail.ExcelSync!.Succeeded);
        Assert.Equal("Succeeded", detail.ExcelSync.Status);
    }

    [Fact]
    public async Task GetRecord_RejectsUnknownRecord()
    {
        var service = CreateService(new InMemoryRepository());

        var exception = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.GetRecordAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(RecordAutomationFailureKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task SetRecordNote_RejectsActiveRecordingWithoutWritingOrQueueing()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Recording, "SF1234567890", Now);
        repository.Records.Add(record);
        var service = CreateService(repository);

        var exception = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.SetRecordNoteAsync(record.Id, "不应写入", CancellationToken.None));

        Assert.Equal(RecordAutomationFailureKind.Conflict, exception.Kind);
        Assert.Equal(string.Empty, record.Note);
        Assert.Empty(repository.Deliveries);
    }

    [Fact]
    public async Task SetRecordNote_WritesCompletedRecordAndEnqueuesExcelWithoutClaimingSync()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        repository.Records.Add(record);
        var service = CreateService(repository);

        var result = await service.SetRecordNoteAsync(record.Id, "  已联系客户  ", CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal("已联系客户", record.Note);
        Assert.NotNull(repository.Deliveries.Single());
        Assert.Equal(SyncStatus.Pending, repository.Deliveries.Single().Status);
        Assert.Equal("Pending", result.ExcelSync!.Status);
        Assert.False(result.ExcelSync.Succeeded);
        Assert.Contains("not", result.SyncSemantics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetRecordNote_AcceptsTwoThousandCharactersAndRejectsTwoThousandOne()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Imported, "SF1234567890", Now);
        repository.Records.Add(record);
        var service = CreateService(repository);

        var accepted = await service.SetRecordNoteAsync(
            record.Id,
            new string('x', 2000),
            CancellationToken.None);
        Assert.True(accepted.Changed);

        var exception = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.SetRecordNoteAsync(record.Id, new string('x', 2001), CancellationToken.None));
        Assert.Equal(RecordAutomationFailureKind.Validation, exception.Kind);
    }

    [Fact]
    public async Task SetRecordNote_RejectsCurrentRecordIdEvenWhenDatabaseIsCompleted()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        repository.Records.Add(record);
        var service = CreateService(repository);

        var exception = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.SetRecordNoteAsync(
                record.Id,
                "tail-window write",
                CancellationToken.None,
                currentRecordId: record.Id));

        Assert.Equal(RecordAutomationFailureKind.Conflict, exception.Kind);
        Assert.Equal(string.Empty, record.Note);
        Assert.Empty(repository.Deliveries);
    }

    [Fact]
    public async Task SetRecordNote_RejectsMissingNoteAndAllowsExplicitEmptyStringToClear()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        record.Note = "existing";
        repository.Records.Add(record);
        var service = CreateService(repository);

        var missing = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.SetRecordNoteAsync(record.Id, null!, CancellationToken.None));
        Assert.Equal(RecordAutomationFailureKind.Validation, missing.Kind);
        Assert.Equal("existing", record.Note);
        Assert.Empty(repository.Deliveries);

        var cleared = await service.SetRecordNoteAsync(record.Id, string.Empty, CancellationToken.None);
        Assert.True(cleared.Changed);
        Assert.Equal(string.Empty, record.Note);
        Assert.Single(repository.Deliveries);
    }

    [Fact]
    public async Task SetRecordNote_RequestCancellationAfterCommitStillQueuesDelivery()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        repository.Records.Add(record);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.UpdateNoteOverride = async (_, _, _, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
        };
        var service = CreateService(repository);
        using var requestCancellation = new CancellationTokenSource();

        var writeTask = service.SetRecordNoteAsync(
            record.Id,
            "committed despite disconnect",
            requestCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await requestCancellation.CancelAsync();
        release.TrySetResult();
        var result = await writeTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(result.Changed);
        Assert.Equal("committed despite disconnect", record.Note);
        Assert.Single(repository.Deliveries);
        Assert.Equal(SyncStatus.Pending, repository.Deliveries[0].Status);
    }

    [Fact]
    public async Task SetRecordNote_RetryAfterQueueFailureRepairsMissingDelivery()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        repository.Records.Add(record);
        var service = CreateService(repository);
        repository.FailNextEnqueue = true;

        await Assert.ThrowsAsync<IOException>(() =>
            service.SetRecordNoteAsync(record.Id, "partial write", CancellationToken.None));
        Assert.Equal("partial write", record.Note);
        Assert.Empty(repository.Deliveries);

        var retry = await service.SetRecordNoteAsync(
            record.Id,
            "partial write",
            CancellationToken.None);

        Assert.False(retry.Changed);
        Assert.Single(repository.Deliveries);
        Assert.Equal(SyncStatus.Pending, repository.Deliveries[0].Status);
        Assert.Equal("Pending", retry.ExcelSync!.Status);
    }

    [Fact]
    public async Task RemoveRecordTag_RetryAfterQueueFailureIsIdempotentAndRepairsQueue()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Imported, "SF1234567890", Now);
        var assignment = new RecordTagAssignment
        {
            RecordId = record.Id,
            TagId = "DAMAGE01",
            TagName = "破损",
            ColorHex = "#FF3B30",
            TaggedAt = Now
        };
        record.Tags = [assignment];
        repository.Records.Add(record);
        var service = CreateService(repository);
        repository.FailNextEnqueue = true;

        await Assert.ThrowsAsync<IOException>(() =>
            service.RemoveRecordTagAsync(record.Id, assignment.Id, CancellationToken.None));
        Assert.NotNull(assignment.RemovedAt);
        Assert.Empty(repository.Deliveries);

        var retry = await service.RemoveRecordTagAsync(
            record.Id,
            assignment.Id,
            CancellationToken.None);

        Assert.False(retry.Changed);
        Assert.Empty(retry.ActiveTags);
        Assert.Single(repository.Deliveries);
        Assert.Equal(SyncStatus.Pending, repository.Deliveries[0].Status);
    }

    [Fact]
    public async Task AddRecordTag_RejectsUnknownOrDisabledDefinitions()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        repository.Records.Add(record);
        var tags = IssueTagDefaults.Create();
        tags[0].Enabled = false;
        var service = CreateService(repository, tags);

        var disabled = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.AddRecordTagAsync(record.Id, tags[0].Id, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<RecordAutomationException>(() =>
            service.AddRecordTagAsync(record.Id, "NO-SUCH", CancellationToken.None));

        Assert.Equal(RecordAutomationFailureKind.NotFound, disabled.Kind);
        Assert.Equal(RecordAutomationFailureKind.NotFound, unknown.Kind);
        Assert.Empty(record.Tags);
        Assert.Empty(repository.Deliveries);
    }

    [Fact]
    public async Task AddRecordTag_IsIdempotentAndQueuesExcelOnlyWhenChanged()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Completed, "SF1234567890", Now);
        repository.Records.Add(record);
        var service = CreateService(repository);

        var first = await service.AddRecordTagAsync(record.Id, "DAMAGE01", CancellationToken.None);
        var second = await service.AddRecordTagAsync(record.Id, "DAMAGE01", CancellationToken.None);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Single(record.Tags);
        Assert.Single(repository.Deliveries);
        Assert.Equal("agent", record.Tags[0].Source);
        Assert.Equal("Pending", first.ExcelSync!.Status);
    }

    [Fact]
    public async Task RemoveRecordTag_RemovesActiveAssignmentAndQueuesExcel()
    {
        var repository = new InMemoryRepository();
        var record = CreateRecord(RecordingState.Imported, "SF1234567890", Now);
        var assignment = new RecordTagAssignment
        {
            RecordId = record.Id,
            TagId = "DAMAGE01",
            TagName = "破损",
            ColorHex = "#FF3B30",
            TaggedAt = Now
        };
        record.Tags = [assignment];
        repository.Records.Add(record);
        var service = CreateService(repository);

        var result = await service.RemoveRecordTagAsync(
            record.Id,
            assignment.Id,
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.NotNull(assignment.RemovedAt);
        Assert.Empty(result.ActiveTags);
        Assert.Single(repository.Deliveries);
    }

    [Fact]
    public async Task RetryExcelSync_RequeuesAllNonSucceededExcelDeliveriesOnly()
    {
        var repository = new InMemoryRepository();
        var first = CreateRecord(RecordingState.Completed, "SF0000000001", Now);
        var second = CreateRecord(RecordingState.Completed, "SF0000000002", Now);
        repository.Records.AddRange([first, second]);
        repository.Deliveries.AddRange(
        [
            new SyncDelivery { RecordId = first.Id, ConnectorId = "excel", Status = SyncStatus.Succeeded },
            new SyncDelivery { RecordId = second.Id, ConnectorId = "excel", Status = SyncStatus.Failed },
            new SyncDelivery { RecordId = Guid.NewGuid(), ConnectorId = "other", Status = SyncStatus.Failed }
        ]);
        var service = CreateService(repository);

        var result = await service.RetryExcelSyncAsync(CancellationToken.None);

        Assert.True(result.Queued);
        Assert.Equal("excel", result.ConnectorId);
        Assert.Equal("all_non_succeeded", result.Scope);
        Assert.Equal(SyncStatus.Succeeded, repository.Deliveries[0].Status);
        Assert.Equal(SyncStatus.Pending, repository.Deliveries[1].Status);
        Assert.Equal(SyncStatus.Failed, repository.Deliveries[2].Status);
    }

    private static RecordAutomationService CreateService(
        InMemoryRepository repository,
        IReadOnlyList<IssueTagDefinition>? tags = null) =>
        new(repository, () => tags ?? IssueTagDefaults.Create(), new FakeClock(Now));

    internal static ScanRecord CreateRecord(
        RecordingState state,
        string trackingNo,
        DateTimeOffset scannedAt) =>
        new()
        {
            TrackingNo = trackingNo,
            State = state,
            ScannedAt = scannedAt,
            RecordingStartedAt = scannedAt.AddSeconds(1),
            RecordingEndedAt = scannedAt.AddMinutes(1),
            VideoPath = $@"C:\synthetic-agent-tests\{trackingNo}.mp4",
            MediaAssets =
            [
                new RecordMediaAsset
                {
                    RecordId = Guid.Empty,
                    CameraId = "primary",
                    DisplayName = "主机位",
                    Role = RecordMediaRole.Primary,
                    VideoPath = $@"C:\synthetic-agent-tests\{trackingNo}.mp4",
                    RelativeVideoPath = $"{trackingNo}.mp4",
                    Codec = "h264",
                    EncoderName = "test",
                    Width = 1920,
                    Height = 1080,
                    FramesPerSecond = 15,
                    Duration = TimeSpan.FromMinutes(1),
                    Integrity = MediaIntegrityStatus.Complete,
                    CreatedAt = scannedAt,
                    UpdatedAt = scannedAt
                }
            ],
            CreatedAt = scannedAt,
            UpdatedAt = scannedAt
        };
}
