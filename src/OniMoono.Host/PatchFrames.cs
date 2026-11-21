using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace OniMoono.Host
{
    /// ARCHITECTURE D19. Harmony calls a mod's prefixes, postfixes and finalizers from the
    /// replacement method it builds for the patched method, a dynamic method owned by 0Harmony.
    /// CoreCLR's JIT inlines a small patch method into it, so the patch method's frame is gone, and
    /// what it calls sees 0Harmony as its caller: Assembly.GetCallingAssembly() names 0Harmony,
    /// whose folder is not the mod's (Traffic Visualizer's SquareLib finds its icons from it, and the
    /// game stopped loading). On Mono the patch method keeps its frame, as measured with the mod in
    /// the original player. The host marks every patch method not to be inlined (MonoMod's own
    /// TryDisableInlining, as Harmony does for the patched method) before Harmony builds the
    /// replacement; this project's own patches (the game tweaks, the host's) keep being inlined.
    ///
    /// The replacement's own frame goes the same way when its last call is a patch method: a
    /// postfix of a method returning nothing is called right before the replacement's ret, and
    /// CoreCLR's optimizing JIT (Harmony compiles replacements fully optimized) makes that call a
    /// tail call, so the replacement leaves the stack before the postfix runs. Mono makes no
    /// implicit tail calls: a TypeLoadException from Cold Wheat Seed Drop's postfix of
    /// SeedProducer.CropPicked shows the replacement as its first frame in the original player,
    /// and the game's lambda that called it in ours. Each such call of a mod's patch method is
    /// followed by `ldc.i4.0; pop` (no code once compiled), which takes it out of tail position.
    internal static class PatchFrames
    {
        static bool installed;
        static Func<MethodBase, bool> disableInlining = null!;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = typeof(Harmony).Assembly;
            var triple = harmony.GetType("MonoMod.Core.Platforms.PlatformTriple", true)!;
            var current = triple.GetProperty("Current")!.GetValue(null)!;
            var tryDisable = triple.GetMethod("TryDisableInlining", [typeof(MethodBase)])
                ?? throw new MissingMethodException(triple.FullName, "TryDisableInlining");
            disableInlining = (Func<MethodBase, bool>)Delegate.CreateDelegate(typeof(Func<MethodBase, bool>), current, tryDisable);
            var updateWrapper = AccessTools.Method("HarmonyLib.PatchFunctions:UpdateWrapper")
                ?? throw new MissingMethodException("HarmonyLib.PatchFunctions", "UpdateWrapper");
            var harmonyPatches = new Harmony("OniMoono.Host.PatchFrames");
            harmonyPatches.Patch(updateWrapper, prefix: new HarmonyMethod(typeof(PatchFrames), nameof(Prefix)), finalizer: new HarmonyMethod(typeof(PatchFrames), nameof(Finalizer)));
            // The replacement's whole body, last of all, before it is emitted (MethodCreator.CreateReplacement).
            var rewrite = AccessTools.Method("HarmonyLib.FaultBlockRewriter:Rewrite")
                ?? throw new MissingMethodException("HarmonyLib.FaultBlockRewriter", "Rewrite");
            harmonyPatches.Patch(rewrite, postfix: new HarmonyMethod(typeof(PatchFrames), nameof(RewritePostfix)));
            PatchParameterTypes.Install(harmonyPatches); // D65: unrelated classes get null
        }

        /// The mods' patch methods of the replacement UpdateWrapper is building on this thread.
        [ThreadStatic] static HashSet<(Module, int)>? building;

        static void Prefix(MethodBase original, PatchInfo patchInfo)
        {
            var mods = new HashSet<(Module, int)>();
            foreach (var patches in new[] { patchInfo.prefixes, patchInfo.postfixes, patchInfo.finalizers, patchInfo.innerprefixes, patchInfo.innerpostfixes })
                foreach (var patch in patches)
                    if (patch.PatchMethod is { } method and not DynamicMethod && !Ours(method.Module.Assembly.GetName().Name))
                    {
                        disableInlining(method);
                        mods.Add((method.Module, method.MetadataToken));
                        if (original != null) PatchParameterTypes.Check(original, method, patch.owner); // D65
                    }
            building = mods;
        }

        static Exception? Finalizer(Exception? __exception)
        {
            building = null;
            return __exception;
        }

        /// A call of a mod's patch method that the replacement's ret follows (past nops) gets a
        /// stack-neutral pair after it, so the JIT does not make it a tail call.
        static void RewritePostfix(List<CodeInstruction> __result)
        {
            if (building is not { Count: > 0 } mods || __result == null) return;
            for (int i = 0; i < __result.Count; i++)
            {
                var call = __result[i];
                // A transpiler's call of a dynamic method (CodeInstruction.CallClosure) is no patch
                // method, and its MetadataToken throws on CoreCLR (Better Info Cards' transpiler
                // of a method Blueprints Expanded prefixes).
                if (call.opcode != OpCodes.Call && call.opcode != OpCodes.Callvirt || call.operand is not MethodInfo { } target || target is DynamicMethod) continue;
                if (!mods.Contains((target.Module, target.MetadataToken))) continue;
                int next = i + 1;
                while (next < __result.Count && __result[next].opcode == OpCodes.Nop) next++;
                if (next >= __result.Count || __result[next].opcode != OpCodes.Ret) continue;
                __result.Insert(i + 1, new CodeInstruction(OpCodes.Ldc_I4_0));
                __result.Insert(i + 2, new CodeInstruction(OpCodes.Pop));
                i += 2;
            }
        }

        /// The host (OniMoono) and the engine's assemblies (OniMoono.*).
        static bool Ours(string? name) => name?.StartsWith("OniMoono", StringComparison.Ordinal) == true;
    }
}
