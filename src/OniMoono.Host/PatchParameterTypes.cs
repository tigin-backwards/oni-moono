using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE D65. Harmony passes a patched method's arguments to a patch method as they are:
    /// it checks `__result`'s type, but not a parameter's, so a patch may declare a parameter of a
    /// type its argument is not. New Skills 2.0.0 prefixes `Navigator.GetNavigationCost(int,
    /// CellOffset[])`, an overload U60 removed: Harmony's lookup binds it to the
    /// `IReadOnlyList<CellOffset>` overload (an array converts to it), and the game passes a
    /// `List<CellOffset>` to a prefix that declares `CellOffset[]`. The prefix hands it on to a
    /// method taking an `IReadOnlyList<CellOffset>`. Mono's JIT inlines only methods under 20 bytes
    /// of IL, so that method runs on its own and calls the list's interface methods on the list:
    /// the mod works in the original player (the array prefix runs first and does the work, given a
    /// List, measured with the mod and a probe). CoreCLR's JIT inlines the method into the prefix, takes the
    /// declared array type as the argument's and calls the array's `Count` and indexer on the list
    /// (issues #101 and #102: an AccessViolationException 2 to 5 s into a colony).
    ///
    /// Before Harmony builds a replacement, each mod patch method's parameters are compared with
    /// the arguments they get (by Harmony's own rules: `__instance`, `__N`, the original's names
    /// and `[HarmonyArgument]`). A reference type that the argument's declared type does not
    /// convert to is a mismatch: the JIT then compiles that patch method with no method inlined
    /// into it (PatchInliners.KeepCalls), as Mono compiles it but for its smallest callees, and one
    /// line names the mod's assembly, its Harmony ID and the patch. Patch methods are never inlined
    /// themselves (D19). What the patch method's own IL does with the parameter is left as it is:
    /// array instructions on a list read what they read on Mono too.
    internal static class PatchParameterTypes
    {
        static readonly HashSet<MethodInfo> checkedPatches = new();
        static readonly object gate = new();
        static Func<MethodInfo, IEnumerable<(ParameterInfo info, string realName)>>? originalParameters;
        static readonly HashSet<string> special = ["__originalMethod", "__args", "__result", "__resultRef", "__state", "__exception", "__runOriginal"];

        /// The mismatches found so far (for the self-check).
        public static int Found { get; private set; }

        /// Before Harmony builds `original`'s replacement: `patch`, a mod's, is looked at once.
        public static void Check(MethodBase original, MethodInfo patch, string owner)
        {
            lock (gate)
            {
                if (!checkedPatches.Add(patch)) return;
            }
            string? mismatch;
            try
            {
                mismatch = Mismatch(original, patch);
            }
            catch (Exception e) // a type that does not load: Harmony reports it as it builds
            {
                EngineLog.Engine($"patch parameter types: {patch.DeclaringType?.FullName}.{patch.Name} not checked ({e.GetType().Name})");
                return;
            }
            if (mismatch == null) return;
            lock (gate) Found++;
            bool kept = PatchInliners.KeepCalls(patch);
            EngineLog.Engine($"patch parameter types: {patch.Module.Assembly.GetName().Name} ({owner}) {patch.DeclaringType?.FullName}.{patch.Name} on "
                + $"{original.DeclaringType?.FullName}.{original.Name}: {mismatch}; "
                + (kept ? "compiled with no method inlined into it, as on Mono" : "left as it is (no JIT hook here)"));
        }

        /// The first parameter of `patch` whose type its argument's declared type does not convert to,
        /// described; else null.
        public static string? Mismatch(MethodBase original, MethodInfo patch)
        {
            originalParameters ??= (Func<MethodInfo, IEnumerable<(ParameterInfo, string)>>)Delegate.CreateDelegate(
                typeof(Func<MethodInfo, IEnumerable<(ParameterInfo, string)>>),
                AccessTools.Method("HarmonyLib.MethodPatcherTools:OriginalParameters", [typeof(MethodInfo)])
                    ?? throw new MissingMethodException("HarmonyLib.MethodPatcherTools", "OriginalParameters"));
            ParameterInfo[]? originals = null;
            foreach (var (parameter, realName) in originalParameters(patch))
            {
                Type? argument;
                if (realName == "__instance")
                    argument = original.IsStatic ? null : original.DeclaringType;
                else if (special.Contains(realName) || realName.StartsWith("___", StringComparison.Ordinal))
                    continue;
                else
                {
                    originals ??= original.GetParameters();
                    int index = -1;
                    if (realName.StartsWith("__", StringComparison.Ordinal))
                        int.TryParse(realName.AsSpan(2), out index);
                    else if (parameter.GetCustomAttribute<HarmonyArgument>() is { Index: >= 0 } byIndex)
                        index = byIndex.Index;
                    else
                        for (int i = 0; i < originals.Length; i++)
                            if (originals[i].Name == realName) index = i;
                    argument = index >= 0 && index < originals.Length ? originals[index].ParameterType : null;
                }
                if (argument == null) continue;
                var declared = parameter.ParameterType;
                if (declared.IsByRef) declared = declared.GetElementType()!;
                if (argument.IsByRef) argument = argument.GetElementType()!;
                if (Holds(declared, argument)) continue;
                return $"`{parameter.Name}` is {declared} and gets {argument}";
            }
            return null;
        }

        /// Whether a parameter of type `declared` holds every argument of type `argument`, as far as
        /// the JIT is concerned: value types (boxed by Harmony, or the same) and open generics are
        /// not looked at.
        static bool Holds(Type declared, Type argument)
        {
            if (declared.IsValueType || argument.IsValueType || declared.IsPointer || argument.IsPointer) return true;
            if (declared.ContainsGenericParameters || argument.ContainsGenericParameters) return true;
            return declared.IsAssignableFrom(argument);
        }
    }
}
