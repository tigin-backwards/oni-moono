using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using G = OniMoono.GameTweaks.GameFrameStats;

namespace OniMoono.GameTweaks
{
    /// Frame stats (`--frame-stats`), not a tweak: patches that time the game's update for
    /// GameFrameStats, made at start-up only with frame stats on. Each timed method gets a
    /// prefix at Priority.First and a postfix at Priority.Last, so mods' patches of it fall
    /// inside its time; the patches only read the clock and the arguments. SimDLL's time is
    /// measured around the calls of its P/Invokes in Sim, SimMessages and
    /// ConduitTemperatureManager (RerouteNatives).
    public static class FrameStatsProbes
    {
        public static void Install(Harmony harmony)
        {
            G.Enabled = true;
            // The callers of SimDLL first, then the methods that call them (D21).
            RerouteNatives(harmony);
            Time(harmony, AccessTools.Method(typeof(JobManager), nameof(JobManager.DoNextWorkItem)), null, nameof(WorkItemTaken));
            Time(harmony, AccessTools.Method(typeof(JobManager), nameof(JobManager.Run)), nameof(JobsBegin), nameof(JobsEnd));
            Time(harmony, AccessTools.Method(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick)), nameof(BucketBegin), nameof(BucketEnd));
            Time(harmony, AccessTools.Method(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick)), nameof(BrainsBegin), nameof(BrainsEnd));
            Time(harmony, AccessTools.Method(typeof(AnimEventManager), nameof(AnimEventManager.Update), Type.EmptyTypes), nameof(AnimEventsBegin), nameof(AnimEventsEnd));
            Time(harmony, AccessTools.Method(typeof(AsyncPathProber.Manager), nameof(AsyncPathProber.Manager.TickFrame)), nameof(PathProbesBegin), nameof(PathProbesEnd));
            Time(harmony, AccessTools.Method(typeof(Pathfinding), nameof(Pathfinding.UpdateNavGrids)), nameof(NavGridsBegin), nameof(NavGridsEnd));
            Time(harmony, AccessTools.Method(typeof(World), nameof(World.UpdateCellInfo)), nameof(CellInfoBegin), nameof(CellInfoEnd));
            Time(harmony, AccessTools.Method(typeof(Game), "StepTheSim"), nameof(StepBegin), nameof(StepEnd));
            Time(harmony, AccessTools.Method(typeof(Game), "UnsafeSim200ms"), nameof(Sim200msBegin), nameof(Sim200msEnd));
            Time(harmony, AccessTools.Method(typeof(Game), "SimEveryTick"), nameof(SimEveryTickBegin), nameof(SimEveryTickEnd));
            if (Environment.GetEnvironmentVariable("ONI_BUCKET_PROFILE") == "1") BucketProfile.Install(harmony);
        }

        /// The window's lines (GameFrameStats.Take), with the game's speed setting.
        public static string[] Take(double seconds, int frames)
        {
            int speed = 0;
            try
            {
                var screen = SpeedControlScreen.Instance;
                if (screen != null) speed = screen.GetSpeed() + 1;
            }
            catch (Exception)
            {
                // The screen is half torn down (leaving a colony): no speed.
            }
            var lines = new System.Collections.Generic.List<string>(G.Take(seconds, frames, speed));
            if (lines.Count == 0) return lines.ToArray();
            lines.AddRange(BucketProfile.Take(frames));
            var ab = Flip();
            if (ab != null) lines.Add(ab);
            return lines.ToArray();
        }

        /// ONI_FRAME_STATS_AB=`TYPE, ASSEMBLY FIELD` (a static bool, such as a tweak's Bypass):
        /// test runs compare a change with itself switched off in one run. Each window's lines end
        /// with `frame stats ab: FIELD=VALUE` for the value it ran with; the field is then flipped
        /// for the next window.
        static readonly string abSpec = Environment.GetEnvironmentVariable("ONI_FRAME_STATS_AB");
        static FieldInfo abField;
        static bool abResolved;

        static string Flip()
        {
            if (string.IsNullOrEmpty(abSpec)) return null;
            if (!abResolved)
            {
                abResolved = true;
                int split = abSpec.LastIndexOf(' ');
                var type = split > 0 ? Type.GetType(abSpec.Substring(0, split).Trim()) : null;
                abField = type?.GetField(abSpec.Substring(split + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (abField == null || abField.FieldType != typeof(bool)) abField = null;
            }
            if (abField == null) return $"frame stats ab: no static bool field {abSpec}";
            bool value = (bool)abField.GetValue(null);
            abField.SetValue(null, !value);
            return $"frame stats ab: {abField.Name}={value}";
        }

        static HarmonyMethod Prefix(string name) => new HarmonyMethod(typeof(FrameStatsProbes), name) { priority = Priority.First };
        static HarmonyMethod Postfix(string name) => new HarmonyMethod(typeof(FrameStatsProbes), name) { priority = Priority.Last };

        static void Time(Harmony harmony, MethodInfo method, string begin, string end)
        {
            if (method == null) throw new MissingMethodException($"frame stats: a timed method of {begin ?? end} is missing");
            Patch(harmony, method, begin != null ? Prefix(begin) : null, Postfix(end));
        }

        static void Patch(Harmony harmony, MethodInfo method, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                harmony.Patch(method, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                global::Debug.Log($"[frame stats] {method.DeclaringType?.Name}.{method.Name} is not timed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// SimDLL's entry points the game calls while it plays, and the timed calls that stand in
        /// for them. Harmony cannot patch a P/Invoke that returns a value (the runtime rejects
        /// the replacement), so the methods of Sim, SimMessages and ConduitTemperatureManager
        /// that call one get a transpiler, after mods' (Priority.Last), that turns each such call
        /// into a call of the same P/Invoke between two clock reads.
        static readonly System.Collections.Generic.Dictionary<MethodInfo, MethodInfo> natives = new System.Collections.Generic.Dictionary<MethodInfo, MethodInfo>();

        static void RerouteNatives(Harmony harmony)
        {
            void Native(Type type, string name, string timed)
            {
                var method = AccessTools.Method(type, name);
                if (method == null) global::Debug.Log($"[frame stats] {type.Name}.{name} is missing: SimDLL's time leaves it out");
                else natives[method] = AccessTools.Method(typeof(FrameStatsProbes), timed);
            }
            Native(typeof(Sim), "SIM_HandleMessage", nameof(HandleMessage));
            Native(typeof(Sim), "SIM_HandleMessages", nameof(HandleMessages));
            Native(typeof(ConduitTemperatureManager), "ConduitTemperatureManager_Update", nameof(ConduitUpdate));
            Native(typeof(ConduitTemperatureManager), "ConduitTemperatureManager_Set", nameof(ConduitSet));
            var transpiler = new HarmonyMethod(typeof(FrameStatsProbes), nameof(Reroute)) { priority = Priority.Last };
            int rerouted = 0;
            foreach (var type in new[] { typeof(Sim), typeof(SimMessages), typeof(ConduitTemperatureManager) })
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetMethodBody() == null || method.ContainsGenericParameters || !CallsNative(method)) continue;
                    try
                    {
                        harmony.Patch(method, transpiler: transpiler);
                        rerouted++;
                    }
                    catch (Exception e)
                    {
                        global::Debug.Log($"[frame stats] {type.Name}.{method.Name}'s calls into SimDLL are not timed: {e.GetType().Name}: {e.Message}");
                    }
                }
            global::Debug.Log($"[frame stats] {rerouted} methods' calls into SimDLL timed");
        }

        /// Whether the method's IL has a `call` of one of the P/Invokes' tokens (all in the
        /// game's assembly; a byte match that is no call only costs a transpiler that changes
        /// nothing).
        static bool CallsNative(MethodInfo method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            foreach (var native in natives.Keys)
            {
                if (native.Module != method.Module) continue;
                int token = native.MetadataToken;
                for (int i = 0; i + 4 < il.Length; i++)
                    if (il[i] == 0x28 && BitConverter.ToInt32(il, i + 1) == token) return true;
            }
            return false;
        }

        static System.Collections.Generic.IEnumerable<CodeInstruction> Reroute(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == System.Reflection.Emit.OpCodes.Call) && instruction.operand is MethodInfo called && natives.TryGetValue(called, out var timed))
                    instruction.operand = timed;
                yield return instruction;
            }
        }

        // ConduitTemperatureManager's P/Invokes are private: the same exports, which the host
        // resolves for this assembly as for the game's.
        [DllImport("SimDLL", EntryPoint = "ConduitTemperatureManager_Update")]
        static extern IntPtr SimConduitUpdate(float dt, IntPtr buildingConductivity);

        [DllImport("SimDLL", EntryPoint = "ConduitTemperatureManager_Set")]
        static extern int SimConduitSet(int handle, float temperature, float mass, int element);

        static unsafe IntPtr HandleMessage(int id, int length, byte* message)
        {
            G.NativeBegin();
            var result = Sim.SIM_HandleMessage(id, length, message);
            G.NativeEnd();
            return result;
        }

        static unsafe IntPtr HandleMessages(int id, int length, int count, byte* messages)
        {
            G.NativeBegin();
            var result = Sim.SIM_HandleMessages(id, length, count, messages);
            G.NativeEnd();
            return result;
        }

        static IntPtr ConduitUpdate(float dt, IntPtr buildingConductivity)
        {
            G.NativeBegin();
            var result = SimConduitUpdate(dt, buildingConductivity);
            G.NativeEnd();
            return result;
        }

        static int ConduitSet(int handle, float temperature, float mass, int element)
        {
            G.NativeBegin();
            int result = SimConduitSet(handle, temperature, mass, element);
            G.NativeEnd();
            return result;
        }

        static void SimEveryTickBegin(float dt)
        {
            G.SimFrame(dt);
            G.Begin(G.SimEveryTick);
        }
        static void SimEveryTickEnd() => G.End(G.SimEveryTick);

        static void Sim200msBegin(float dt)
        {
            G.SimStep(dt);
            G.Begin(G.UnsafeSim200ms);
        }
        static void Sim200msEnd() => G.End(G.UnsafeSim200ms);

        static void StepBegin() => G.Begin(G.StepTheSim);
        static void StepEnd() => G.End(G.StepTheSim);
        static void CellInfoBegin() => G.Begin(G.CellInfo);
        static void CellInfoEnd() => G.End(G.CellInfo);
        static void NavGridsBegin() => G.Begin(G.NavGrids);
        static void NavGridsEnd() => G.End(G.NavGrids);
        static void PathProbesBegin() => G.Begin(G.PathProbes);
        static void PathProbesEnd() => G.End(G.PathProbes);
        static void BrainsBegin() => G.Begin(G.Brains);
        static void BrainsEnd() => G.End(G.Brains);
        static void AnimEventsBegin() => G.Begin(G.AnimEvents);
        static void AnimEventsEnd() => G.End(G.AnimEvents);

        static void BucketBegin(StateMachineUpdater.BucketGroup __instance) => G.Begin(G.FirstBucket + Bucket(__instance));
        static void BucketEnd(StateMachineUpdater.BucketGroup __instance) => G.End(G.FirstBucket + Bucket(__instance));

        static int Bucket(StateMachineUpdater.BucketGroup group)
        {
            int rate = (int)group.updateRate;
            return rate >= 0 && rate < G.Buckets ? rate : G.Buckets - 1;
        }

        /// The game's own JobManager.Run (fast-jobs off): the main thread found no item left, and
        /// waits for the workers from here until Run returns.
        static long workDone;

        static void WorkItemTaken(int threadIndex, bool __result)
        {
            if (!__result && threadIndex == 0 && G.OnMain) workDone = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        static void JobsBegin()
        {
            if (!G.OnMain) return;
            workDone = 0;
            G.Begin(G.Jobs);
        }

        static void JobsEnd()
        {
            if (!G.OnMain) return;
            if (workDone != 0) G.AddJobWait(workDone);
            workDone = 0;
            G.End(G.Jobs);
        }
    }
}
