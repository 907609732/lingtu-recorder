namespace UnpackVision.Core;

/// <summary>
/// Serializes record mutations with Excel delivery claims/completions, media
/// rename updates and desktop annotation edits. The lock is shared by one
/// desktop process; it does not claim to coordinate a second process writing
/// the same SQLite file.
/// </summary>
public interface IRecordMutationGate
{
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default);
}

public sealed class RecordMutationGate : IRecordMutationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
