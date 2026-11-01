using System;
using System.Reflection;
using System.Text;
using System.Threading;

namespace OniMoono.Engine
{
    /// The thread pool's own bookkeeping, read through reflection for a log line when work on it
    /// waits too long (CoreCLR's PortableThreadPool and ThreadPoolWorkQueue; fields that are not
    /// found are left out).
    internal static class ThreadPoolState
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static string Describe()
        {
            var s = new StringBuilder();
            s.Append($"threads {ThreadPool.ThreadCount}, queued {ThreadPool.PendingWorkItemCount}, completed {ThreadPool.CompletedWorkItemCount}");
            try
            {
                var corelib = typeof(ThreadPool).Assembly;
                var pool = corelib.GetType("System.Threading.PortableThreadPool")?.GetField("ThreadPoolInstance", Any)?.GetValue(null);
                if (pool != null)
                {
                    var separated = pool.GetType().GetField("_separated", Any)?.GetValue(pool);
                    if (separated != null)
                    {
                        var counts = separated.GetType().GetField("counts", Any)?.GetValue(separated);
                        if (counts != null)
                            foreach (var name in (string[])["NumProcessingWork", "NumExistingThreads", "NumThreadsGoal"])
                                s.Append($", {name} {counts.GetType().GetProperty(name, Any)?.GetValue(counts)}");
                        foreach (var name in (string[])["numRequestedWorkers", "gateThreadRunningState", "lastDequeueTime"])
                            s.Append($", {name} {separated.GetType().GetField(name, Any)?.GetValue(separated)}");
                    }
                    foreach (var name in (string[])["_pendingBlockingAdjustment", "_numBlockedThreads", "_numThreadsAddedDueToBlocking", "_minThreads", "_maxThreads", "_memoryUsageBytes", "_memoryLimitBytes"])
                        s.Append($", {name} {pool.GetType().GetField(name, Any)?.GetValue(pool)}");
                }
                s.Append($", now {Environment.TickCount}");
                var queue = typeof(ThreadPool).GetField("s_workQueue", Any)?.GetValue(null);
                if (queue != null)
                {
                    var separated = queue.GetType().GetField("_separated", Any)?.GetValue(queue);
                    s.Append($", outstanding request {separated?.GetType().GetField("_hasOutstandingThreadRequest", Any)?.GetValue(separated)}");
                    var list = corelib.GetType("System.Threading.ThreadPoolWorkQueue+WorkStealingQueueList")?.GetProperty("Queues", Any)?.GetValue(null) as Array;
                    if (list != null)
                    {
                        int nonEmpty = 0;
                        foreach (var q in list)
                            if (q.GetType().GetProperty("CanSteal", Any)?.GetValue(q) is true) nonEmpty++;
                        s.Append($", local queues {list.Length} ({nonEmpty} with work)");
                    }
                }
            }
            catch (Exception e)
            {
                s.Append($" (internals unreadable: {e.GetType().Name})");
            }
            return s.ToString();
        }
    }
}
