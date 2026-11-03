using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// Frame stats' per-bucket profile (`--frame-stats` with ONI_BUCKET_PROFILE=1), not a tweak:
    /// StateMachineUpdater.BucketGroup.AdvanceOneSubTick's loop, the same calls in the same order,
    /// with each bucket's Update timed. The 600-frame window's line lists the costliest update
    /// kinds by rate: the bucket's name, its updater (the state's callback, or the scheduler's
    /// interface for components), the entries it holds and the time.
    public static class BucketProfile
    {
        public static bool Enabled { get; private set; }

        static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>> frames =
            AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>>("bucketFrames");
        static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, int> next =
            AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, int>("nextUpdateIndex");

        sealed class Kind
        {
            public string Label;
            public long Ticks, Calls, Entries;
        }

        static readonly Dictionary<StateMachineUpdater.BaseUpdateBucket, Kind> byBucket = new Dictionary<StateMachineUpdater.BaseUpdateBucket, Kind>();
        static readonly Dictionary<string, Kind> byLabel = new Dictionary<string, Kind>();

        /// The timed loop.
        static void Timed(StateMachineUpdater.BucketGroup group, float dt)
        {
            var list = frames(group)[next(group)];
            int count = list.Count;
            string rate = group.updateRate.ToString();
            for (int i = 0; i < count; i++)
            {
                var bucket = list[i];
                int entries = bucket.count;
                if (entries == 0) continue;
                long start = Stopwatch.GetTimestamp();
                bucket.Update(dt);
                long ticks = Stopwatch.GetTimestamp() - start;
                if (!byBucket.TryGetValue(bucket, out var kind))
                {
                    string label = rate + " | " + Label(bucket);
                    if (!byLabel.TryGetValue(label, out kind)) byLabel[label] = kind = new Kind { Label = label };
                    byBucket[bucket] = kind;
                }
                kind.Ticks += ticks;
                kind.Calls++;
                kind.Entries += entries;
            }
            next(group) = (next(group) + 1) % frames(group).Count;
        }

        /// The prefix proper: replaces the loop with the timed one (the same calls, in order).
        static bool AdvanceTimed(StateMachineUpdater.BucketGroup __instance, float dt)
        {
            if (!GameFrameStats.OnMain) return true;
            Timed(__instance, dt);
            return false;
        }

        public static void Install(Harmony harmony)
        {
            Enabled = true;
            var method = AccessTools.Method(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick));
            // After frame stats' own prefix (Priority.First), so its span holds this loop.
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(BucketProfile), nameof(AdvanceTimed)) { priority = Priority.First - 1 });
        }

        /// The bucket's name, its data type and, for a state's update, the callback's method.
        static string Label(StateMachineUpdater.BaseUpdateBucket bucket)
        {
            var type = bucket.GetType();
            string data = type.IsGenericType ? Short(type.GetGenericArguments()[0]) : type.Name;
            string updater = "";
            try
            {
                var entries = AccessTools.Field(type, "entries")?.GetValue(bucket);
                var list = entries?.GetType().GetMethod("GetDataList")?.Invoke(entries, null) as IList;
                if (list != null)
                    foreach (var entry in list)
                    {
                        var u = entry.GetType().GetField("updater").GetValue(entry);
                        if (u == null) continue;
                        var callback = u.GetType().GetField("callback", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(u) as Delegate;
                        updater = callback != null ? $"{Short(callback.Method.DeclaringType)}.{callback.Method.Name}" : Short(u.GetType());
                        break;
                    }
                if (AccessTools.Field(type, "batch_update_delegate")?.GetValue(bucket) is Delegate batch)
                    updater = $"batch {Short(batch.Method.DeclaringType)}.{batch.Method.Name}";
            }
            catch (Exception)
            {
            }
            return $"{bucket.name} [{data}] {updater}";
        }

        static string Short(Type type)
        {
            if (type == null) return "?";
            var name = type.FullName ?? type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0 && type.IsGenericType) name = name.Substring(0, tick) + "<" + string.Join(",", type.GetGenericArguments().Select(Short)) + ">";
            return name.Replace("+", ".");
        }

        /// The window's line: the 40 costliest kinds, ms a frame, calls and entries a call.
        public static string[] Take(int frames)
        {
            if (!Enabled || frames <= 0 || byLabel.Count == 0) return Array.Empty<string>();
            var c = CultureInfo.InvariantCulture;
            double ms = 1000.0 / Stopwatch.Frequency;
            var lines = new List<string>();
            long total = byLabel.Values.Sum(k => k.Ticks);
            lines.Add(string.Format(c, "bucket profile (ms/frame over {0}): all buckets {1:F3} in {2} kinds", frames, total * ms / frames, byLabel.Count));
            foreach (var k in byLabel.Values.OrderByDescending(k => k.Ticks).Take(40))
                lines.Add(string.Format(c, "bucket profile: {0:F3} ms, {1:F2} calls/frame, {2:F0} entries/call, {3:F2} us/entry: {4}",
                    k.Ticks * ms / frames, (double)k.Calls / frames, k.Calls > 0 ? (double)k.Entries / k.Calls : 0,
                    k.Entries > 0 ? k.Ticks * ms * 1000 / k.Entries : 0, k.Label));
            foreach (var k in byLabel.Values) k.Ticks = k.Calls = k.Entries = 0;
            return lines.ToArray();
        }
    }
}
