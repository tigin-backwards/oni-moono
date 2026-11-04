using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KMod;

namespace SelectCycleTestMod
{
    /// Click selection patched in the shape Fast Track and Better Info Cards give it (#48, #52):
    /// - a transpiler replaces the body of InterfaceTool.GetObjectUnderCursor<KSelectable> with
    ///   `return null` (Fast Track), then a second Patch call adds a postfix taking __result by
    ///   reference (Better Info Cards);
    /// - a prefix of SelectTool.OnLeftClickDown calls it, from code compiled against it, and
    ///   cycles through what lies under the pointer itself when it returns null (Fast Track).
    /// The prefix calls it 25 times a click, so its shared code tiers up within a few clicks. If
    /// the patched body stops running, the original's returns the first object under the pointer
    /// (no previous selection is passed), the prefix selects that, and clicks stop cycling.
    public sealed class Mod : UserMod2
    {
        const int CallsPerClick = 25;
        static Func<InterfaceTool, KSelectable> under;
        static AccessTools.FieldRef<InterfaceTool, int> layerMask;
        static int lost;

        public override void OnLoad(Harmony harmony)
        {
            var self = typeof(Mod);
            var target = AccessTools.Method(typeof(InterfaceTool), "GetObjectUnderCursor").MakeGenericMethod(typeof(KSelectable));
            harmony.Patch(target, transpiler: new HarmonyMethod(self, nameof(ReturnNull)));
            new Harmony("SelectCycleTestMod.Second").Patch(target, postfix: new HarmonyMethod(self, nameof(After)));
            harmony.Patch(AccessTools.Method(typeof(SelectTool), nameof(SelectTool.OnLeftClickDown)), prefix: new HarmonyMethod(self, nameof(Click)));
            var call = new DynamicMethod("UnderCursor", typeof(KSelectable), new[] { typeof(InterfaceTool) }, typeof(InterfaceTool), true);
            var il = call.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            under = (Func<InterfaceTool, KSelectable>)call.CreateDelegate(typeof(Func<InterfaceTool, KSelectable>));
            layerMask = AccessTools.FieldRefAccess<InterfaceTool, int>("layerMask");
            Debug.Log("[SelectCycleTestMod] patched");
        }

        static IEnumerable<CodeInstruction> ReturnNull(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Ldnull);
            yield return new CodeInstruction(OpCodes.Ret);
        }

        static void After(bool cycleSelection, ref KSelectable __result) { }

        static bool Click(SelectTool __instance)
        {
            KSelectable found = null;
            for (int i = 0; i < CallsPerClick; i++)
            {
                var hit = under(__instance);
                if (hit != null) found = hit;
            }
            if (found != null)
                Debug.LogWarning($"[SelectCycleTestMod] GetObjectUnderCursor ran the original body ({++lost} clicks)");
            else
                found = Next(__instance);
            __instance.Select(found);
            return false;
        }

        /// The object after the selection among the selectable ones under the pointer, by instance id.
        static KSelectable Next(SelectTool tool)
        {
            var hits = new List<KSelectable>();
            tool.GetSelectablesUnderCursor(hits);
            int mask = layerMask(tool);
            hits.RemoveAll(s => s == null || !s.IsSelectable || ((1 << s.gameObject.layer) & mask) == 0);
            if (hits.Count == 0) return null;
            hits.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            int index = hits.IndexOf(tool.selected);
            return hits[(index + 1) % hits.Count];
        }
    }
}
