using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// MonoMod's warnings, errors and the recompiles whose detour it does not move or overruns, in
    /// Player.log (src/OniMoono.Host/MonoModLog.cs, compiled in; issue #127).
    public class MonoModLogTests
    {
        [Fact]
        public void LinesOfAKindAreCappedAndCounted()
        {
            var limiter = new MonoModLog.Limiter(perKind: 2, total: 5);
            var taken = new List<string>();
            for (int i = 0; i < 3000; i++)
                if (limiter.Take($"monomod: A was recompiled to 0x{0x7d68c3d72b38 + i:x} ({i})", out int n) is { } line) taken.Add($"{n} {line}");
            // Two, then the 1,000th, 2,000th and 3,000th of the kind; numbers do not make a new kind.
            Assert.Equal(["1", "2", "1000", "2000", "3000"], taken.Select(t => t.Split(' ')[0]));
            Assert.Equal("monomod log: 5 lines written; no more this session", limiter.Take("another kind", out _));
            Assert.Null(limiter.Take("yet another kind", out _));
        }

        [Fact]
        public void SkippedMovesAreNamed()
        {
            Assert.Null(MonoModLog.Skipped(applied: false, applying: true, ownApply: false, codeSize: 1));
            // MonoMod compiling the method it is detouring, on the applying thread: as it should.
            Assert.Null(MonoModLog.Skipped(applied: true, applying: true, ownApply: true, codeSize: 200));
            Assert.Contains("another thread", MonoModLog.Skipped(applied: true, applying: true, ownApply: false, codeSize: 200));
            Assert.Contains("1 byte(s)", MonoModLog.Skipped(applied: true, applying: false, ownApply: false, codeSize: 1));
            Assert.Null(MonoModLog.Skipped(applied: true, applying: false, ownApply: false, codeSize: MonoModLog.LongestJump));
        }

        /// `class Tracker { public virtual void UpdateCell(int, int) { } }`, emitted: this test
        /// assembly is a debug build, whose methods never tier up.
        static Type EmitTracker()
        {
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Tests.MonoModLogTracker"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Tracker");
            var type = module.DefineType("Tracker", TypeAttributes.Public);
            var method = type.DefineMethod("UpdateCell", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                typeof(void), [typeof(int), typeof(int)]);
            method.SetImplementationFlags(MethodImplAttributes.NoInlining);
            method.GetILGenerator().Emit(OpCodes.Ret);
            type.DefineDefaultConstructor(MethodAttributes.Public);
            return type.CreateType();
        }

        static int prefixRuns;
        static bool Counted()
        {
            Interlocked.Increment(ref prefixRuns);
            return false;
        }

        /// An empty patched method tiers up to a 1-byte `ret`; MonoMod's move then writes a longer
        /// jump into it, which the log names (OffsetTracker.UpdateCell's case in the game).
        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void ATinyRecompiledMethodIsLogged()
        {
            var lines = new List<string>();
            var previous = MonoModLog.Sink;
            MonoModLog.Sink = l => { lock (lines) lines.Add(l); };
            var harmony = new Harmony("OniMoono.Tests.MonoModLog");
            try
            {
                MonoModLog.Install();
                var tracker = EmitTracker();
                var update = tracker.GetMethod("UpdateCell")!;
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(MonoModLogTests), nameof(Counted)));
                var call = (Action<int, int>)Delegate.CreateDelegate(typeof(Action<int, int>), Activator.CreateInstance(tracker)!, update);
                var clock = Stopwatch.StartNew();
                bool Seen() { lock (lines) return lines.Any(l => l.Contains("Tracker.UpdateCell was recompiled") && l.Contains("byte(s)")); }
                while (!Seen() && clock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    for (int i = 0; i < 1000; i++) call(i, i + 1);
                    Thread.Sleep(10);
                }
                lock (lines) Assert.True(Seen(), "no line for the recompile; lines: " + string.Join(" | ", lines));
                // The jump MonoMod wrote into the 1-byte code still leads to the patches.
                int before = prefixRuns;
                for (int i = 0; i < 1000; i++) call(i, i + 1);
                Assert.Equal(before + 1000, prefixRuns);
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
                MonoModLog.Sink = previous;
            }
        }
    }
}
