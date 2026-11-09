namespace Library.Web.Meshes;

/// <summary>
/// Lets one full mesh load happen at a time, across the viewer (<see cref="MeshService"/>) and the indexer's STL and
/// geometry previews. A large STL or 3MF can take gigabytes while it's read, so a few of them at once could run the
/// container out of memory; waiting a few seconds is cheaper, and every result is cached.
/// </summary>
public sealed class MeshWorkGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Release(_semaphore);
    }

    public IDisposable Enter(CancellationToken cancellationToken)
    {
        _semaphore.Wait(cancellationToken);
        return new Release(_semaphore);
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
