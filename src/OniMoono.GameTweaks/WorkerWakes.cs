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

    /// A batch the scheduler hands to its workers: `Holders` counts the caller and each worker
    /// it was handed to and that is not done with it.
    public class HeldBatch
    {
        internal int Holders;
    }

    /// How the `fast-jobs` scheduler keeps one run's batch for the next run instead of making a
    /// new one each time (0.9 KB a frame with its event, on a paused colony). A worker woken
    /// late must never take items of a later run from a batch it was woken for earlier, so a
    /// batch is used again only when nothing holds it: Hand gives it to the caller and to the
    /// first workers, each of which takes it once (Take) and gives it back (Release); a batch a
    /// worker had not taken when the next one was handed to it is given back for that worker.
    /// The last holder's Release makes it the spare, which Spare takes. Plain .NET (no game
    /// types): the unit tests compile it in.
    public sealed class BatchHandOver<T> where T : HeldBatch
    {
        readonly T[] handed;
        T spare;

        public BatchHandOver(int workers) => handed = new T[workers];

        /// A batch nothing holds, or null when there is none: no other thread reads or writes
        /// it until it is handed over.
        public T Spare() => Interlocked.Exchange(ref spare, null);

        /// Hands the batch, filled in, to the caller and workers 0..helpers-1 (wake them after).
        public void Hand(T batch, int helpers)
        {
            Volatile.Write(ref batch.Holders, helpers + 1);
            for (int i = 0; i < helpers; i++)
            {
                var untaken = Interlocked.Exchange(ref handed[i], batch);
                if (untaken != null) Release(untaken);
            }
        }

        /// The batch handed to worker `worker` (0-based) and not taken yet, or null: a wake
        /// whose batch a later run replaced, or that the worker took at an earlier wake.
        public T Take(int worker) => Interlocked.Exchange(ref handed[worker], null);

        /// A holder is done with the batch.
        public void Release(T batch)
        {
            if (Interlocked.Decrement(ref batch.Holders) == 0) Volatile.Write(ref spare, batch);
        }
    }
}
