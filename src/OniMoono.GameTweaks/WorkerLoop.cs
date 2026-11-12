#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;

namespace OniMoono.GameTweaks
{
    /// Parallel.For for a loop a tweak runs each frame, without its allocations (about 2 KB a
    /// call: the task replicator, its replicas and tasks, the index ranges and the closures), with
    /// no game types (tests compile it in). The calling thread claims items too, as Parallel.For's
    /// does, and up to one thread-pool helper a core besides; it waits only for items a helper has
    /// claimed and not finished, so a helper that starts late finds nothing left and returns.
    ///
    /// Compiled into the host as well (OniMoono, .NET): there a run is the pool's work item
    /// itself, queued once a helper, and runs are reused once every helper queued for them has
    /// finished with them, so a call allocates nothing. This assembly (.NET Standard, which has no
    /// such work items) calls the host's copy when it finds it, and otherwise makes a run a call
    /// and the pool a record a helper queued.
    ///
    /// Exceptions as Parallel.For: after one, no item starts that had not; those running finish,
    /// and every exception thrown comes out in one AggregateException.
    internal static class WorkerLoop
    {
        sealed class Run
#if NET
            : IThreadPoolWorkItem
#endif
        {
            public Action<int> Body;
            public int Count;
            public int Next = -1;
            /// Threads between claiming an item and finishing it (or finding none left).
            public int InFlight;
            public List<Exception> Errors;
#if NET
            /// Helpers queued for this run that have not finished with it, and whether a call
            /// has it: it is reused only when both are clear, so no helper of an earlier call
            /// sees it being set up again.
            public int Queued, InUse;

            void IThreadPoolWorkItem.Execute()
            {
                Work(this);
                // Last: the run is not touched after this.
                Interlocked.Decrement(ref Queued);
            }
#endif
        }

        /// Helpers queued at most: one a core besides the caller, as Parallel.For's default.
        public static int MaxHelpers = Math.Max(0, Environment.ProcessorCount - 1);

#if NET
        /// Runs to reuse (any thread may call For).
        static readonly Run[] runs = new Run[8];

        static Run Take()
        {
            for (int i = 0; i < runs.Length; i++)
            {
                var run = runs[i];
                if (run == null)
                {
                    run = new Run { InUse = 1 };
                    if (Interlocked.CompareExchange(ref runs[i], run, null) == null) return run;
                    run = runs[i];
                }
                if (Volatile.Read(ref run.Queued) == 0 && Interlocked.CompareExchange(ref run.InUse, 1, 0) == 0)
                {
                    // Taken, so a helper finishing late is past its last touch.
                    if (Volatile.Read(ref run.Queued) == 0) return run;
                    Volatile.Write(ref run.InUse, 0);
                }
            }
            // Every run is held by a call or by a helper still to start: one of this call's own.
            return new Run { InUse = 1 };
        }
#else
        static readonly WaitCallback help = state => Work((Run)state);
        static Action<int, Action<int>> hosted;
        static bool bound;

        /// The host's copy (allocates nothing), when this runs in it.
        static Action<int, Action<int>> Hosted()
        {
            if (bound) return hosted;
            bound = true;
            try
            {
                var method = Type.GetType("OniMoono.GameTweaks.WorkerLoop, OniMoono", false)?.GetMethod(nameof(For), new[] { typeof(int), typeof(Action<int>) });
                if (method != null) hosted = (Action<int, Action<int>>)Delegate.CreateDelegate(typeof(Action<int, Action<int>>), method);
            }
            catch (Exception)
            {
                hosted = null;
            }
            return hosted;
        }
#endif

        public static void For(int count, Action<int> body)
        {
            if (count <= 0) return;
#if NET
            var run = Take();
            run.Body = body;
            run.Count = count;
            run.Errors = null;
            run.InFlight = 0;
            // Last: helpers claim from here, and see the fields above.
            Volatile.Write(ref run.Next, -1);
            int helpers = Math.Min(count - 1, MaxHelpers);
            Interlocked.Add(ref run.Queued, helpers);
            for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(run, preferLocal: false);
#else
            if (Hosted() is { } host)
            {
                host(count, body);
                return;
            }
            var run = new Run { Body = body, Count = count };
            int helpers = Math.Min(count - 1, MaxHelpers);
            for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(help, run);
#endif
            Work(run);
            // Claims are closed now (Next is past the end for good): a thread still in flight is
            // finishing an item, and one that enters later finds none.
            if (Volatile.Read(ref run.InFlight) != 0)
                lock (run)
                    while (run.InFlight > 0) Monitor.Wait(run);
            var errors = run.Errors;
#if NET
            run.Body = null;
            Volatile.Write(ref run.InUse, 0);
#endif
            if (errors != null) throw new AggregateException(errors);
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
