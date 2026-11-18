using UnpackVision.Agent.Protocol;

namespace UnpackVision.Tests;

public sealed class AgentConnectionLockTests
{
    [Fact]
    public async Task PublishWaitsForSharedSidecarLock()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        Directory.CreateDirectory(directory.Path);
        await using var heldLock = new FileStream(
            AgentConnectionFile.GetLockPath(path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var publish = AgentConnectionFile.WriteAtomicAsync(
            path,
            Connection("publish-token-0123456789", 111),
            CancellationToken.None,
            TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        Assert.False(publish.IsCompleted);

        await heldLock.DisposeAsync();
        await publish.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CleanupAndNewOwnerPublishRaceCannotDeleteTheNewOwner()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        var oldOwner = Connection("old-owner-token-0123456789", 101);
        var newOwner = Connection("new-owner-token-0123456789", 202);
        await AgentConnectionFile.WriteAtomicAsync(path, oldOwner, CancellationToken.None);

        await using var heldLock = new FileStream(
            AgentConnectionFile.GetLockPath(path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var cleanup = AgentConnectionFile.TryDeleteIfOwnedAsync(
            path,
            oldOwner.Token,
            oldOwner.Pid,
            CancellationToken.None,
            TimeSpan.FromSeconds(2));
        var publish = AgentConnectionFile.WriteAtomicAsync(
            path,
            newOwner,
            CancellationToken.None,
            TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        await heldLock.DisposeAsync();
        await Task.WhenAll(cleanup, publish).WaitAsync(TimeSpan.FromSeconds(4));

        var current = await AgentConnectionFile.ReadAsync(path, CancellationToken.None);
        Assert.Equal(newOwner.Token, current.Token);
        Assert.Equal(newOwner.Pid, current.Pid);
    }

    [Fact]
    public async Task PublishLockWaitIsBounded()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "connection.json");
        Directory.CreateDirectory(directory.Path);
        await using var heldLock = new FileStream(
            AgentConnectionFile.GetLockPath(path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AgentConnectionFile.WriteAtomicAsync(
                path,
                Connection("timeout-token-0123456789", 303),
                CancellationToken.None,
                TimeSpan.FromMilliseconds(100)));
    }

    private static AgentConnectionInfo Connection(string token, int pid) => new()
    {
        BaseUrl = "http://127.0.0.1:42001/",
        Token = token,
        Pid = pid,
        StartedAt = DateTimeOffset.UtcNow
    };
}
