using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// Game tweak `lazy-table-refresh` (it changes what the game does a little, so it is left out
    /// of `exact`): an open table screen refreshes, once a second, only the rows in sight.
    ///
    /// The render 1000 ms bucket marks every table column dirty each second
    /// (TableColumn.Render1000ms), and the open table's next ScreenUpdate runs each dirty column's
    /// load action on the widget of every row: on a colony of 200 Duplicants, thousands of
    /// widgets in one frame, most of them scrolled out of sight. Here that frame refreshes the
    /// widgets of the rows that lie within the table's scroll view or within one view's height of
    /// it (in the game's order, columns as the game keeps them, rows within each), and marks the
    /// other rows' widgets stale. Each frame, before the canvases are laid out and drawn
    /// (Canvas.preWillRenderCanvases), once any stale row has come that close (scrolled, the view
    /// resized, the rows laid out again) every stale row gets the load actions it missed, in the
    /// same order, in one go (a table laid out again once, not every frame of a scroll); a frame
    /// that sorts the rows refreshes every stale row first. While the list is being scrolled (it
    /// moved within the last 1.5 s) the refresh is the game's, of every row: rows coming into
    /// sight one after another would otherwise be caught up more often than once a second. A frame in which the table
    /// builds its rows (opening, a Duplicant added or removed) refreshes every row, as the game.
    ///
    /// What differs from the game: a row out of sight shows, once scrolled in, values read then
    /// rather than at the last refresh (at most a second newer), and the load actions of rows out
    /// of sight run later or not at all (a mod reading their widgets' text off screen would read
    /// older values). Every row and object stays as the game made it.
    ///
    /// ONI_TWEAK_VERIFY=1: each refresh also runs the game's own (every row), and the widgets of
    /// the rows in sight must read the same after both (their text, sprites, colours, toggles'
    /// states and which of their objects are active); a line every 60 refreshes and one for each
    /// difference, and each frame no stale row may lie within the view once the stale rows near
    /// it are caught up. The tweak stands down, leaving the game's refresh, when another mod patches the
    /// table's ScreenUpdate or SortRows, or the columns' Render1000ms, MarkDirty or MarkClean.
    [GameMember(GameUse.Prefix, "System.Void TableScreen::ScreenUpdate(System.Boolean)")]
    [GameMember(GameUse.Prefix, "System.Void TableScreen::SortRows()")]
    [GameMember(GameUse.Uses, "System.Boolean TableScreen::rows_dirty")]
    [GameMember(GameUse.Uses, "System.Boolean TableScreen::incubating")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<System.String,TableColumn> TableScreen::columns")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<TableRow,UnityEngine.GameObject> TableColumn::widgets_by_row")]
    [GameMember(GameUse.Uses, "System.Void TableColumn::Render1000ms(System.Single)")]
    [GameMember(GameUse.Uses, "System.Void TableColumn::MarkDirty(UnityEngine.GameObject,TableScreen/ResultValues)")]
    [GameMember(GameUse.Uses, "System.Void TableColumn::MarkClean()")]
    public static class LazyTableRefresh
    {
        public const string Name = "lazy-table-refresh";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        static AccessTools.FieldRef<TableScreen, bool> rowsDirty, incubating;
        static AccessTools.FieldRef<TableScreen, Dictionary<string, TableColumn>> columnsOf;

        /// Each table's stale widgets: per row, the columns whose load action it missed, in the
        /// order the refresh met them.
        sealed class Table
        {
            public readonly Dictionary<TableRow, List<TableColumn>> Stale = new();
            public ScrollRect View;
            /// Where the scrolled content was last seen, and when it last moved (unscaled time).
            public Vector3 LastAt;
            public float MovedAt = float.NegativeInfinity;
            /// The view's extent in the content's space at the last Measure.
            public float ViewMin, ViewMax;
            public bool Measured;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TableScreen, Table> tables = new();
        static readonly List<TableScreen> withStale = new();

        /// How long after its list last moved a table still counts as being scrolled.
        const float ScrollingSeconds = 1.5f;

        enum State { Unchecked, On, Off }
        static State state;

        public static long Refreshes, Skipped, Caught, Verified, Wrong;

        public static void Install(Harmony harmony)
        {
            var update = AccessTools.Method(typeof(TableScreen), "ScreenUpdate", new[] { typeof(bool) });
            var sort = AccessTools.Method(typeof(TableScreen), "SortRows");
            var dirtyField = AccessTools.Field(typeof(TableScreen), "rows_dirty");
            var incubatingField = AccessTools.Field(typeof(TableScreen), "incubating");
            var columnsField = AccessTools.Field(typeof(TableScreen), "columns");
            if (update == null || sort == null || dirtyField?.FieldType != typeof(bool) || incubatingField?.FieldType != typeof(bool)
                || columnsField?.FieldType != typeof(Dictionary<string, TableColumn>) || AccessTools.Field(typeof(TableColumn), "widgets_by_row") == null)
            {
                global::Debug.Log($"{Name}: left out, this build's table screens are not the ones it changes");
                return;
            }
            rowsDirty = AccessTools.FieldRefAccess<TableScreen, bool>(dirtyField);
            incubating = AccessTools.FieldRefAccess<TableScreen, bool>(incubatingField);
            columnsOf = AccessTools.FieldRefAccess<TableScreen, Dictionary<string, TableColumn>>(columnsField);
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(LazyTableRefresh), nameof(BeforeUpdate)) { priority = Priority.Last });
            harmony.Patch(sort, prefix: new HarmonyMethod(typeof(LazyTableRefresh), nameof(BeforeSort)) { priority = Priority.Last });
            Canvas.preWillRenderCanvases += CatchUp;
        }

        static IEnumerable<string> Owners(IEnumerable<Patch> patches)
        {
            foreach (var p in patches) yield return p.owner;
        }

        static void Check()
        {
            string why;
            try
            {
                var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
                foreach (var m in new MethodBase[]
                         {
                             AccessTools.Method(typeof(TableScreen), "ScreenUpdate", new[] { typeof(bool) }),
                             AccessTools.Method(typeof(TableScreen), "SortRows"),
                             AccessTools.Method(typeof(TableColumn), "Render1000ms"),
                             AccessTools.Method(typeof(TableColumn), "MarkDirty"),
                             AccessTools.Method(typeof(TableColumn), "MarkClean"),
                         })
                {
                    if (m == null) continue;
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
            global::Debug.Log(why == null ? $"{Name}: on" : $"{Name}: off ({why}); the game's table refresh stays");
        }

        // ---- What is in sight ----

        /// The table's scroll view: the ScrollRect its rows scroll in.
        static ScrollRect ViewOf(TableScreen screen, Table t)
        {
            if (t.View == null && screen.scroll_content_transform != null)
                t.View = screen.scroll_content_transform.GetComponentInParent<ScrollRect>();
            return t.View;
        }

        static readonly Vector3[] corners = new Vector3[4];

        /// Whether a row lies within the view or within one view's height above or below it. A
        /// row outside the scrolled content (the header) is always in sight, and so is any row
        /// when the view could not be measured.
        static bool Near(TableScreen screen, Table t, TableRow row) => Within(screen, t, row, 1f);

        /// Verify mode: whether any of the row is within the view itself.
        static bool InView(TableScreen screen, Table t, TableRow row) => Within(screen, t, row, 0f);

        /// The view's extent in the scrolled content's space, measured at the start of a pass
        /// (Measure): the rows are the content's children, so each row's own place then tells
        /// whether it is near, without a world transform for each.
        static void Measure(TableScreen screen, Table t)
        {
            t.Measured = false;
            var view = ViewOf(screen, t);
            var content = screen.scroll_content_transform;
            if (view == null || content == null) return;
            var viewport = view.viewport != null ? view.viewport : (RectTransform)view.transform;
            viewport.GetWorldCorners(corners);
            var toContent = content.worldToLocalMatrix;
            float yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float y = toContent.MultiplyPoint3x4(corners[i]).y;
                yMin = Math.Min(yMin, y);
                yMax = Math.Max(yMax, y);
            }
            t.ViewMin = yMin;
            t.ViewMax = yMax;
            t.Measured = true;
        }

        static bool Within(TableScreen screen, Table t, TableRow row, float views)
        {
            if (row == null || !t.Measured) return true;
            var rt = row.transform as RectTransform;
            if (rt == null || rt.parent != screen.scroll_content_transform) return true;
            float margin = (t.ViewMax - t.ViewMin) * views;
            var r = rt.rect;
            float scale = rt.localScale.y, y = rt.localPosition.y;
            float a = y + r.yMin * scale, b = y + r.yMax * scale;
            return Math.Max(a, b) >= t.ViewMin - margin && Math.Min(a, b) <= t.ViewMax + margin;
        }

        // ---- The refresh ----

        /// Before the table's ScreenUpdate: the dirty columns refreshed for the rows near sight,
        /// the others marked stale, and the columns marked clean, so the game's loop finds none.
        /// A frame that builds the rows (rows_dirty, or the first after the table opened) is the
        /// game's, and refreshes every row.
        static void BeforeUpdate(TableScreen __instance)
        {
            if (state == State.Off) return;
            if (state == State.Unchecked) Check();
            if (state != State.On) return;
            var columns = columnsOf(__instance);
            if (columns == null) return;
            var t = tables.GetValue(__instance, _ => new Table());
            // A table whose list moved within the last second is being scrolled: its rows come
            // into sight one after another, and bringing them up to date as they do would lay
            // the table out more often than the game's own refresh, once a second. So while it
            // scrolls, the refresh is the game's, of every row.
            var content = __instance.scroll_content_transform;
            float now = Time.unscaledTime;
            if (content != null)
            {
                var at = content.localPosition;
                if (at != t.LastAt)
                {
                    t.LastAt = at;
                    t.MovedAt = now;
                }
            }
            bool scrolling = now - t.MovedAt < ScrollingSeconds;
            if (rowsDirty(__instance) || incubating(__instance) || scrolling)
            {
                // The game's loop then refreshes every row of each dirty column; the misses of the
                // others stay.
                foreach (var c in columns.Values)
                    if (c.isDirty)
                        foreach (var missed in t.Stale.Values) missed.Remove(c);
                return;
            }
            bool dirty = false;
            foreach (var c in columns.Values) dirty |= c.isDirty;
            if (!dirty) return;
            Refreshes++;
            Measure(__instance, t);
            List<(TableRow, GameObject)> checkedWidgets = verify ? new() : null;
            var near = new Dictionary<TableRow, bool>();
            foreach (var column in columns.Values)
            {
                if (!column.isDirty) continue;
                foreach (var item in column.widgets_by_row)
                {
                    var row = item.Key;
                    if (!near.TryGetValue(row, out bool inSight)) near[row] = inSight = Near(__instance, t, row);
                    if (inSight)
                    {
                        // A row refreshed now has no older misses for this column.
                        if (t.Stale.TryGetValue(row, out var missed)) missed.Remove(column);
                        column.on_load_action(row.GetIdentity(), item.Value);
                        if (verify) checkedWidgets.Add((row, item.Value));
                    }
                    else
                    {
                        if (!t.Stale.TryGetValue(row, out var missed)) t.Stale[row] = missed = new List<TableColumn>();
                        if (!missed.Contains(column)) missed.Add(column);
                        Skipped++;
                    }
                }
                column.MarkClean();
            }
            if (t.Stale.Count > 0 && !withStale.Contains(__instance)) withStale.Add(__instance);
            if (verify) Verify(__instance, columns, checkedWidgets);
        }

        /// Before the rows are sorted: every stale row first, so none moves into sight stale.
        static void BeforeSort(TableScreen __instance)
        {
            if (state != State.On || !tables.TryGetValue(__instance, out var t) || t.Stale.Count == 0) return;
            var rows = new List<TableRow>(t.Stale.Keys);
            foreach (var row in rows) CatchUpRow(__instance, t, row);
            withStale.Remove(__instance);
        }

        /// Before the canvases are laid out and drawn: each stale row near sight now gets the
        /// load actions it missed.
        static readonly List<TableRow> due = new();

        static void CatchUp()
        {
            if (state != State.On || withStale.Count == 0) return;
            for (int s = withStale.Count - 1; s >= 0; s--)
            {
                var screen = withStale[s];
                if (screen == null || !tables.TryGetValue(screen, out var t) || t.Stale.Count == 0)
                {
                    withStale.RemoveAt(s);
                    continue;
                }
                // A closed table (ShowOptimizedKScreen keeps it active, hidden) draws nothing: its
                // stale rows wait until it shows again.
                if (!screen.isActiveAndEnabled || !screen.IsScreenActive()) continue;
                Measure(screen, t);
                // One stale row near sight brings every stale row of the table up to date: a row's
                // load actions change its texts, which lays the whole table out again, so rows
                // caught up one or two a frame while a list scrolls would lay it out every frame
                // (Vitals on a 200-Duplicant colony: 22 to 32 ms a frame scrolling); all at once
                // costs one layout, as the game's own refresh does once a second.
                bool any = false;
                foreach (var row in t.Stale.Keys)
                    if (row == null || Near(screen, t, row))
                    {
                        any = true;
                        break;
                    }
                if (!any) continue;
                due.Clear();
                due.AddRange(t.Stale.Keys);
                foreach (var row in due) CatchUpRow(screen, t, row);
                if (verify)
                    foreach (var row in t.Stale.Keys)
                        if (row != null && InView(screen, t, row))
                        {
                            Wrong++;
                            global::Debug.Log($"{Name}: verify wrong ({Wrong}): {row.name} is in sight and stale");
                        }
                if (t.Stale.Count == 0) withStale.RemoveAt(s);
            }
            due.Clear();
        }

        static void CatchUpRow(TableScreen screen, Table t, TableRow row)
        {
            if (!t.Stale.TryGetValue(row, out var missed)) return;
            t.Stale.Remove(row);
            if (row == null) return;
            foreach (var column in missed)
                if (column.widgets_by_row.TryGetValue(row, out var widget) && widget != null)
                {
                    column.on_load_action(row.GetIdentity(), widget);
                    Caught++;
                }
        }

        // ---- Verify mode ----

        static void Verify(TableScreen screen, Dictionary<string, TableColumn> columns, List<(TableRow Row, GameObject Widget)> refreshed)
        {
            var before = new List<string>(refreshed.Count);
            foreach (var (_, w) in refreshed) before.Add(StateOf(w));
            // The game's own refresh: every row of every column, in its order.
            foreach (var column in columns.Values)
                foreach (var item in column.widgets_by_row)
                    column.on_load_action(item.Key.GetIdentity(), item.Value);
            for (int i = 0; i < refreshed.Count; i++)
            {
                Verified++;
                var after = StateOf(refreshed[i].Widget);
                if (after != before[i])
                {
                    Wrong++;
                    global::Debug.Log($"{Name}: verify wrong ({Wrong}): {refreshed[i].Row?.name}/{refreshed[i].Widget?.name}: lazy {before[i]} | full {after}");
                }
            }
            // The stale marks stay: the catching up runs as without the check.
            if (Refreshes % 60 == 1) global::Debug.Log($"{Name}: {Refreshes} refreshes, {Skipped} widgets out of sight skipped, {Caught} caught up, {Verified} verified, {Wrong} wrong");
        }

        static readonly List<Component> components = new();

        /// What a widget shows: its objects' active flags, texts, images and toggles' states.
        static string StateOf(GameObject widget)
        {
            if (widget == null) return "destroyed";
            var text = new StringBuilder();
            void Walk(Transform tr)
            {
                text.Append(tr.gameObject.activeSelf ? '+' : '-').Append(tr.name);
                tr.GetComponents(components);
                foreach (var c in components)
                    switch (c)
                    {
                        case TMPro.TMP_Text t: text.Append(" t=").Append(t.text).Append(" c=").Append(t.color); break;
                        case Image i: text.Append(" i=").Append(i.sprite == null ? "-" : i.sprite.name).Append(" c=").Append(i.color).Append(i.enabled ? "" : " off"); break;
                        case MultiToggle m: text.Append(" s=").Append(m.CurrentState); break;
                    }
                text.Append(';');
                for (int k = 0; k < tr.childCount; k++) Walk(tr.GetChild(k));
            }
            Walk(widget.transform);
            return text.ToString();
        }
    }
}
