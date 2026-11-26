using System.Collections.Generic;
using HarmonyLib;
using KMod;

namespace PatchTypesTestMod
{
    /// A postfix in Default Deconstruct's shape (#128): it declares
    /// `ref Dictionary<string, ToggleState> filters` on FilteredDragTool.GetDefaultFilters, whose
    /// argument U59 made a `ToggleData[]`. For the Deconstruct tool it asks the dictionary for two
    /// keys, as the mod does, and keeps what it got.
    public sealed class Mod : UserMod2
    {
    }

    [HarmonyPatch(typeof(FilteredDragTool), "GetDefaultFilters")]
    public static class Probe
    {
        public static int Calls;
        public static int DeconstructCalls;
        public static bool Null;
        public static bool HasAll;
        public static bool HasBuildings;
        public static bool Done;

        public static void Postfix(DeconstructTool __instance, ref Dictionary<string, ToolParameterMenu.ToggleState> filters)
        {
            Calls++;
            if (__instance.ToString() != "DeconstructTool (DeconstructTool)") return;
            DeconstructCalls++;
            Null = filters == null;
            if (!Null)
            {
                HasAll = filters.ContainsKey(ToolParameterMenu.FILTERLAYERS.ALL);
                HasBuildings = filters.ContainsKey(ToolParameterMenu.FILTERLAYERS.BUILDINGS);
            }
            Done = true;
            Debug.Log($"[PatchTypesTestMod] DeconstructTool filters: null {Null}, ALL {HasAll}, BUILDINGS {HasBuildings}");
        }
    }
}
