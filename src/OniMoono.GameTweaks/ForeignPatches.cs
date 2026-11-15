using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// Every tweak's stand-down check: whether a Harmony owner that is not ours
    /// (ModOverlapsCore.IsForeign) patches a method the tweak replaces or relies on. The tweaks
    /// call it from patched methods, at their recheck intervals.
    public static class ForeignPatches
    {
        /// The first foreign patch of `method` that counts under `kinds`, as
        /// "OWNER patches Type.Method", or null. Inner patches count as transpilers: they change
        /// calls inside the body.
        public static string Find(MethodBase method, ProbeRepairPolicy.GuardedKinds kinds = ProbeRepairPolicy.GuardedKinds.All)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return null;
            string owner = First(info.Finalizers, PatchKind.Finalizer, kinds) ?? First(info.Prefixes, PatchKind.Prefix, kinds)
                ?? First(info.Transpilers, PatchKind.Transpiler, kinds) ?? First(info.InnerPrefixes, PatchKind.Transpiler, kinds)
                ?? First(info.InnerPostfixes, PatchKind.Transpiler, kinds) ?? First(info.Postfixes, PatchKind.Postfix, kinds);
            return owner == null ? null : $"{owner} patches {method.DeclaringType?.Name}.{method.Name}";
        }

        /// The first foreign patch of any of `methods`, each under `kinds`. A method that is
        /// null is passed over.
        public static string Find(MethodBase[] methods, ProbeRepairPolicy.GuardedKinds kinds = ProbeRepairPolicy.GuardedKinds.All)
        {
            for (int i = 0; i < methods.Length; i++)
                if (methods[i] != null && Find(methods[i], kinds) is { } found) return found;
            return null;
        }

        /// The same for a tweak's list with kinds by method (`kindsOf` null: all). `missing`:
        /// what to answer for a method that is null (null: pass over it).
        public static string Find(IEnumerable<MethodBase> methods, System.Func<MethodBase, ProbeRepairPolicy.GuardedKinds> kindsOf = null, string missing = null)
        {
            foreach (var method in methods)
            {
                if (method == null)
                {
                    if (missing != null) return missing;
                    continue;
                }
                if (Find(method, kindsOf?.Invoke(method) ?? ProbeRepairPolicy.GuardedKinds.All) is { } found) return found;
            }
            return null;
        }

        /// Whether `method` has a prefix or postfix of `hooks`' now. Hooks several tweaks share
        /// (MainThread, PartitionerFreed, CritterNeedsFirst) are patched under the Harmony ID of
        /// the first tweak that installs them, and go with it if its install is taken back: the
        /// next tweak asks this and installs them again.
        public static bool Hooked(MethodBase method, System.Type hooks)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            foreach (var p in info.Prefixes) if (p.PatchMethod.DeclaringType == hooks) return true;
            foreach (var p in info.Postfixes) if (p.PatchMethod.DeclaringType == hooks) return true;
            return false;
        }

        static string First(IReadOnlyList<Patch> patches, PatchKind kind, ProbeRepairPolicy.GuardedKinds kinds)
        {
            if (!ProbeRepairPolicy.Counts(kinds, kind)) return null;
            for (int i = 0; i < patches.Count; i++)
                if (ModOverlapsCore.IsForeign(patches[i].owner)) return patches[i].owner;
            return null;
        }
    }
}
