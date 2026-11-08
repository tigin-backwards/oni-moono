using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `no-save-gc` (on by default): SaveLoader.Save ends with GC.Collect(), a full blocking
    /// collection of the whole heap on the main thread (about 9 GB in a large colony), to release
    /// the save's buffers. The collector reclaims them on its own schedule anyway; this removes the
    /// forced collection from saving and nothing else.
    [GameMember(GameUse.Transpiler, "System.String SaveLoader::Save(System.String,System.Boolean,System.Boolean)", "905dcad114d8f685 linux-744825 win-744825", Rewrites = 1)]
    internal static class NoSaveGc
    {
        public const string Name = GameTweakNames.NoSaveGc;

        public static void Install(Harmony harmony)
            => harmony.Patch(AccessTools.Method("SaveLoader:Save", [typeof(string), typeof(bool), typeof(bool)]),
                transpiler: new HarmonyMethod(typeof(NoSaveGc), nameof(Transpiler)));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, System.Reflection.MethodBase original)
        {
            var collect = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
            int removed = 0;
            foreach (var i in instructions)
            {
                if (i.Calls(collect))
                {
                    removed++;
                    yield return new CodeInstruction(OpCodes.Nop) { labels = i.labels, blocks = i.blocks };
                }
                else yield return i;
            }
            // One GC.Collect() in SaveLoader.Save (TweakInstall checks the count).
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, removed);
        }
    }
}
