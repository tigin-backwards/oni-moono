using System;
using System.Collections.Generic;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-load-screen-clicks` (on by default, the player's choice; it changes what
    /// the game does, so it is left out of `exact`): a click on a colony in the Load screen opens
    /// it once.
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
    [GameMember(GameUse.Uses, "System.Action KButton::onClick")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Void LoadScreen::AddColonyToList(System.Collections.Generic.List`1<LoadScreen/SaveGameFileDetails>)")]
    [GameMember(GameUse.Uses, "UIPool`1<HierarchyReferences> LoadScreen::colonyListPool")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Stack`1<!0> UIPool`1::freeElements")]
    public static class FixLoadScreenClicks
    {
        public const string Name = "fix-load-screen-clicks";

        static AccessTools.FieldRef<LoadScreen, UIPool<HierarchyReferences>> pool;
        static AccessTools.FieldRef<UIPool<HierarchyReferences>, Stack<HierarchyReferences>> free;
        static AccessTools.FieldRef<KButton, System.Action> onClick;

        /// Installs the fix only where the game still has the bug: AddColonyToList with the
        /// signature, and the pool, free-row stack and onClick fields with the types, it was
        /// written for, and an `onClick +=` with no ClearOnClick before it (LoadScreenClickBug).
        /// A later build that changes them (as a Klei fix may) leaves it out with a log line,
        /// rather than stopping start-up.
        public static void Install(Harmony harmony)
        {
            var add = AccessTools.Method(typeof(LoadScreen), "AddColonyToList");
            var parameters = add?.GetParameters();
            var poolField = AccessTools.Field(typeof(LoadScreen), "colonyListPool");
            var freeField = AccessTools.Field(typeof(UIPool<HierarchyReferences>), "freeElements");
            var onClickField = AccessTools.Field(typeof(KButton), "onClick");
            if (add == null || add.ReturnType != typeof(void) || parameters.Length != 1 || parameters[0].ParameterType != SavesType()
                || poolField?.FieldType != typeof(UIPool<HierarchyReferences>) || freeField?.FieldType != typeof(Stack<HierarchyReferences>)
                || onClickField?.FieldType != typeof(System.Action))
            {
                global::Debug.Log($"{Name}: left out, this build's LoadScreen is not the one it fixes");
                return;
            }
            string fixedBy;
            try
            {
                fixedBy = LoadScreenClickBug.Fixed(add);
            }
            catch (Exception e)
            {
                global::Debug.Log($"{Name}: left out, this build's LoadScreen.AddColonyToList could not be read: {e.GetType().Name}: {e.Message}");
                return;
            }
            if (fixedBy != null)
            {
                global::Debug.Log($"{Name}: left out, this build already {fixedBy} in LoadScreen.AddColonyToList");
                return;
            }
            pool = AccessTools.FieldRefAccess<LoadScreen, UIPool<HierarchyReferences>>(poolField);
            free = AccessTools.FieldRefAccess<UIPool<HierarchyReferences>, Stack<HierarchyReferences>>(freeField);
            onClick = AccessTools.FieldRefAccess<KButton, System.Action>(onClickField);
            harmony.Patch(add,
                prefix: new HarmonyMethod(typeof(FixLoadScreenClicks), nameof(Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FixLoadScreenClicks), nameof(Postfix)) { priority = Priority.First });
        }

        /// List<LoadScreen.SaveGameFileDetails> (a private struct), or null when it is gone.
        static Type SavesType()
        {
            var details = AccessTools.Inner(typeof(LoadScreen), "SaveGameFileDetails");
            return details == null ? null : typeof(List<>).MakeGenericType(details);
        }

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
