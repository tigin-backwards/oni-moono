using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
    /// array instructions on a list read what they read on Mono too. `___field` parameters are
    /// compared with the field's type.
    ///
    /// A `___field` parameter whose class shares no base class but object with the field's (no
    /// object is both) gets null instead (issue #109). Pipe Color Overlay's postfix of
    /// ConduitMode.Update declares `HashSet<SaveLoadRoot> ___layerTargets`, a HybridListHashSet in
    /// U60: on Mono its HashSet calls read the other class's fields and throw
    /// IndexOutOfRangeException each frame, which Unity logs; on CoreCLR they read past the object
    /// (an access violation). With null the postfix throws a NullReferenceException of its own.
    /// Harmony emits the patch's arguments (EmitCallParameter); for such a patch the values from
    /// the first unrelated one on are stored in locals and loaded back with null in its place.
    /// Arguments, `__instance` and `ref` fields keep the real object, as on Mono: MaterialColor's
    /// `Tag[] tags`, given a HashSet<Tag>, only checks its length, which reads non-zero on both.
    internal static class PatchParameterTypes
    {
        static readonly HashSet<(MethodBase, Module, int)> checkedPatches = new();
        static readonly Dictionary<(MethodBase, Module, int), int[]> nulled = new();
        static readonly object gate = new();
        static Func<MethodInfo, IEnumerable<(ParameterInfo info, string realName)>>? originalParameters;
        static readonly HashSet<string> special = ["__originalMethod", "__args", "__result", "__resultRef", "__state", "__exception", "__runOriginal"];
        static FieldInfo config = null!, configOriginal = null!, configIl = null!;
        static bool installed;

        /// The mismatches found so far (for the self-check).
        public static int Found { get; private set; }

        /// A parameter of a patch method whose argument's declared type does not convert to its type.
        /// `Unrelated`: a `___field` (not by reference) of a class sharing no base class but object
        /// with the field's; such a parameter gets null.
        public readonly record struct MismatchedParameter(int Position, string Name, Type Declared, Type Argument, bool Unrelated)
        {
            public override string ToString() => $"`{Name}` is {Declared} and gets {Argument}";
        }

        /// The parameters Harmony emits for a patch method (MethodCreatorTools.EmitCallParameter):
        /// an unrelated `___field` one gets null; `harmony` is PatchFrames'.
        public static void Install(Harmony harmony)
        {
            if (installed) return;
            installed = true;
            var emit = AccessTools.Method("HarmonyLib.MethodCreatorTools:EmitCallParameter")
                ?? throw new MissingMethodException("HarmonyLib.MethodCreatorTools", "EmitCallParameter");
            config = AccessTools.Field("HarmonyLib.MethodCreator:config") ?? throw new MissingFieldException("HarmonyLib.MethodCreator", "config");
            configOriginal = AccessTools.Field("HarmonyLib.MethodCreatorConfig:original") ?? throw new MissingFieldException("HarmonyLib.MethodCreatorConfig", "original");
            configIl = AccessTools.Field("HarmonyLib.MethodCreatorConfig:il") ?? throw new MissingFieldException("HarmonyLib.MethodCreatorConfig", "il");
            harmony.Patch(emit, postfix: new HarmonyMethod(typeof(PatchParameterTypes), nameof(CallParameters)));
        }

        /// Before Harmony builds `original`'s replacement: `patch`, a mod's, is looked at once for it.
        public static void Check(MethodBase original, MethodInfo patch, string owner)
        {
            var key = (original, patch.Module, patch.MetadataToken);
            lock (gate)
            {
                if (!checkedPatches.Add(key)) return;
            }
            List<MismatchedParameter> mismatches;
            try
            {
                mismatches = Mismatches(original, patch);
            }
            catch (Exception e) // a type that does not load: Harmony reports it as it builds
            {
                EngineLog.Engine($"patch parameter types: {patch.DeclaringType?.FullName}.{patch.Name} not checked ({e.GetType().Name})");
                return;
            }
            if (mismatches.Count == 0) return;
            var nulls = new List<int>();
            var related = new List<MismatchedParameter>();
            foreach (var m in mismatches)
                if (m.Unrelated) nulls.Add(m.Position);
                else related.Add(m);
            lock (gate)
            {
                Found++;
                if (nulls.Count > 0) nulled[key] = [.. nulls];
            }
            string what = $"patch parameter types: {patch.Module.Assembly.GetName().Name} ({owner}) {patch.DeclaringType?.FullName}.{patch.Name} on "
                + $"{original.DeclaringType?.FullName}.{original.Name}: ";
            if (nulls.Count > 0)
                EngineLog.Engine(what + string.Join(", ", mismatches.Where(m => m.Unrelated))
                    + "; fields of a class unrelated to theirs, so the patch gets null for them");
            if (related.Count > 0)
            {
                bool kept = PatchInliners.KeepCalls(patch);
                EngineLog.Engine(what + string.Join(", ", related) + "; "
                    + (kept ? "compiled with no method inlined into it, as on Mono" : "left as it is (no JIT hook here)"));
            }
        }

        /// The first parameter of `patch` whose type its argument's declared type does not convert to,
        /// described; else null.
        public static string? Mismatch(MethodBase original, MethodInfo patch) => Mismatches(original, patch) is [var first, ..] ? first.ToString() : null;

        /// The parameters of `patch` whose type their argument's declared type does not convert to:
        /// `__instance`, `__N`, the original's names, `[HarmonyArgument]` and `___field` (by Harmony's
        /// rules).
        public static List<MismatchedParameter> Mismatches(MethodBase original, MethodInfo patch)
        {
            originalParameters ??= (Func<MethodInfo, IEnumerable<(ParameterInfo, string)>>)Delegate.CreateDelegate(
                typeof(Func<MethodInfo, IEnumerable<(ParameterInfo, string)>>),
                AccessTools.Method("HarmonyLib.MethodPatcherTools:OriginalParameters", [typeof(MethodInfo)])
                    ?? throw new MissingMethodException("HarmonyLib.MethodPatcherTools", "OriginalParameters"));
            ParameterInfo[]? originals = null;
            var found = new List<MismatchedParameter>();
            foreach (var (parameter, realName) in originalParameters(patch))
            {
                Type? argument;
                bool field = false;
                if (realName == "__instance")
                    argument = original.IsStatic ? null : original.DeclaringType;
                else if (special.Contains(realName))
                    continue;
                else if (realName.StartsWith("___", StringComparison.Ordinal))
                {
                    // Harmony's lookup (EmitCallParameter): by index if all digits, else by name.
                    string name = realName.Substring(3);
                    var info = name.Length > 0 && name.All(char.IsDigit) ? AccessTools.DeclaredField(original.DeclaringType, int.Parse(name))
                        : AccessTools.Field(original.DeclaringType, name);
                    argument = info?.FieldType;
                    field = true;
                }
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
                bool byRef = declared.IsByRef;
                if (byRef) declared = declared.GetElementType()!;
                if (argument.IsByRef) argument = argument.GetElementType()!;
                if (Holds(declared, argument)) continue;
                bool unrelated = field && !byRef && Unrelated(declared, argument);
                found.Add(new MismatchedParameter(parameter.Position, parameter.Name ?? realName, declared, argument, unrelated));
            }
            return found;
        }

        /// Two classes (arrays among them) whose only common base class is object: no object is
        /// both, and none of a base class's members reads the same in both. An interface on either
        /// side may be the object's.
        static bool Unrelated(Type declared, Type argument)
        {
            if (declared.IsInterface || argument.IsInterface || argument.IsAssignableFrom(declared)) return false;
            for (var b = declared.BaseType; b != null && b != typeof(object); b = b.BaseType)
                if (b.IsAssignableFrom(argument)) return false;
            return true;
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

        /// After Harmony emits the arguments of a call of `patch` (one value per parameter, but the
        /// first when a postfix passes its result through): from the first unrelated parameter on,
        /// they are stored in locals and loaded back, each unrelated one replaced by null. Patches
        /// without one are not looked at.
        static void CallParameters(object __0, MethodInfo __1, bool __2, List<CodeInstruction> __result)
        {
            if (__result == null || __0 == null || __1 == null) return;
            var settings = config.GetValue(__0);
            if (configOriginal.GetValue(settings) is not MethodBase original) return;
            int[]? nulls;
            lock (gate)
            {
                if (nulled.Count == 0 || !nulled.TryGetValue((original, __1.Module, __1.MetadataToken), out nulls)) return;
            }
            var parameters = __1.GetParameters();
            int start = __2 && __1.ReturnType != typeof(void) && parameters.Length > 0 && parameters[0].ParameterType == __1.ReturnType ? 1 : 0;
            int first = int.MaxValue;
            foreach (int n in nulls)
                if (n >= start && n < parameters.Length) first = Math.Min(first, n);
            if (first == int.MaxValue) return;
            var generator = (ILGenerator)configIl.GetValue(settings)!;
            var locals = new LocalBuilder?[parameters.Length];
            for (int j = parameters.Length - 1; j >= first; j--)
            {
                if (Array.IndexOf(nulls, j) >= 0)
                    __result.Add(new CodeInstruction(OpCodes.Pop));
                else
                {
                    locals[j] = generator.DeclareLocal(parameters[j].ParameterType);
                    __result.Add(new CodeInstruction(OpCodes.Stloc, locals[j]));
                }
            }
            for (int j = first; j < parameters.Length; j++)
                __result.Add(locals[j] is { } kept ? new CodeInstruction(OpCodes.Ldloc, kept) : new CodeInstruction(OpCodes.Ldnull));
        }
    }
}
