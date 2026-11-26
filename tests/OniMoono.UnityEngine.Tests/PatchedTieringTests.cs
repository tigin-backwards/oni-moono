using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// Methods Harmony patches before their first compile are kept off tier-up, so MonoMod never
    /// moves their detour (src/OniMoono.Host/PatchedTiering.cs, compiled in; issue #127).
    public class PatchedTieringTests
    {
        /// `class Tracker { virtual void UpdateCell(int) { } virtual void CompiledFirst(int) { }
        /// static int Sum(int n) { loop } }`, emitted: this test assembly is a debug build, whose
        /// methods never tier up.
        static Type EmitTracker(string name)
        {
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Tests." + name), AssemblyBuilderAccess.Run)
                .DefineDynamicModule(name);
            var type = module.DefineType("Tracker", TypeAttributes.Public);
            foreach (var method in new[] { "UpdateCell", "CompiledFirst" })
                type.DefineMethod(method, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(void), [typeof(int)])
                    .GetILGenerator().Emit(OpCodes.Ret);
            var sum = type.DefineMethod("Sum", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
            var il = sum.GetILGenerator();
            var total = il.DeclareLocal(typeof(int));
            var i = il.DeclareLocal(typeof(int));
            var test = il.DefineLabel();
            var body = il.DefineLabel();
            il.Emit(OpCodes.Br, test);
            il.MarkLabel(body);
            il.Emit(OpCodes.Ldloc, total); il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, total);
            il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, i);
            il.MarkLabel(test);
            il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Blt, body);
            il.Emit(OpCodes.Ldloc, total); il.Emit(OpCodes.Ret);
            type.DefineDefaultConstructor(MethodAttributes.Public);
            return type.CreateType();
        }

        static int updates, firsts, sums;
        static void CountUpdate() => Interlocked.Increment(ref updates);
        static void CountFirst() => Interlocked.Increment(ref firsts);
        static void CountSum() => Interlocked.Increment(ref sums);

        static readonly List<IntPtr> compiled = new();
        /// Prefix of MonoMod's JIT hook handler (PlatformTripleDetourFactory.Detour.OnMethodCompiled):
        /// every method compiled.
        static void Compiled(RuntimeMethodHandle methodHandle)
        {
            lock (compiled) compiled.Add(methodHandle.Value);
        }

        static int CompilesOf(MethodBase method)
        {
            var handle = method.MethodHandle.Value;
            lock (compiled) return compiled.FindAll(h => h == handle).Count;
        }

        /// An empty virtual method (OffsetTracker.UpdateCell's shape, which tier 1 compiles to a
        /// 1-byte `ret`) and a loop, patched before their first call, are compiled once and never
        /// again while a method compiled before its patch tiers up beside them; every call of each
        /// runs its prefix.
        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void MethodsPatchedBeforeTheirFirstCallNeverRecompile()
        {
            var lines = new List<string>();
            PatchedTiering.Sink = l => { lock (lines) lines.Add(l); };
            PatchedTiering.Install();
            Assert.True(PatchedTiering.Available, "off: " + string.Join(" | ", lines));
            var harmony = new Harmony("OniMoono.Tests.PatchedTiering");
            var hook = AccessTools.Method("MonoMod.Core.Platforms.PlatformTripleDetourFactory+Detour:OnMethodCompiled");
            Assert.NotNull(hook);
            try
            {
                var tracker = EmitTracker("PatchedTieringTracker");
                var update = tracker.GetMethod("UpdateCell")!;
                var first = tracker.GetMethod("CompiledFirst")!;
                var sum = tracker.GetMethod("Sum")!;
                var instance = Activator.CreateInstance(tracker)!;
                var callUpdate = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), instance, update);
                var callFirst = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), instance, first);
                var callSum = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), sum);
                callFirst(0);
                Assert.True(PatchedTiering.Eligible(update) && PatchedTiering.Eligible(sum) && PatchedTiering.Eligible(first));

                Assert.Equal(PatchedTiering.Outcome.Kept, PatchedTiering.Keep(update));
                Assert.Equal(PatchedTiering.Outcome.CompiledFirst, PatchedTiering.Keep(first));
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(PatchedTieringTests), nameof(CountUpdate)));
                harmony.Patch(first, prefix: new HarmonyMethod(typeof(PatchedTieringTests), nameof(CountFirst)));
                harmony.Patch(sum, prefix: new HarmonyMethod(typeof(PatchedTieringTests), nameof(CountSum)));
                Assert.False(PatchedTiering.Eligible(update));
                Assert.False(PatchedTiering.Eligible(sum));
                Assert.True(PatchedTiering.Eligible(first));
                // Compiles from here on: MonoMod compiled the patched methods while patching.
                harmony.Patch(hook, prefix: new HarmonyMethod(typeof(PatchedTieringTests), nameof(Compiled)));

                int calls = 0;
                int u0 = updates, f0 = firsts, s0 = sums;
                void Calls()
                {
                    for (int i = 0; i < 100; i++)
                    {
                        callUpdate(i);
                        callFirst(i);
                        Assert.Equal(4950, callSum(100));
                        calls++;
                    }
                }
                // Until the method compiled first is recompiled (tier 1 after 30 calls, once 100 ms
                // pass without new tier-0 compiles, in the background), then a while longer.
                var clock = Stopwatch.StartNew();
                while (CompilesOf(first) == 0 && clock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Calls();
                    Thread.Sleep(10);
                }
                Assert.True(CompilesOf(first) > 0, "the method compiled before its patch was never recompiled");
                var more = Stopwatch.StartNew();
                while (more.Elapsed < TimeSpan.FromSeconds(1))
                {
                    Calls();
                    Thread.Sleep(10);
                }
                Assert.Equal(0, CompilesOf(update));
                Assert.Equal(0, CompilesOf(sum));
                // Every call ran its prefix, the method compiled first's through its moved detour too.
                Assert.Equal(calls, updates - u0);
                Assert.Equal(calls, firsts - f0);
                Assert.Equal(calls, sums - s0);
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
            }
        }

        /// Stubs and methods with no code slot are left as they are.
        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void DynamicMethodsAreNotTiered()
        {
            var dynamic = new DynamicMethod("Probe", typeof(int), Type.EmptyTypes);
            dynamic.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
            Assert.Equal(PatchedTiering.Outcome.NotTiered, PatchedTiering.Keep(dynamic));
            // This test assembly is a debug build: its methods are not eligible.
            Assert.Equal(PatchedTiering.Outcome.NotTiered, PatchedTiering.Keep(typeof(PatchedTieringTests).GetMethod(nameof(CountSum), BindingFlags.NonPublic | BindingFlags.Static)!));
        }
    }
}
