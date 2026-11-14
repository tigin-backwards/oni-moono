using System;
using System.Threading;

namespace OniMoono.Desktop.Gl
{
    /// Runs a loop's items on the calling thread and a few helper threads of its own, returning
    /// when all are done. Idle helpers sleep (they never spin): the thread pool's workers spin for
    /// work after each Parallel.For, and many of them a few times a frame took the GL thread's and
    /// the game's job threads' cores whenever the machine was busy (U57: 17.5 fps serial, 14.3
    /// with Parallel.For, while other work loaded 12 of 16 cores). The caller claims items too,
    /// so a helper that wakes late finds nothing left and sleeps again; the caller waits only for
    /// items a helper has claimed and not finished.
    internal sealed class ForkJoin
    {
        /// One run's items: helpers keep the run they woke for, so a late one never takes items
        /// of a later run.
        sealed class Run
        {
            public Action<int> Body;
            public int Count;
            public int Next = -1;
            public int Remaining;
            /// The caller and the helpers that took the run and are not done with it (under
            /// `gate`). At zero nothing reads the run, and the next For takes it again.
            public int Holders;
        }

        readonly object gate = new();
        Run current;
        /// A run of an earlier For, used again once nothing holds it (a For made a new one
        /// each time).
        Run spare;
        /// Runs made (tests).
        internal int RunsMade;
        int generation;

        /// The caller's time spent waiting for helpers' items, since the last call (diagnostics).
        public long WaitTicks;

        public ForkJoin(int helpers)
        {
            for (int i = 0; i < helpers; i++)
                new Thread(Help) { Name = $"Draw recording {i + 1}", IsBackground = true }.Start();
        }

        /// Helpers for this machine: a quarter of the cores, at most three; none on two cores.
        public static int DefaultHelpers => Math.Clamp(Environment.ProcessorCount / 4, 0, 3);

        public void For(int count, Action<int> body)
        {
            Run run;
            lock (gate)
            {
                // Helpers take a run and give it back under the gate, so a spare without
                // holders is read by no one.
                if (spare is { Holders: 0 }) run = spare;
                else { spare = run = new Run(); RunsMade++; }
                run.Body = body;
                run.Count = count;
                run.Next = -1;
                run.Remaining = count;
                run.Holders = 1;
                current = run;
                generation++;
                Monitor.PulseAll(gate);
            }
            try
            {
                Work(run);
                if (Volatile.Read(ref run.Remaining) == 0) return;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (run)
                    while (run.Remaining > 0) Monitor.Wait(run);
                WaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            }
            finally
            {
                lock (gate) run.Holders--;
            }
        }

        static void Work(Run run)
        {
            int i;
            while ((i = Interlocked.Increment(ref run.Next)) < run.Count)
            {
                try
                {
                    run.Body(i);
                }
                finally
                {
                    if (Interlocked.Decrement(ref run.Remaining) == 0)
                        lock (run) Monitor.PulseAll(run);
                }
            }
        }

        void Help()
        {
            int seen = 0;
            while (true)
            {
                Run run;
                lock (gate)
                {
                    while (generation == seen) Monitor.Wait(gate);
                    seen = generation;
                    run = current;
                    run.Holders++;
                }
                try
                {
                    Work(run);
                }
                finally
                {
                    lock (gate) run.Holders--;
                }
            }
        }
    }
}
