using System.Collections.Concurrent;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

/// <summary>
/// Singleton per-repo async mutex. Both <c>BaselineSyncService</c> and the per-ticket
/// apply path acquire the same lock for a repo so a baseline rebuild can't clobber a
/// repo while an apply is copying its baseline.
/// </summary>
public sealed class RepoLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string repoFullName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoFullName))
        {
            throw new ArgumentException("repoFullName must be non-empty.", nameof(repoFullName));
        }
        SemaphoreSlim semaphore = _locks.GetOrAdd(repoFullName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
