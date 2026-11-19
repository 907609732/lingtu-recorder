using System;
using System.IO;
using System.Linq;
using UnpackVision.Application.Recording;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class FirstFrameCaptureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(8));
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"UnpackVisionFirstFrame-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartCapturesFirstFrameAndLeavesSnapshotListEmpty()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp);
        var coordinator = CreateCoordinator(repository, backend, new StubBarcodeReader("SF1234567890"));

        var started = await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        var record = Assert.Single(repository.Records);
        Assert.Equal(ScanAction.Started, started.Action);
        Assert.NotNull(record.FirstFramePath);
        Assert.True(File.Exists(record.FirstFramePath));
        Assert.Equal(FirstFrameCheck.Match, record.FirstFrameCheck);
        Assert.NotNull(record.FirstFrameCapturedAt);
        Assert.Empty(record.Snapshots);
    }

    [Fact]
    public async Task SnapshotFailureKeepsRecordingRunning()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp) { AlwaysFailSnapshots = true };
        var coordinator = CreateCoordinator(repository, backend, new StubBarcodeReader("SF1234567890"));

        var started = await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        var record = Assert.Single(repository.Records);
        Assert.Equal(ScanAction.Started, started.Action);
        Assert.Equal(RecordingState.Recording, record.State);
        Assert.Null(record.FirstFramePath);
        Assert.Equal(FirstFrameCheck.NotAttempted, record.FirstFrameCheck);
        Assert.Equal(5, backend.SnapshotAttempts);
    }

    [Fact]
    public async Task RetriesWhileCameraHasNotProducedFirstFrame()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp, failuresBeforeSuccess: 2);
        var coordinator = CreateCoordinator(repository, backend, new StubBarcodeReader("SF1234567890"));

        await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        Assert.Equal(3, backend.SnapshotAttempts);
        Assert.NotNull(Assert.Single(repository.Records).FirstFramePath);
    }

    [Fact]
    public async Task MismatchedBarcodeIsReportedAsMismatch()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp);
        var coordinator = CreateCoordinator(repository, backend, new StubBarcodeReader("SF9999999999"));

        await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        Assert.Equal(FirstFrameCheck.Mismatch, Assert.Single(repository.Records).FirstFrameCheck);
    }

    [Fact]
    public async Task MultipleCodesOnOneLabelStillMatch()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp);
        var coordinator = CreateCoordinator(
            repository,
            backend,
            new StubBarcodeReader("PRODUCT-CODE-01", "SF1234567890", "WH-2026"));

        await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        Assert.Equal(FirstFrameCheck.Match, Assert.Single(repository.Records).FirstFrameCheck);
    }

    [Fact]
    public async Task NoBarcodeReadIsReportedAsUnreadable()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp);
        var coordinator = CreateCoordinator(repository, backend, new StubBarcodeReader());

        await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        Assert.Equal(FirstFrameCheck.Unreadable, Assert.Single(repository.Records).FirstFrameCheck);
    }

    [Fact]
    public async Task MissingBarcodeReaderOnlyCapturesFrame()
    {
        var repository = new InMemoryRepository();
        var backend = new FirstFrameRecordingBackend(_temp);
        var coordinator = new RecordingCoordinator(
            repository,
            backend,
            new NullEventPublisher(),
            new FakeClock(Now),
            new ScannerProfile());

        await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);

        var record = Assert.Single(repository.Records);
        Assert.NotNull(record.FirstFramePath);
        Assert.Equal(FirstFrameCheck.NotAttempted, record.FirstFrameCheck);
    }

    private static RecordingCoordinator CreateCoordinator(
        InMemoryRepository repository,
        IRecordingBackend backend,
        IBarcodeRecognitionService barcodeReader) =>
        new(
            repository,
            backend,
            new NullEventPublisher(),
            new FakeClock(Now),
            new ScannerProfile(),
            barcodeReader: barcodeReader);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp))
            {
                Directory.Delete(_temp, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败不影响断言结果。
        }
    }
}

/// <summary>
/// 专注首帧行为的录像后端：可精确控制快照在第几次调用后成功。
/// </summary>
internal sealed class FirstFrameRecordingBackend(string outputRoot, int failuresBeforeSuccess = 0)
    : IRecordingBackend
{
    private int _snapshotAttempts;

    public bool AlwaysFailSnapshots { get; init; }
    public int SnapshotAttempts => _snapshotAttempts;

    public Task<RecordingSession> StartAsync(
        Guid recordId,
        string trackingNo,
        WorkflowMode workflow,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var temporary = Path.Combine(outputRoot, $"{recordId:N}.partial.mp4");
        File.WriteAllBytes(temporary, [1, 2, 3]);
        return Task.FromResult(new RecordingSession(recordId, trackingNo, workflow, scannedAt, temporary));
    }

    public Task<RecordingCompletion> StopAsync(RecordingSession session, CancellationToken cancellationToken)
    {
        var output = Path.Combine(outputRoot, $"{session.TrackingNo}.mp4");
        File.Move(session.TemporaryPath, output);
        return Task.FromResult(new RecordingCompletion(output, session.StartedAt.AddMinutes(1)));
    }

    public Task AbortAsync(RecordingSession session, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateIssueOverlayAsync(
        Guid recordId,
        IReadOnlyList<RecordTagAssignment> activeTags,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        _snapshotAttempts++;
        if (AlwaysFailSnapshots || _snapshotAttempts <= failuresBeforeSuccess)
        {
            throw new InvalidOperationException("相机尚未提供有效画面");
        }
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, $"first-frame-{_snapshotAttempts}.jpg");
        File.WriteAllBytes(path, [4, 5, 6]);
        return Task.FromResult(path);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class StubBarcodeReader(params string[] codes) : IBarcodeRecognitionService
{
    public Task<IReadOnlyList<BarcodeRecognitionResult>> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BarcodeRecognitionResult>>(
            codes.Select(code => new BarcodeRecognitionResult(code, "CODE_128", [])).ToArray());
}
