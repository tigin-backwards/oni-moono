using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `low-fps-timing`'s leak part (**changes timing**; LowFpsTiming): the window
    /// that lets a damaged tile leak liquid at most once a second runs on game time, not real time.
    ///
    /// WorldDamage.ApplyDamage, on a solid tile damaged past 0.15 with more than 1 kg of liquid
    /// behind it, spills 1 kg into the open cell on the other side unless that cell is in
    /// `spawnTimes`, then records it there with Time.realtimeSinceStartup. WorldDamage.Update
    /// (every frame) drops entries more than 1 s old by the same clock. So a cell leaks at most
    /// once a real second: at speed 3 a third as often a game second as at speed 1, less again
    /// with speed mods (case 12 of docs/research/frame-rate-dependence.md).
    ///
    /// With the tweak, a transpiler on each method reads Now instead: game time from GameClock
    /// (LeakClock), so a cell leaks at most once a game second at any speed. At speed 1 that is
    /// the game's window in game-time terms. Paused, game time stands still and so does the
    /// window, but nothing damages tiles while paused (every caller runs on the sim's updates,
    /// or the sim's own damage reports), so paused frames are as before.
    ///
    /// This part is off for the session once another Harmony owner patches ApplyDamage or Update
    /// other than with a postfix (it could keep its own times there; checked at the first frame,
    /// then every 4,096); Now then reads real time again, and the window's entries are dropped
    /// once so none outlives the switch.
    [GameMember(GameUse.Transpiler, "System.Single WorldDamage::ApplyDamage(System.Int32,System.Single,System.Int32,WorldDamage/DamageType,System.String,System.String)", "3714495a239e4f3f linux-744825 win-744825", "3cd9c1956334858e linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", "230acd3635ab26b2 linux-763796 win-763796", Rewrites = 1)]
    [GameMember(GameUse.Transpiler, "System.Void WorldDamage::Update()", "811199d1817d3c88 linux-744825 win-744825 linux-763796 win-763796", "3b23da7dd5d935ec linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", Rewrites = 1)]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<System.Int32,System.Single> WorldDamage::spawnTimes")]
    [GameMember(GameUse.Uses, "GameClock GameClock::Instance")]
    [GameMember(GameUse.Uses, "System.Single GameClock::timeSinceStartOfCycle")]
    [GameMember(GameUse.Uses, "System.Int32 GameClock::cycle")]
    public static class LeakTiming
    {
        /// The tweak this is a part of (its game members are declared under that name), and the
        /// part as Player.log names it.
        public const string Name = LowFpsTiming.Name, Part = "leak timing";

        static MethodBase[] guarded;
        static LeakClock clock;
        static float last;
        static long frames;
        static bool off;

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            var transpiler = new HarmonyMethod(typeof(LeakTiming), nameof(GameTime));
            foreach (var method in guarded) harmony.Patch(method, transpiler: transpiler);
        }

        /// The methods another owner's patch (but a postfix) makes this part stand down (ModOverlaps).
        internal static MethodBase[] Guarded() =>
            new MethodBase[]
            {
                AccessTools.Method(typeof(WorldDamage), nameof(WorldDamage.ApplyDamage),
                    new[] { typeof(int), typeof(float), typeof(int), typeof(WorldDamage.DamageType), typeof(string), typeof(string) }),
                AccessTools.Method(typeof(WorldDamage), "Update"),
            };

        internal static ProbeRepairPolicy.GuardedKinds KindsOf(MethodBase method) => ProbeRepairPolicy.GuardedKinds.AllButPostfixes;

        /// Each Time.realtimeSinceStartup in the method becomes Now (ApplyDamage) or Expiring (Update).
        static IEnumerable<CodeInstruction> GameTime(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var real = AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup));
            var now = AccessTools.Method(typeof(LeakTiming), original.Name == "Update" ? nameof(Expiring) : nameof(Now));
            int found = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, real))
                {
                    instruction.operand = now;
                    found++;
                }
                yield return instruction;
            }
            // One read in each (TweakInstall checks the count).
            TranspilerCounts.Report(Name, original, found);
        }

        /// The leak window's clock: game time, or real time once this part is off.
        static float Now()
        {
            if (off) return Time.realtimeSinceStartup;
            var c = GameClock.Instance;
            if (c != null) last = clock.Seconds(c, c.timeSinceStartOfCycle, c.cycle);
            return last;
        }

        /// The clock as WorldDamage.Update reads it, once a frame: the stand-down check first.
        static float Expiring()
        {
            if (!off && (frames++ & 0xFFF) == 0 && ForeignPatches.Find(guarded, ProbeRepairPolicy.GuardedKinds.AllButPostfixes) is { } why)
            {
                off = true;
                WorldDamage.Instance?.spawnTimes.Clear();
                global::Debug.Log($"{Name}: {Part} off ({why})");
            }
            return Now();
        }
    }
}
