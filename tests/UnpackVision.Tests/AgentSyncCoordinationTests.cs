using UnpackVision.Application.Agent;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class AgentSyncCoordinationTests
{
    [Fact]
    public async Task AgentWriteWaitsForInFlightPushAndLeavesNewDeliveryPending()
    {
        var now = new DateTimeOffset(2026, 11, 19, 11, 0, 0, TimeSpan.FromHours(8));
        var repository = new InMemoryRepository();
        var record = AgentRecordAutomationTests.CreateRecord(RecordingState.Completed, "SF-COORD-1", now);
        record.Note = "old note";
        repository.Records.Add(record);
        repository.Deliveries.Add(new SyncDelivery
        {
            RecordId = record.Id,
            ConnectorId = "excel",
            Status = SyncStatus.Pending,
            NextRetryAt = now
        });
        var gate = new RecordMutationGate();
        var connector = new BlockingConnector();
        var dispatcher = new SyncDispatcher(repository, [connector], new FakeClock(now), gate);
        var service = new RecordAutomationService(
            repository,
            () => IssueTagDefaults.Create(),
            new FakeClock(now),
            gate);

        var syncTask = dispatcher.ProcessDueAsync(1, CancellationToken.None);
        await connector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var writeTask = service.SetRecordNoteAsync(
            record.Id,
            "new note",
            CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal("old note", record.Note);

        connector.Release.TrySetResult();
        await syncTask.WaitAsync(TimeSpan.FromSeconds(3));
        await writeTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("new note", record.Note);
        var delivery = Assert.Single(repository.Deliveries);
        Assert.Equal(SyncStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal("old note", connector.PushedNote);
    }

    private sealed class BlockingConnector : ISyncConnector
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? PushedNote { get; private set; }

        public string Id => "excel";
        public IReadOnlyList<string> ValidateConfiguration() => [];
        public Task<ConnectorHealth> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectorHealth(true, "ok"));
        public Task<ConnectorHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectorHealth(true, "ok"));

        public async Task<SyncPushResult> PushRecordAsync(
            ScanRecord record,
            CancellationToken cancellationToken = default)
        {
            PushedNote = record.Note;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new SyncPushResult("row:1", "ok");
        }
    }
}
