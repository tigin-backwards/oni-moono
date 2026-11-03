using System;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// The game's main thread, for tweaks whose state only that thread may touch: the thread that
    /// runs Game.OnPrefabInit (before any colony's state machines update). Until a game has
    /// started, no thread counts as main.
    public static class MainThread
    {
        static int id = -1;
        static bool installed;

        public static bool Is => Environment.CurrentManagedThreadId == id;

        public static void Install(Harmony harmony)
        {
            if (installed) return;
            installed = true;
            harmony.Patch(AccessTools.Method(typeof(Game), "OnPrefabInit"), postfix: new HarmonyMethod(typeof(MainThread), nameof(Started)));
        }

        static void Started() => id = Environment.CurrentManagedThreadId;
    }
}
