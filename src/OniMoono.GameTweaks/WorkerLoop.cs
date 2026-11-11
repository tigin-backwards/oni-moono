using System;
using System.Collections.Generic;
using System.Threading;

namespace OniMoono.GameTweaks
{
    /// Parallel.For for a loop a tweak runs each frame, without most of its allocations (about
    /// 2 KB a call: the task replicator, its replicas and tasks, the index ranges and the
    /// closures), with no game types (tests compile it in). The calling thread claims items too,
    /// as Parallel.For's does, and up to one thread-pool helper a core besides; it waits only for
    /// items a helper has claimed and not finished, so a helper that starts late finds nothing
    /// left and returns. A call allocates its run and the pool's record of each helper queued.
    ///
    /// Exceptions as Parallel.For: after one, no item starts that had not; those running finish,
    /// and every exception thrown comes out in one AggregateException.
    internal static class WorkerLoop
    {
        sealed class Run
        {
            public Action<int> Body;
            public int Count;
            public int Next = -1;
            /// Threads between claiming an item and finishing it (or finding none left).
            public int InFlight;
            public List<Exception> Errors;
        }

        static readonly WaitCallback help = state => Work((Run)state);

        /// Helpers queued at most: one a core besides the caller, as Parallel.For's default.
        public static int MaxHelpers = Math.Max(0, Environment.ProcessorCount - 1);

        public static void For(int count, Action<int> body)
        {
            if (count <= 0) return;
            var run = new Run { Body = body, Count = count };
            int helpers = Math.Min(count - 1, MaxHelpers);
            for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(help, run);
            Work(run);
            // Claims are closed now (Next is past the end for good): a thread still in flight is
            // finishing an item, and one that enters later finds none.
            if (Volatile.Read(ref run.InFlight) != 0)
                lock (run)
                    while (run.InFlight > 0) Monitor.Wait(run);
            if (run.Errors != null) throw new AggregateException(run.Errors);
        }

        static void Work(Run run)
        {
            while (true)
            {
                // In flight before the claim, so the caller cannot miss an item being run.
                Interlocked.Increment(ref run.InFlight);
                int i = Interlocked.Increment(ref run.Next);
                try
                {
                    if (i >= run.Count) return;
                    try
                    {
                        run.Body(i);
                    }
                    catch (Exception e)
                    {
                        lock (run) (run.Errors ??= new List<Exception>()).Add(e);
                        // No item starts after a failure.
                        Interlocked.Exchange(ref run.Next, run.Count);
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref run.InFlight) == 0)
                        lock (run) Monitor.PulseAll(run);
                }
            }
        }
    }
}
