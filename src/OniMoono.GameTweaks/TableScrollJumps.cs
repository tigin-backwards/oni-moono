using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-table-scroll` (Bug fixes, on by default; it changes what the game does, so
    /// it is left out of `exact`): a table screen's columns stay where they were scrolled when the
    /// table's rows are laid out again.
    ///
    /// The table screens (Consumables, Vitals, Priorities and the like) give each row its own
    /// horizontal ScrollRect, and keep them in step: a row whose scroll position changed sends it
    /// to TableScreen.SetScrollersDirty, which moves every row there (PositionScrollers). Each
    /// row's scroller object also holds the layout group that places the row's scroll content,
    /// so whenever the table's rows are laid out again (a row or a widget in it changed size,
    /// which happens every second or so while the columns move), the layout group puts the
    /// content back at its start: the scroll position lost, drawn in that frame, and reported by
    /// the row's ScrollRect at the next LateUpdate as 0, which SetScrollersDirty then applies to
    /// every row. Sideways scrolling throws every row back to the first columns for a frame or
    /// more, in the original as in Moono, on every renderer.
    ///
    /// Here, once a row's layout group has placed its scroll content, the content goes back to
    /// the table's scroll position (TableScreen's target, as PositionScrollers would set it):
    /// a layout pass no longer scrolls a row, so a row that was not scrolled does not move the
    /// others. Scrolling (the wheel, a drag, the header's scrollbar), the table zeroing its
    /// columns when it opens, and rows made afresh (the target is then 0, the layout's own
    /// place) behave as before. The tweak stands down, leaving the game's behaviour, when
    /// another mod patches the methods that keep the rows in step.
    [GameMember(GameUse.Postfix, "System.Void UnityEngine.UI.HorizontalOrVerticalLayoutGroup::SetChildrenAlongAxis(System.Int32,System.Boolean)")]
    [GameMember(GameUse.Uses, "System.Single TableScreen::targetScrollerPosition")]
    [GameMember(GameUse.Uses, "UnityEngine.UI.ScrollRect TableRow::scroll_rect")]
    [GameMember(GameUse.Uses, "System.Void TableScreen::SetScrollersDirty(System.Single)")]
    [GameMember(GameUse.Uses, "System.Void TableScreen::PositionScrollers()")]
    public static class TableScrollJumps
    {
        public const string Name = "fix-table-scroll";

        static AccessTools.FieldRef<TableScreen, float> target;
        static AccessTools.FieldRef<TableRow, ScrollRect> rowScroller;
        static MethodBase placeChildren;

        /// What a layout group is to this tweak: the ScrollRect of a table row whose content it
        /// places, and the table; null for any other group.
        sealed class Placed
        {
            public ScrollRect Scroller;
            public TableScreen Table;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HorizontalOrVerticalLayoutGroup, Placed> groups = new();
        static readonly Placed none = new();

        enum State { Unchecked, On, Off }
        static State state;

        public static void Install(Harmony harmony)
        {
            var targetField = AccessTools.Field(typeof(TableScreen), "targetScrollerPosition");
            var scrollField = AccessTools.Field(typeof(TableRow), "scroll_rect");
            placeChildren = AccessTools.Method(typeof(HorizontalOrVerticalLayoutGroup), "SetChildrenAlongAxis", new[] { typeof(int), typeof(bool) });
            if (targetField?.FieldType != typeof(float) || scrollField?.FieldType != typeof(ScrollRect) || placeChildren == null
                || AccessTools.Method(typeof(TableScreen), "SetScrollersDirty", new[] { typeof(float) }) == null
                || AccessTools.Method(typeof(TableScreen), "PositionScrollers") == null)
            {
                global::Debug.Log($"{Name}: left out, this build's table screens are not the ones it fixes");
                return;
            }
            target = AccessTools.FieldRefAccess<TableScreen, float>(targetField);
            rowScroller = AccessTools.FieldRefAccess<TableRow, ScrollRect>(scrollField);
            harmony.Patch(placeChildren, postfix: new HarmonyMethod(typeof(TableScrollJumps), nameof(Placed_)) { priority = Priority.Last });
        }

        /// After a layout group placed its children along an axis: when it is a table row's
        /// scroller and that was the horizontal axis, its content goes back to the table's
        /// scroll position.
        static void Placed_(HorizontalOrVerticalLayoutGroup __instance, int axis)
        {
            if (axis != 0 || state == State.Off || __instance == null) return;
            var placed = groups.GetValue(__instance, Find);
            if (placed == none || placed.Scroller == null || placed.Table == null) return;
            if (state == State.Unchecked) Check();
            if (state != State.On) return;
            var scroller = placed.Scroller;
            if (!scroller.horizontal || scroller.content == null) return;
            // As PositionScrollers sets it (SetNormalizedPosition leaves a move under 0.01 alone).
            scroller.horizontalNormalizedPosition = target(placed.Table);
        }

        static Placed Find(HorizontalOrVerticalLayoutGroup group)
        {
            var scroller = group.GetComponent<ScrollRect>();
            if (scroller == null || scroller.content == null || scroller.content.parent != group.transform) return none;
            var row = group.GetComponentInParent<TableRow>();
            if (row == null || rowScroller(row) != scroller) return none;
            var table = row.GetComponentInParent<TableScreen>();
            return table == null ? none : new Placed { Scroller = scroller, Table = table };
        }

        static IEnumerable<string> Owners(IEnumerable<Patch> patches)
        {
            foreach (var p in patches) yield return p.owner;
        }

        /// Once, at the first table row laid out (every mod has patched by then): another owner's
        /// patch of the methods that keep the rows in step, or of the layout groups' placing,
        /// leaves the game as it is.
        static void Check()
        {
            string why;
            try
            {
                var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
                foreach (var m in new MethodBase[]
                         {
                             AccessTools.Method(typeof(TableScreen), "SetScrollersDirty", new[] { typeof(float) }),
                             AccessTools.Method(typeof(TableScreen), "PositionScrollers"),
                             placeChildren,
                         })
                {
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    owners.Add(new KeyValuePair<string, IEnumerable<string>>($"{m.DeclaringType?.Name}.{m.Name}",
                        ProbeRepairPolicy.Counted(ProbeRepairPolicy.GuardedKinds.All, Owners(info.Prefixes), Owners(info.Postfixes), Owners(info.Transpilers),
                            Owners(info.Finalizers), System.Linq.Enumerable.Concat(Owners(info.InnerPrefixes), Owners(info.InnerPostfixes)))));
                }
                why = ProbeRepairPolicy.Foreign(owners);
            }
            catch (Exception e)
            {
                why = "its methods could not be read: " + e.Message;
            }
            state = why == null ? State.On : State.Off;
            global::Debug.Log(why == null ? $"{Name}: on" : $"{Name}: off ({why}); the game's table scrolling stays");
        }
    }
}
