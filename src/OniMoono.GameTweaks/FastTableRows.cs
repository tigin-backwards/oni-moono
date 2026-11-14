using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-table-rows` (exact): a table screen (Consumables, Vitals, Priorities)
    /// knows each cell's row and column from the moment the row is built.
    ///
    /// Every cell's load action asks the table which row and which column its widget belongs to
    /// (TableScreen.GetWidgetRow, GetWidgetColumn). The table remembers each answer
    /// (known_widget_rows, known_widget_columns), but finds it the first time by going through
    /// its rows, or its columns, in order and searching each one's widgets for this one: for the
    /// cells of the row just built, every row before it, and for each cell every column before
    /// its own with every row's widget in that column. Building R rows of C columns compares
    /// widgets about R²C² / 2 times for the rows and R²C² / 4 for the columns; Consumables has
    /// some 80 columns, so with 200 Duplicants its first opening spent most of its time there.
    ///
    /// Here, before a row's cells are loaded (TableRow.RefreshColumns, which ConfigureContent
    /// calls once the row has its widgets), each of its widgets is entered in the table's two
    /// dictionaries with the row and the column it was made for, unless it is already there. The
    /// game's lookups then find every answer at once. The answers are the game's: the first row
    /// of the table's list that holds the widget (world dividers left out), and the first column
    /// of the table's that holds it; a widget made for one cell is in one row and one column.
    /// A row is entered once (again when the table has more columns than it had then).
    ///
    /// Consumables' cells have a second search of the same kind: each food cell's load action
    /// ends with ConsumablesTableScreen.refresh_scrollers, which counts the discovered foods and
    /// then shows or hides the scroll bar and border of every row of the table, for each of a
    /// row's 80 cells. Its answer is the same for every cell of a row (nothing a load action does
    /// changes what it reads or writes), so here it runs for the first cell of each row a table
    /// loads (one TableRow.RefreshColumns call) and is skipped for the others, unless the table
    /// has more rows than at that first call. Outside a row's load (a column the game marked
    /// dirty) it runs every time.
    ///
    /// ONI_TWEAK_VERIFY=1: each entry is also found the game's way (the rows and then the columns
    /// searched in order) and must be the same, and each refresh_scrollers call that would be
    /// skipped runs after all and must leave every row's scroll bar, border and scroller as
    /// they were; a line for each difference, and a count now and then. The tweak stands down,
    /// leaving the game's code, when another mod patches GetWidgetRow, GetWidgetColumn, either
    /// ContainsWidget or refresh_scrollers.
    [GameMember(GameUse.Prefix | GameUse.Finalizer, "System.Void TableRow::RefreshColumns(System.Collections.Generic.Dictionary`2<System.String,TableColumn>)")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void ConsumablesTableScreen::refresh_scrollers()", "385691b5697d46c4 linux-744825 win-744825", "9d5ecb53860d4f3f linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "UnityEngine.GameObject TableRow::GetScroller(System.String)")]
    [GameMember(GameUse.Uses, "UnityEngine.GameObject TableRow::GetScrollerBorder(System.String)")]
    [GameMember(GameUse.Mirrors, "TableRow TableScreen::GetWidgetRow(UnityEngine.GameObject)", "c3078ea3e3f50d47 linux-744825 win-744825", "039765c1e75c832e linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "TableColumn TableScreen::GetWidgetColumn(UnityEngine.GameObject)", "96382a79a5051657 linux-744825 win-744825", "0b24a8ee17c1925c linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Boolean TableRow::ContainsWidget(UnityEngine.GameObject)", "77e1847d86b47dd5 linux-744825 win-744825", "08cc36e1342698de linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Boolean TableColumn::ContainsWidget(UnityEngine.GameObject)", "da24fe67c8f74548 linux-744825 win-744825", "99567d7130c8ada4 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<UnityEngine.GameObject,TableRow> TableScreen::known_widget_rows")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<UnityEngine.GameObject,TableColumn> TableScreen::known_widget_columns")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<System.String,TableColumn> TableScreen::columns")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<TableRow> TableScreen::rows")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<TableRow,UnityEngine.GameObject> TableColumn::widgets_by_row")]
    [GameMember(GameUse.Uses, "TableScreen TableColumn::screen")]
    [GameMember(GameUse.Uses, "TableRow/RowType TableRow::rowType")]
    public static class FastTableRows
    {
        public const string Name = "fast-table-rows";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        static AccessTools.FieldRef<TableScreen, Dictionary<GameObject, TableRow>> knownRows;
        static AccessTools.FieldRef<TableScreen, Dictionary<GameObject, TableColumn>> knownColumns;
        static AccessTools.FieldRef<TableScreen, Dictionary<string, TableColumn>> columnsOf;

        /// The rows entered so far, each with the number of columns its table had then.
        sealed class Entered
        {
            public int Columns;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TableRow, Entered> entered = new();

        enum State { Unchecked, On, Off }
        static State state;

        public static long Entries, Verified, Wrong, ScrollerSkips;

        /// The row loads in progress (TableRow.RefreshColumns calls, nested ones counted), the
        /// number of the outermost one, and the refresh_scrollers call that ran in it.
        static int loads;
        static long load, scrollersLoad;
        static ConsumablesTableScreen scrollersTable;
        static int scrollersRows;
        static bool scrollersPatched;

        public static void Install(Harmony harmony)
        {
            var refresh = AccessTools.Method(typeof(TableRow), "RefreshColumns", new[] { typeof(Dictionary<string, TableColumn>) });
            var rowsField = AccessTools.Field(typeof(TableScreen), "known_widget_rows");
            var columnsField = AccessTools.Field(typeof(TableScreen), "known_widget_columns");
            var tableColumns = AccessTools.Field(typeof(TableScreen), "columns");
            if (refresh == null || rowsField?.FieldType != typeof(Dictionary<GameObject, TableRow>)
                || columnsField?.FieldType != typeof(Dictionary<GameObject, TableColumn>)
                || tableColumns?.FieldType != typeof(Dictionary<string, TableColumn>)
                || AccessTools.Field(typeof(TableScreen), "rows")?.FieldType != typeof(List<TableRow>)
                || AccessTools.Field(typeof(TableColumn), "widgets_by_row")?.FieldType != typeof(Dictionary<TableRow, GameObject>)
                || AccessTools.Field(typeof(TableColumn), "screen")?.FieldType != typeof(TableScreen)
                || AccessTools.Method(typeof(TableScreen), "GetWidgetRow", new[] { typeof(GameObject) }) == null
                || AccessTools.Method(typeof(TableScreen), "GetWidgetColumn", new[] { typeof(GameObject) }) == null)
            {
                global::Debug.Log($"{Name}: left out, this build's table screens are not the ones it changes");
                return;
            }
            knownRows = AccessTools.FieldRefAccess<TableScreen, Dictionary<GameObject, TableRow>>(rowsField);
            knownColumns = AccessTools.FieldRefAccess<TableScreen, Dictionary<GameObject, TableColumn>>(columnsField);
            columnsOf = AccessTools.FieldRefAccess<TableScreen, Dictionary<string, TableColumn>>(tableColumns);
            var scrollers = AccessTools.Method(typeof(ConsumablesTableScreen), "refresh_scrollers", Type.EmptyTypes);
            scrollersPatched = scrollers != null && scrollers.ReturnType == typeof(void);
            harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(FastTableRows), nameof(BeforeRefresh)) { priority = Priority.First },
                finalizer: scrollersPatched ? new HarmonyMethod(typeof(FastTableRows), nameof(AfterRefresh)) { priority = Priority.Last } : null);
            if (scrollersPatched) harmony.Patch(scrollers, prefix: new HarmonyMethod(typeof(FastTableRows), nameof(BeforeScrollers)) { priority = Priority.Last });
            else global::Debug.Log($"{Name}: Consumables' scroller refresh is left as it is (this build has no refresh_scrollers)");
        }

        static IEnumerable<string> Owners(IEnumerable<Patch> patches)
        {
            foreach (var p in patches) yield return p.owner;
        }

        /// Once, at the first row loaded (every mod has patched by then).
        static void Check()
        {
            string why;
            try
            {
                var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
                foreach (var m in new MethodBase[]
                         {
                             AccessTools.Method(typeof(TableScreen), "GetWidgetRow", new[] { typeof(GameObject) }),
                             AccessTools.Method(typeof(TableScreen), "GetWidgetColumn", new[] { typeof(GameObject) }),
                             AccessTools.Method(typeof(TableRow), "ContainsWidget", new[] { typeof(GameObject) }),
                             AccessTools.Method(typeof(TableColumn), "ContainsWidget", new[] { typeof(GameObject) }),
                             AccessTools.Method(typeof(ConsumablesTableScreen), "refresh_scrollers", Type.EmptyTypes),
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
            global::Debug.Log(why == null ? $"{Name}: on" : $"{Name}: off ({why}); the game's table lookups stay");
        }

        /// Before a row's cells are loaded: its widgets entered with their row and column.
        static void BeforeRefresh(TableRow __instance, Dictionary<string, TableColumn> columns)
        {
            if (scrollersPatched && loads++ == 0) load++;
            if (state == State.Off || columns == null || __instance == null) return;
            if (state == State.Unchecked) Check();
            if (state != State.On) return;
            var mark = entered.GetValue(__instance, _ => new Entered { Columns = -1 });
            if (mark.Columns == columns.Count) return;
            try
            {
                Enter(__instance, columns);
                mark.Columns = columns.Count;
            }
            catch (Exception e)
            {
                state = State.Off;
                global::Debug.Log($"{Name}: off ({e.GetType().Name}: {e.Message}); the game's table lookups stay");
            }
        }

        /// After a row's cells are loaded, however that ended (a prefix of another owner that
        /// skips the game's method skips neither this nor BeforeRefresh).
        static void AfterRefresh()
        {
            if (loads > 0) loads--;
        }

        /// Before ConsumablesTableScreen.refresh_scrollers: skipped when it already ran in this
        /// row's load for the table with as many rows.
        static bool BeforeScrollers(ConsumablesTableScreen __instance, bool __runOriginal)
        {
            if (!__runOriginal || state != State.On || loads == 0) return __runOriginal;
            int rows = __instance.rows.Count;
            if (scrollersLoad == load && ReferenceEquals(scrollersTable, __instance) && scrollersRows == rows)
            {
                ScrollerSkips++;
                if (!verify) return false;
                VerifyScrollers(__instance);
                return false;
            }
            scrollersLoad = load;
            scrollersTable = __instance;
            scrollersRows = rows;
            return true;
        }

        /// Each widget entered with what TableScreen.GetWidgetRow and GetWidgetColumn would find
        /// for it: the first row of the table's list that is no world divider and holds the
        /// widget (TableRow.ContainsWidget: among its widgets), and the first of the table's
        /// columns that holds it (TableColumn.ContainsWidget: among its widgets by row).
        [MirrorsGame("TableRow TableScreen::GetWidgetRow(UnityEngine.GameObject)")]
        [MirrorsGame("TableColumn TableScreen::GetWidgetColumn(UnityEngine.GameObject)")]
        [MirrorsGame("System.Boolean TableRow::ContainsWidget(UnityEngine.GameObject)")]
        [MirrorsGame("System.Boolean TableColumn::ContainsWidget(UnityEngine.GameObject)")]
        static void Enter(TableRow row, Dictionary<string, TableColumn> columns)
        {
            // A world divider is no row to GetWidgetRow, and it has no cells.
            bool isRow = row.rowType != TableRow.RowType.WorldDivider;
            TableScreen asked = null;
            bool listed = false;
            foreach (var item in columns)
            {
                var column = item.Value;
                var screen = column?.screen;
                // Only for the table whose columns these are: its GetWidgetColumn searches them.
                if (screen == null || !ReferenceEquals(columnsOf(screen), columns)) continue;
                if (!column.widgets_by_row.TryGetValue(row, out var widget) || widget == null) continue;
                // GetWidgetRow searches the table's rows: the row must be one of them.
                if (isRow && !ReferenceEquals(asked, screen))
                {
                    asked = screen;
                    listed = screen.rows.Contains(row);
                }
                bool newRow = isRow && listed && knownRows(screen).TryAdd(widget, row);
                bool newColumn = knownColumns(screen).TryAdd(widget, column);
                if (newRow || newColumn) Entries++;
                if (verify && (newRow || newColumn)) Verify(screen, columns, widget, newRow ? row : null, newColumn ? column : null);
            }
        }

        // ---- Verify mode ----

        static MethodInfo scrollersMethod;
        static readonly List<(bool Bar, bool Border, bool Horizontal, bool Enabled)> before = new();

        /// The call about to be skipped runs after all (by reflection: no prefix runs) and must
        /// change no row's scroll bar, border or scroller.
        static void VerifyScrollers(ConsumablesTableScreen table)
        {
            static (bool, bool, bool, bool) StateOf(TableRow row)
            {
                GameObject scroller = null, border = null;
                try
                {
                    scroller = row == null ? null : row.GetScroller("consumableScroller");
                    border = row == null ? null : row.GetScrollerBorder("consumableScroller");
                }
                catch (KeyNotFoundException) { }
                var rect = scroller == null ? null : scroller.transform.parent.GetComponent<UnityEngine.UI.ScrollRect>();
                if (rect == null) return default;
                return (rect.horizontalScrollbar != null && rect.horizontalScrollbar.gameObject.activeSelf, border != null && border.activeSelf, rect.horizontal, rect.enabled);
            }
            try
            {
                before.Clear();
                foreach (var row in table.rows) before.Add(StateOf(row));
                // Harmony's patched method is what Invoke reaches too; the game's own body is
                // run with this prefix letting it through (loads is 0 for the call).
                scrollersMethod ??= AccessTools.Method(typeof(ConsumablesTableScreen), "refresh_scrollers", Type.EmptyTypes);
                int kept = loads;
                loads = 0;
                try { scrollersMethod.Invoke(table, null); }
                finally { loads = kept; }
                Verified++;
                for (int i = 0; i < before.Count && i < table.rows.Count; i++)
                    if (StateOf(table.rows[i]) != before[i])
                    {
                        Wrong++;
                        global::Debug.Log($"{Name}: verify wrong ({Wrong}): a skipped refresh_scrollers would have changed row {i} ({table.rows[i].name}) from {before[i]} to {StateOf(table.rows[i])}");
                        break;
                    }
                if (ScrollerSkips % 5000 == 1) global::Debug.Log($"{Name}: {ScrollerSkips} scroller refreshes skipped, {Entries} widgets entered, {Verified} verified, {Wrong} wrong");
            }
            catch (Exception e)
            {
                global::Debug.Log($"{Name}: verify of a skipped refresh_scrollers failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// The game's own search for the widget's row and column must find the ones entered.
        static void Verify(TableScreen screen, Dictionary<string, TableColumn> columns, GameObject widget, TableRow row, TableColumn column)
        {
            if (row != null)
            {
                TableRow found = null;
                foreach (var r in screen.rows)
                    if (r.rowType != TableRow.RowType.WorldDivider && r.ContainsWidget(widget))
                    {
                        found = r;
                        break;
                    }
                Verified++;
                if (!ReferenceEquals(found, row))
                {
                    Wrong++;
                    global::Debug.Log($"{Name}: verify wrong ({Wrong}): {widget.name} entered in row {row.name}, the game finds {(found == null ? "none" : found.name)}");
                }
            }
            if (column != null)
            {
                TableColumn found = null;
                foreach (var c in columns)
                    if (c.Value.ContainsWidget(widget))
                    {
                        found = c.Value;
                        break;
                    }
                Verified++;
                if (!ReferenceEquals(found, column))
                {
                    Wrong++;
                    global::Debug.Log($"{Name}: verify wrong ({Wrong}): {widget.name} entered in another column than the game finds");
                }
            }
            if (Verified % 2000 < 2) global::Debug.Log($"{Name}: {Entries} widgets entered, {Verified} verified, {Wrong} wrong");
        }
    }
}
