using UnpackVision.Application.Agent;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class AgentQueueRecoveryTests
{
    [Theory]
    [InlineData("note")]
    [InlineData("add_tag")]
    [InlineData("remove_tag")]
    public async Task RetryAfterPartialWriteRequeuesEvenWhenOldVersionSucceeded(string operation)
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryRepository();
        var record = AgentRecordAutomationTests.CreateRecord(RecordingState.Completed, "TEST-QUEUE-RECOVERY", now);
        repository.Records.Add(record);
        var previousDelivery = new SyncDelivery
        {
            RecordId = record.Id,
            ConnectorId = "excel",
            Status = SyncStatus.Succeeded
        };
        repository.Deliveries.Add(previousDelivery);
        var tags = IssueTagDefaults.Create();
        var tag = tags.First(item => item.Enabled);
        var service = new RecordAutomationService(repository, () => tags, new FakeClock(now));
        var assignment = operation == "remove_tag"
            ? await repository.AddTagAsync(record.Id, tag, now, "test")
            : null;
        Func<Task> write = operation switch
        {
            "note" => async () => { await service.SetRecordNoteAsync(record.Id, "new note"); },
            "add_tag" => async () => { await service.AddRecordTagAsync(record.Id, tag.Id); },
            _ => async () => { await service.RemoveRecordTagAsync(record.Id, assignment!.Id); }
        };

        repository.FailNextEnqueue = true;
        await Assert.ThrowsAsync<IOException>(write);
        Assert.Equal(SyncStatus.Succeeded, previousDelivery.Status);

        await write();

        Assert.Equal(SyncStatus.Pending, Assert.Single(repository.Deliveries).Status);
        if (operation == "note") Assert.Equal("new note", record.Note);
        var activeTags = await repository.GetTagsAsync(record.Id);
        Assert.Equal(operation == "add_tag" ? 1 : 0, activeTags.Count);
    }
}
