using System.Threading;

namespace OniMoono.GameTweaks
{
    /// How the `fast-jobs` scheduler (FastJobs) wakes its workers: a semaphore of each worker's
    /// own, so that a job's helpers wake side by side. One semaphore shared by all of them made
    /// the woken workers queue for its lock one after another (Windows, U57 on U60: 15 workers,
    /// each waiting about 0.18 ms for the lock after every wake, the last helpers starting late
    /// while the main thread waited for their items). Wake(n) wakes workers 0..n-1, each once;
    /// a wake given while its worker is still busy is kept for its next Wait, as the shared
    /// semaphore's count kept it. Plain .NET (no game types): the unit tests compile it in.
    public sealed class WorkerWakes
    {
        readonly SemaphoreSlim[] wakes;

        public WorkerWakes(int workers)
        {
            wakes = new SemaphoreSlim[workers];
            for (int i = 0; i < workers; i++) wakes[i] = new SemaphoreSlim(0);
        }

        public int Workers => wakes.Length;

        /// Wakes the first `count` workers (at most all of them).
        public void Wake(int count)
        {
            if (count > wakes.Length) count = wakes.Length;
            for (int i = 0; i < count; i++) wakes[i].Release();
        }

        /// Worker `worker` (0-based) waits for its next wake.
        public void Wait(int worker) => wakes[worker].Wait();

        /// Whether worker `worker` has a wake waiting (tests).
        public bool Pending(int worker) => wakes[worker].CurrentCount > 0;
    }
}
