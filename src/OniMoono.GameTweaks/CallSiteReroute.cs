using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Transpilers that send a game method's calls to generic helpers (Mode.AddTargetIfVisible and
    /// the like) to a tweak's versions of them.
    ///
    /// Harmony builds a patched method from the original IL each time, running every owner's
    /// transpilers on it in order of priority, highest first. Mods transpile the same call sites:
    /// Fast Track's CullConduits replaces the overlay modes' AddTargetIfVisible calls with its own,
    /// which also shows the conduits it hid inside solid tiles, and it finds the call by its exact
    /// target. Our tweaks install before any mod loads, so at an equal priority ours would run
    /// first, and the mod's transpiler would find no call left to replace: the conduits it hid
    /// stayed hidden in the overlays (issue #41). Ours run last instead: a mod's replacement is
    /// made first, and ours then rewrites only the calls still left to the game's helpers.
    public static class CallSiteReroute
    {
        /// The priority of the tweaks' call-site transpilers: after every other owner's.
        public const int TranspilerPriority = Priority.Last;

        /// A transpiler of `type`'s method `name`, run after every other owner's.
        [NameLookupReviewed("A tweak's own transpiler, by its name")]
        public static HarmonyMethod Transpiler(Type type, string name) => new(type, name) { priority = TranspilerPriority };

        /// The instructions, with each call to an instance of a generic method definition that
        /// `replacementFor` maps to another definition sent to that one (a static call with the
        /// same type arguments). Calls it maps to null are left alone. How many were sent is
        /// reported for `tweak` (TranspilerCounts): fewer than it declares only when a mod's
        /// transpiler took some first.
        public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, Func<MethodInfo, MethodInfo> replacementFor, string tweak = null, MethodBase original = null)
        {
            int rewritten = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo m && m.IsGenericMethod
                    && replacementFor(m.GetGenericMethodDefinition()) is { } replacement)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement.MakeGenericMethod(m.GetGenericArguments());
                    rewritten++;
                }
                yield return instruction;
            }
            if (tweak != null) OniMoono.Tweaks.TranspilerCounts.Report(tweak, original, rewritten);
        }
    }
}
