using System;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-jobs` (opt-in). The game's JobManager runs a collection of work items on
    /// its worker threads and the calling thread. Each Run wakes every worker (a kernel semaphore
    /// released once per worker, 15 on a 16-thread machine), works through the items, then
    /// waits until every woken worker has run, found no item left and checked in, however few
    /// items there were. The main thread calls Run about 14 times a frame in U57 (fetch updates,
    /// mesh batches, amounts, conveyor arms, conduits); waiting on thread wake-ups is much of the
    /// 5.4 ms a frame spent in it.
    ///
    /// The tweak replaces Run with a scheduler of its own threads (as many as the JobManager has
    /// workers, each with the same fixed thread index the work items use for per-thread data):
    /// it wakes only as many workers as there are items beyond the caller's first, each through
    /// a semaphore of its own that spins briefly before blocking (WorkerWakes), and returns as
    /// soon as every item has completed. Each item still runs exactly once, on one thread, and
    /// all are done before Run returns; which thread runs which item was never fixed. A single
    /// item runs on the caller.
    ///
    /// With ONI_TWEAK_VERIFY=1 every run checks that each item index ran exactly once.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void JobManager::Run(IWorkItemCollection)", "dd3f5277f6a47c7f linux-744825 win-744825", "355b5baec1f1de62 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    public static class FastJobs
    {
        public const string Name = "fast-jobs";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly ConditionalWeakTable<JobManager, Scheduler> schedulers = new ConditionalWeakTable<JobManager, Scheduler>();
        static long runs, wrong;

        public static void Install(Harmony harmony)
            => harmony.Patch(AccessTools.Method(typeof(JobManager), nameof(JobManager.Run)), prefix: new HarmonyMethod(typeof(FastJobs), nameof(Run)));

        /// One Run: its items, the next index to take and how many have not finished.
        sealed class Batch
        {
            public IWorkItemCollection Items;
            public int Count, Next = -1, Remaining;
            public int[] Ran; // verify mode: runs per index
            /// Set by whichever thread finishes the last item (one per run, so a late signal
            /// from an earlier run cannot end this one).
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        sealed class Scheduler
        {
            readonly WorkerWakes wakes;
            public readonly int Workers;
            volatile Batch current;

            public Scheduler(int workers)
            {
                Workers = workers;
                wakes = new WorkerWakes(workers);
                for (int i = 0; i < workers; i++)
                {
                    int index = i + 1; // the caller is index 0, as in JobManager
                    var thread = new Thread(() => Worker(index), 131072) { IsBackground = true, Name = $"KFastWorker{i}", Priority = ThreadPriority.AboveNormal };
                    thread.Start();
                }
            }

            void Worker(int index)
            {
                while (true)
                {
                    wakes.Wait(index - 1);
                    var batch = current;
                    if (batch != null) Work(batch, index);
                }
            }

            /// Takes items until none are left; the thread that finishes the last one signals.
            void Work(Batch batch, int threadIndex)
            {
                int i;
                while ((i = Interlocked.Increment(ref batch.Next)) < batch.Count)
                {
                    try
                    {
                        if (batch.Ran != null) Interlocked.Increment(ref batch.Ran[i]);
                        batch.Items.InternalDoWorkItem(i, threadIndex);
                    }
                    // As JobManager: a worker's exception is reported, the caller's propagates.
                    catch (Exception e) when (threadIndex != 0)
                    {
                        JobManager.errorOccured = true;
                        global::Debug.LogError(e);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref batch.Remaining) == 0) batch.Done.Set();
                    }
                }
            }

            public void Run(IWorkItemCollection items)
            {
                int count = items.Count;
                if (count <= 0) return;
                var batch = new Batch { Items = items, Count = count, Remaining = count, Ran = verify ? new int[count] : null };
                current = batch;
                int helpers = Math.Min(Workers, count - 1);
                if (helpers > 0) wakes.Wake(helpers);
                Work(batch, 0);
                if (Volatile.Read(ref batch.Remaining) != 0)
                {
                    // Frame stats: the caller's wait for the workers (GameFrameStats).
                    long waiting = GameFrameStats.OnMain ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    batch.Done.Wait();
                    if (waiting != 0) GameFrameStats.AddJobWait(waiting);
                }
                if (batch.Ran == null) return;
                runs++;
                for (int k = 0; k < count; k++)
                    if (batch.Ran[k] != 1) { global::Debug.Log($"{Name}: item {k} of a {items.GetType().Name} ran {batch.Ran[k]} times ({++wrong})"); break; }
                if (runs % 20000 == 0) global::Debug.Log($"{Name}: {runs} runs checked, {wrong} wrong");
            }
        }

        /// JobManager.Run(work_items).
        static bool Run(JobManager __instance, IWorkItemCollection work_items)
        {
            var threads = __instance.threads;
            // Single-threaded managers (one core) run as the game does.
            if (threads == null || threads.Count == 0) return true;
            schedulers.GetValue(__instance, m => new Scheduler(m.threads.Count)).Run(work_items);
            return false;
        }
    }
}
