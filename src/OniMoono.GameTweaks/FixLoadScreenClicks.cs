using System;
using System.Collections.Generic;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-load-screen-clicks` (opt-in; it changes what the game does): a click on a
    /// colony in the Load screen opens it once.
    ///
    /// LoadScreen.AddColonyToList fills a pooled row of the colony list and adds a handler
    /// opening that colony (ShowColony) to the row's button with `onClick +=`. Unlike the row's
    /// cloud and local buttons, that button is never cleared, so every refresh within one opening
    /// of the screen (each Load more, a conversion between cloud and local) adds one more: after
    /// k refreshes a click on a colony of an earlier page builds the colony's view k + 1 times in
    /// that frame (each ShowColony destroys the rows the one before made and instantiates one per
    /// save again; a colony of 715 saves costs about a second each). The last handler wins:
    /// after the frame the screen shows what one handler would have shown.
    ///
    /// Here the row keeps only the handler of the refresh that filled it: the handlers it had
    /// from earlier refreshes are removed when it is filled again. What differs from the game:
    /// ShowColony runs once a click (a mod patching it sees fewer calls); the earlier runs' log
    /// lines are not written (ShowColonySave's `Failed to find cluster ...` for a save of an
    /// unknown cluster, once a run); and the objects those runs made and destroyed within the
    /// frame are not made.
    public static class FixLoadScreenClicks
    {
        public const string Name = "fix-load-screen-clicks";

        static readonly AccessTools.FieldRef<LoadScreen, UIPool<HierarchyReferences>> pool =
            AccessTools.FieldRefAccess<LoadScreen, UIPool<HierarchyReferences>>("colonyListPool");
        static readonly AccessTools.FieldRef<UIPool<HierarchyReferences>, Stack<HierarchyReferences>> free =
            AccessTools.FieldRefAccess<UIPool<HierarchyReferences>, Stack<HierarchyReferences>>("freeElements");
        static readonly AccessTools.FieldRef<KButton, System.Action> onClick = AccessTools.FieldRefAccess<KButton, System.Action>("onClick");

        public static void Install(Harmony harmony)
            => harmony.Patch(AccessTools.Method(typeof(LoadScreen), "AddColonyToList"),
                prefix: new HarmonyMethod(typeof(FixLoadScreenClicks), nameof(Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FixLoadScreenClicks), nameof(Postfix)) { priority = Priority.First });

        /// The row AddColonyToList will reuse (UIPool.GetFreeElement pops the top free one), with
        /// the handlers its button has from earlier refreshes.
        static void Prefix(LoadScreen __instance, out (KButton Button, System.Action Stale) __state)
        {
            __state = default;
            var rows = pool(__instance);
            if (rows == null) return;
            var stack = free(rows);
            if (stack == null || stack.Count == 0) return;
            var button = RowButton(stack.Peek());
            if (button != null) __state = (button, onClick(button));
        }

        static void Postfix((KButton Button, System.Action Stale) __state)
        {
            if (__state.Button == null || __state.Stale == null) return;
            // Whether or not the row was taken (no saves: it was not), the stale handlers go.
            onClick(__state.Button) = (System.Action)Delegate.Remove(onClick(__state.Button), __state.Stale);
        }

        static KButton RowButton(HierarchyReferences row)
        {
            if (row == null || !row.HasReference("Button")) return null;
            var reference = row.GetReference<UnityEngine.RectTransform>("Button");
            return reference == null ? null : reference.GetComponent<KButton>();
        }
    }
}
