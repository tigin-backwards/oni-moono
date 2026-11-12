using System.Collections;
using System.Collections.Generic;

using HarmonyLib;
using UnityEngine;


namespace PerfProbe
{
    /// TEST INFRASTRUCTURE: `uilists.txt` beside the probe: the management screens' long lists
    /// (the Duplicant tables, skills, research, reports, codex) and the build menu's search, each
    /// measured the same way in both players. One command a line:
    /// - `wait N`: N frames, unmeasured;
    /// - `idle LABEL N`: N frames as they come;
    /// - `open NAME`: clicks the management menu's button for NAME (as Screens: consumables,
    ///   vitals, jobs, schedule, skills, research, reports, codex) and measures the click's call
    ///   and the 180 frames after it (`LABEL` open-NAME);
    /// - `close`: closes the open screen as its button does, measured as `open`;
    /// - `scroll LABEL N [h]`: N frames of the open screen's longest scroll list moving down and
    ///   back up, a full sweep every 240 frames, through ScrollRect's normalized position (the
    ///   value a wheel or scrollbar drag sets); `h` scrolls the table's columns instead (the
    ///   header's horizontal scroller, which the rows follow). The research screen pans as its
    ///   arrow keys do (its pan flags), right for 120 frames, then left;
    ///   `wheel` sends the first Duplicant row's scroller a wheel notch every 3rd frame (down,
    ///   then up after 120 frames: ExecuteEvents' scroll handler, as the input module does);
    ///   `bar` moves the header row's horizontal scrollbar (Scrollbar.value, as its drag does);
    /// - `toggle LABEL N consumable|priority`: every 10th frame of N, the first Duplicant row's
    ///   first checkbox is clicked (its MultiToggle's onClick, as a click does), or its first
    ///   priority raised and lowered in turn (OptionSelector's change, as a left and right click);
    /// - `search LABEL N CATEGORY WORD`: the build menu's CATEGORY opens, then WORD is typed into
    ///   its search field a letter every 20 frames (each letter refreshing the results at once,
    ///   as the field does 0.2 s after an edit), then cleared, over N frames;
    /// - `quit`: logs `PERF ui done` and quits the game (Application.Quit), for runs that end
    ///   with the scenario;
    /// - `capture N`: the next measured span also captures its first N frames, one PNG a frame
    ///   (ScreenCapture.CaptureScreenshot, `shots/LABEL-K.png` beside the probe), to compare
    ///   consecutive frames between the players (a capture run is not a timing run);
    /// - `counts on|off`: from then on, also counts a frame's layout rebuilds
    ///   (LayoutRebuilder.Rebuild), graphic rebuilds (Graphic.Rebuild, TextMeshProUGUI.Rebuild)
    ///   and text layouts (TMP_Text.ForceMeshUpdate excluded) through Harmony prefixes, which
    ///   cost time themselves: a counting run is not a timing run.
    /// Each measured span logs `PERF ui LABEL f#A-f#B n N mean p50 p95 p99 max ms` (the frames'
    /// real time), the managed allocations a frame (the runtime's own total, as AllocMeter: Boehm's
    /// in the original, .NET's in ours, and ours on the main thread alone), and where the
    /// player reports them (the original's ProfilerRecorder counters) the main thread's time and
    /// the draws, batches and set-pass calls a frame. Ours logs its draws in --frame-stats'
    /// `frame counts`, by frame.
    static class UiLists
    {
        static readonly string[] RecorderNames = { "Main Thread", "Render Thread", "Draw Calls Count", "Batches Count", "SetPass Calls Count" };
        static readonly System.Reflection.MethodInfo threadAllocated = typeof(System.GC).GetMethod("GetAllocatedBytesForCurrentThread", System.Type.EmptyTypes);
        static readonly System.Reflection.MethodInfo totalAllocated = typeof(System.GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });
        static System.Func<long> monoTotal;

        static long Total() => monoTotal != null ? monoTotal() : totalAllocated != null ? (long)totalAllocated.Invoke(null, new object[] { false }) : -1;
        static long Thread() => threadAllocated != null ? (long)threadAllocated.Invoke(null, null) : -1;

        static KScreen open;
        static string openName;

        // ---- counts ----

        static bool counting, patched;
        public static long LayoutRebuilds, GraphicRebuilds, TextRebuilds;
        static void CountLayout() { if (counting) LayoutRebuilds++; }
        static void CountGraphic() { if (counting) GraphicRebuilds++; }
        static void CountText() { if (counting) TextRebuilds++; }

        static void Patch()
        {
            if (patched) return;
            patched = true;
            var h = new Harmony("PerfProbe.UiLists");
            void P(System.Type type, string method, string counter)
            {
                var m = AccessTools.DeclaredMethod(type, method, new[] { typeof(UnityEngine.UI.CanvasUpdate) });
                if (m == null) { Debug.Log($"PERF ui counts: no {type.Name}.{method}"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(UiLists), counter));
            }
            P(typeof(UnityEngine.UI.LayoutRebuilder), "Rebuild", nameof(CountLayout));
            P(typeof(UnityEngine.UI.Graphic), "Rebuild", nameof(CountGraphic));
            P(typeof(TMPro.TextMeshProUGUI), "Rebuild", nameof(CountText));
        }

        // ---- the table columns' 1000 ms refresh ----

        /// Frames in which TableColumn.Render1000ms ran (the render 1000 ms bucket marks every
        /// column dirty, and the table refreshes each widget at its next ScreenUpdate).
        static readonly HashSet<int> tickFrames = new HashSet<int>();
        static void ColumnTick() { if (tickFrames.Count > 4096) tickFrames.Clear(); tickFrames.Add(Time.frameCount); }

        // ---- trace-scrollers ----

        /// `trace-scrollers`: each TableScreen.SetScrollersDirty that moves the table's columns
        /// by more than 0.05 is logged with the scroll rect whose LateUpdate (or other caller) sent
        /// it, its content and view widths, and the frame.
        static UnityEngine.UI.ScrollRect lateUpdating;
        static int scrollerLogs;
        static void LateBegin(UnityEngine.UI.ScrollRect __instance) => lateUpdating = __instance;
        static void LateEnd() => lateUpdating = null;
        static void Dirty(TableScreen __instance, float position)
        {
            float target = Traverse.Create(__instance).Field("targetScrollerPosition").GetValue<float>();
            if (System.Math.Abs(position - target) < 0.05f || scrollerLogs++ > 300) return;
            var s = lateUpdating;
            string from = s == null ? "not in a ScrollRect.LateUpdate: " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", " | ")
                : $"{Path(s.transform)} content {s.content?.rect.width:F1} at {s.content?.anchoredPosition.x:F1} view {((RectTransform)(s.viewport != null ? s.viewport : s.transform)).rect.width:F1} active {s.gameObject.activeInHierarchy}"
                  + $" column tick {(tickFrames.Contains(Time.frameCount) ? "this frame" : tickFrames.Contains(Time.frameCount - 1) ? "last frame" : "no")}";
            Debug.Log($"PERF ui scrollers f{Time.frameCount} {target:F3} -> {position:F3} from {from}");
        }

        /// `trace-content`: (ours only, where RectTransform's setters are managed) each write that
        /// moves the default row's scroller content left is logged with its stack.
        static RectTransform watchedContent;
        static int contentLogs;
        static void ContentWrite(RectTransform __instance, Vector2 value)
        {
            if (watchedContent == null || !ReferenceEquals(__instance, watchedContent) || contentLogs > 40) return;
            if (value.x <= __instance.anchoredPosition.x + 0.5f) return; // only moves back towards the start
            contentLogs++;
            Debug.Log($"PERF ui content write f{Time.frameCount} {__instance.anchoredPosition.x:F1} -> {value.x:F1}: " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", " | "));
        }
        static void ContentLocal(Transform __instance, Vector3 value)
        {
            if (watchedContent == null || !ReferenceEquals(__instance, watchedContent) || contentLogs > 40) return;
            if (System.Math.Abs(value.x - __instance.localPosition.x) < 0.5f) return;
            contentLogs++;
            Debug.Log($"PERF ui content local f{Time.frameCount} {__instance.localPosition.x:F1} -> {value.x:F1}: " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", " | "));
        }

        static int markLogs;
        static void Mark(RectTransform rect)
        {
            if (watchedContent == null || rect == null || markLogs > 30) return;
            var row = watchedContent.parent?.parent;
            bool under = false;
            for (var t = rect.transform; t != null; t = t.parent) if (ReferenceEquals(t, row)) { under = true; break; }
            if (!under) return;
            markLogs++;
            Debug.Log($"PERF ui layout mark f{Time.frameCount} {Path(rect)}: " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", " | "));
        }

        static void TraceContent()
        {
            var table = open as TableScreen;
            TableRow def = null;
            if (table != null) foreach (var r in table.rows) if (r != null && r.rowType == TableRow.RowType.Default) def = r;
            watchedContent = def?.scroll_rect?.content;
            Debug.Log($"PERF ui trace-content: {(watchedContent == null ? "no default row" : Path(watchedContent))}");
            var h = new Harmony("PerfProbe.UiLists.content");
            var set = AccessTools.PropertySetter(typeof(RectTransform), "anchoredPosition");
            try { h.Patch(set, prefix: new HarmonyMethod(typeof(UiLists), nameof(ContentWrite))); } catch (System.Exception e) { Debug.Log($"PERF ui trace-content: anchoredPosition not patched: {e.Message}"); }
            var mark = AccessTools.Method(typeof(UnityEngine.UI.LayoutRebuilder), "MarkLayoutForRebuild", new[] { typeof(RectTransform) });
            try { h.Patch(mark, prefix: new HarmonyMethod(typeof(UiLists), nameof(Mark))); } catch (System.Exception e) { Debug.Log($"PERF ui trace-content: MarkLayoutForRebuild not patched: {e.Message}"); }
            var local = AccessTools.PropertySetter(typeof(Transform), "localPosition");
            try { h.Patch(local, prefix: new HarmonyMethod(typeof(UiLists), nameof(ContentLocal))); } catch (System.Exception e) { Debug.Log($"PERF ui trace-content: localPosition not patched: {e.Message}"); }
        }

        static void TraceScrollers()
        {
            var h = new Harmony("PerfProbe.UiLists.scrollers");
            h.Patch(AccessTools.Method(typeof(TableScreen), "SetScrollersDirty"), prefix: new HarmonyMethod(typeof(UiLists), nameof(Dirty)));
            foreach (var t in new[] { typeof(UnityEngine.UI.ScrollRect), typeof(KScrollRect) })
            {
                var m = AccessTools.DeclaredMethod(t, "LateUpdate");
                if (m == null) continue;
                h.Patch(m, prefix: new HarmonyMethod(typeof(UiLists), nameof(LateBegin)), postfix: new HarmonyMethod(typeof(UiLists), nameof(LateEnd)));
            }
        }

        // ---- a measured span ----

        sealed class Span
        {
            readonly string label;
            public string Label => label;
            readonly int startFrame;
            readonly List<double> ms = new List<double>();
            readonly List<int> frameNumbers = new List<int>();
            readonly long total0, thread0, layout0, graphic0, text0;
            readonly Unity.Profiling.ProfilerRecorder[] recorders = new Unity.Profiling.ProfilerRecorder[RecorderNames.Length];
            readonly double[] sums = new double[RecorderNames.Length];
            long last;

            public Span(string label)
            {
                this.label = label;
                startFrame = Time.frameCount;
                total0 = Total();
                thread0 = Thread();
                layout0 = LayoutRebuilds; graphic0 = GraphicRebuilds; text0 = TextRebuilds;
                for (int i = 0; i < RecorderNames.Length; i++)
                {
                    try
                    {
                        var category = i < 2 ? Unity.Profiling.ProfilerCategory.Internal : Unity.Profiling.ProfilerCategory.Render;
                        recorders[i] = Unity.Profiling.ProfilerRecorder.StartNew(category, RecorderNames[i]);
                    }
                    catch (System.Exception) { }
                }
                last = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            /// After each frame's yield: the frame that just ended.
            public void Frame()
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                ms.Add((now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                frameNumbers.Add(Time.frameCount - 1);
                last = now;
                for (int i = 0; i < recorders.Length; i++)
                    if (recorders[i].Valid) sums[i] += recorders[i].LastValue;
            }

            public void End(string extra = "")
            {
                int n = ms.Count;
                var sorted = new List<double>(ms);
                sorted.Sort();
                double sum = 0;
                foreach (var v in ms) sum += v;
                double P(double q) => n == 0 ? 0 : sorted[System.Math.Min(n - 1, (int)System.Math.Floor(q * n))];
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var text = new System.Text.StringBuilder();
                text.Append(string.Format(inv, "PERF ui {0} f{1}-f{2} n {3} mean {4:F2} p50 {5:F2} p95 {6:F2} p99 {7:F2} max {8:F2} sum {9:F1}",
                    label, startFrame, Time.frameCount, n, n == 0 ? 0 : sum / n, P(0.5), P(0.95), P(0.99), n == 0 ? 0 : sorted[n - 1], sum));
                long total = Total(), thread = Thread();
                if (total0 >= 0 && n > 0) text.Append(string.Format(inv, " alloc {0:F1} KB/f", (total - total0) / 1000.0 / n));
                if (thread0 >= 0 && n > 0) text.Append(string.Format(inv, " main-alloc {0:F1} KB/f", (thread - thread0) / 1000.0 / n));
                if (counting && n > 0)
                    text.Append(string.Format(inv, " layout {0:F1}/f graphic {1:F1}/f text {2:F1}/f",
                        (LayoutRebuilds - layout0) / (double)n, (GraphicRebuilds - graphic0) / (double)n, (TextRebuilds - text0) / (double)n));
                for (int i = 0; i < recorders.Length; i++)
                {
                    if (!recorders[i].Valid) continue;
                    double v = n == 0 ? 0 : sums[i] / n;
                    if (i < 2) text.Append(string.Format(inv, " {0} {1:F2} ms", RecorderNames[i].Replace(' ', '-').ToLowerInvariant(), v / 1e6));
                    else text.Append(string.Format(inv, " {0} {1:F1}", RecorderNames[i].Replace(" Count", "").Replace(' ', '-').ToLowerInvariant(), v));
                    recorders[i].Dispose();
                }
                // The frames of the columns' refresh: the one a Render1000ms ran in and the next.
                int ticks = 0; double tickSum = 0, otherSum = 0;
                for (int i = 0; i < n; i++)
                {
                    int f = frameNumbers[i];
                    if (tickFrames.Contains(f) || tickFrames.Contains(f - 1)) { ticks++; tickSum += ms[i]; }
                    else otherSum += ms[i];
                }
                if (ticks > 0)
                    text.Append(string.Format(inv, " tick-frames {0} mean {1:F2} others {2:F2}", ticks, tickSum / ticks, n > ticks ? otherSum / (n - ticks) : 0));
                text.Append(extra);
                Debug.Log(text.ToString());
            }
        }

        static int captureNext;
        static string probeDir;

        static void Capture(string label, int i)
        {
            var dir = System.IO.Path.Combine(probeDir, "shots");
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{label}-{i:D3}.png"));
        }

        static IEnumerator Measure(string label, int frames, System.Action<int> each = null)
        {
            int capture = captureNext;
            captureNext = 0;
            var span = new Span(label);
            for (int i = 0; i < frames; i++)
            {
                each?.Invoke(i);
                if (i < capture) Capture(label, i);
                yield return null;
                span.Frame();
            }
            span.End();
        }

        // ---- screens ----

        static KScreen ScreenOf(KIconToggleMenu.ToggleInfo info)
        {
            if (Traverse.Create(ManagementMenu.Instance).Field("ScreenInfoMatch").GetValue() is IDictionary match && match.Contains(info)
                && match[info] is ManagementMenu.ScreenData data)
                return data.screen;
            return null;
        }

        static IEnumerator Open(string name)
        {
            var info = AccessTools.Field(typeof(ManagementMenu), name + "Info")?.GetValue(ManagementMenu.Instance) as KIconToggleMenu.ToggleInfo;
            if (info == null) { Debug.Log($"PERF ui: no button {name}"); yield break; }
            var span = new Span("open-" + name);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { ManagementMenu.Instance.OnButtonClick(info); }
            catch (System.Exception e) { Debug.Log($"PERF ui: {name} did not open: {e.GetType().Name} {e.Message}"); }
            double call = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            open = ScreenOf(info);
            openName = name;
            int capture = captureNext;
            captureNext = 0;
            for (int i = 0; i < 180; i++) { if (i < capture) Capture(span.Label, i); yield return null; span.Frame(); }
            span.End(string.Format(System.Globalization.CultureInfo.InvariantCulture, " call {0:F2} ms", call));
        }

        static IEnumerator Close()
        {
            var span = new Span("close-" + openName);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ManagementMenu.Instance.CloseAll();
            double call = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int capture = captureNext;
            captureNext = 0;
            for (int i = 0; i < 180; i++) { if (i < capture) Capture(span.Label, i); yield return null; span.Frame(); }
            span.End(string.Format(System.Globalization.CultureInfo.InvariantCulture, " call {0:F2} ms", call));
            open = null;
        }

        /// The open screen's scroll list with the most to scroll (vertically, or horizontally).
        static UnityEngine.UI.ScrollRect Longest(bool horizontal)
        {
            if (open == null) return null;
            UnityEngine.UI.ScrollRect best = null;
            float most = 0;
            foreach (var s in open.GetComponentsInChildren<UnityEngine.UI.ScrollRect>(false))
            {
                if (s.content == null || (horizontal ? !s.horizontal : !s.vertical)) continue;
                var view = s.viewport != null ? s.viewport : (RectTransform)s.transform;
                float over = horizontal ? s.content.rect.width - view.rect.width : s.content.rect.height - view.rect.height;
                if (over > most) { most = over; best = s; }
            }
            if (best != null) Debug.Log($"PERF ui scroll {(horizontal ? "columns" : "list")} of {openName}: {Path(best.transform)} ({most:F0} px to scroll)");
            else Debug.Log($"PERF ui scroll: nothing to scroll in {openName}");
            return best;
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        static IEnumerator Scroll(string label, int frames, string mode)
        {
            bool horizontal = mode == "h";
            if (open is ResearchScreen research)
            {
                var t = Traverse.Create(research);
                yield return Measure(label, frames, i =>
                {
                    bool right = (i / 120) % 2 == 0;
                    t.Field("panRight").SetValue(right);
                    t.Field("panLeft").SetValue(!right);
                });
                t.Field("panRight").SetValue(false);
                t.Field("panLeft").SetValue(false);
                yield break;
            }
            if (mode == "wheel" || mode == "bar")
            {
                var table = open as TableScreen;
                TableRow first = null, header = null;
                if (table != null)
                    foreach (var r in table.rows)
                    {
                        if (r == null || !r.gameObject.activeInHierarchy) continue;
                        if (r.rowType == TableRow.RowType.Header && header == null) header = r;
                        if (r.rowType == TableRow.RowType.Minion && first == null) first = r;
                    }
                if (mode == "wheel")
                {
                    if (first == null || first.scroll_rect == null) { Debug.Log("PERF ui wheel: no Duplicant row scroller"); yield break; }
                    var target = first.scroll_rect.gameObject;
                    Debug.Log($"PERF ui wheel over {Path(target.transform)}");
                    yield return Measure(label, frames, i =>
                    {
                        if (i % 3 != 0) return;
                        var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { scrollDelta = new Vector2(0f, (i / 120) % 2 == 0 ? -1f : 1f) };
                        UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(target, data, UnityEngine.EventSystems.ExecuteEvents.scrollHandler);
                    });
                }
                else
                {
                    var bar = header == null ? null : Traverse.Create(header).Field("scrollbar").GetValue<UnityEngine.UI.Scrollbar>();
                    if (bar == null) { Debug.Log("PERF ui bar: no header scrollbar"); yield break; }
                    Debug.Log($"PERF ui bar {Path(bar.transform)}");
                    yield return Measure(label, frames, i =>
                    {
                        float phase = (i % 240) / 120f;
                        bar.value = phase <= 1f ? phase : 2f - phase;
                    });
                }
                yield break;
            }
            var rect = Longest(horizontal);
            if (rect == null) yield break;
            yield return Measure(label, frames, i =>
            {
                float phase = (i % 240) / 120f;
                float v = phase <= 1f ? 1f - phase : phase - 1f; // top (1) to bottom (0) and back
                if (horizontal) rect.horizontalNormalizedPosition = 1f - v;
                else rect.verticalNormalizedPosition = v;
            });
        }

        static IEnumerator Toggle(string label, int frames, string kind)
        {
            var table = open as TableScreen;
            if (table == null) { Debug.Log($"PERF ui toggle: {openName} is not a table"); yield break; }
            TableRow row = null;
            foreach (var r in table.rows)
                if (r != null && r.rowType == TableRow.RowType.Minion && r.gameObject.activeInHierarchy) { row = r; break; }
            if (row == null) { Debug.Log("PERF ui toggle: no Duplicant row"); yield break; }
            System.Action<int> act = null;
            if (kind == "consumable")
            {
                var toggle = row.GetComponentInChildren<MultiToggle>(false);
                if (toggle == null || toggle.onClick == null) { Debug.Log("PERF ui toggle: no checkbox"); yield break; }
                Debug.Log($"PERF ui toggle {Path(toggle.transform)}");
                act = i => { if (i % 10 == 0) toggle.onClick(); };
            }
            else
            {
                var selector = row.GetComponentInChildren<OptionSelector>(false);
                if (selector == null) { Debug.Log("PERF ui toggle: no priority"); yield break; }
                var id = Traverse.Create(selector).Field("id").GetValue();
                Debug.Log($"PERF ui toggle {Path(selector.transform)}");
                act = i => { if (i % 10 == 0) selector.OnChangePriority(id, (i / 10) % 2 == 0 ? 1 : -1); };
            }
            yield return Measure(label, frames, act);
        }

        static IEnumerator Search(string label, int frames, string category, string word)
        {
            ManagementMenu.Instance.CloseAll();
            PlanScreen.Instance.OpenCategoryByName(category);
            for (int i = 0; i < 120; i++) yield return null;
            var field = BuildingGroupScreen.Instance.inputField;
            yield return Measure(label, frames, i =>
            {
                if (i % 20 != 0) return;
                int letters = i / 20 + 1;
                field.text = letters <= word.Length ? word.Substring(0, letters) : "";
                field.ForceChangeValueRefresh();
            });
            BuildingGroupScreen.Instance.ClearSearch();
            PlanScreen.Instance.CloseCategoryPanel(false);
        }

        public static IEnumerator Run(string path)
        {
            monoTotal = AllocMeter.MonoTotal();
            probeDir = System.IO.Path.GetDirectoryName(path);
            new Harmony("PerfProbe.UiLists.ticks").Patch(AccessTools.Method(typeof(TableColumn), "Render1000ms"), postfix: new HarmonyMethod(typeof(UiLists), nameof(ColumnTick)));
            for (int i = 0; i < 300; i++) yield return null;
            Screens.DismissDialogs();
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var w = raw.Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 0 || w[0].StartsWith("#")) continue;
                switch (w[0])
                {
                    case "wait": for (int i = int.Parse(w[1]); i > 0; i--) yield return null; break;
                    case "idle": yield return Measure(w[1], int.Parse(w[2])); break;
                    case "open": yield return Open(w[1]); break;
                    case "close": yield return Close(); break;
                    case "scroll": yield return Scroll(w[1], int.Parse(w[2]), w.Length > 3 ? w[3] : ""); break;
                    case "toggle": yield return Toggle(w[1], int.Parse(w[2]), w[3]); break;
                    case "search": yield return Search(w[1], int.Parse(w[2]), w[3], w[4]); break;
                    case "quit": Debug.Log("PERF ui done"); Application.Quit(); yield break;
                    case "trace-scrollers": TraceScrollers(); break;
                    case "trace-content": TraceContent(); break;
                    case "capture": captureNext = int.Parse(w[1]); break;
                    case "counts": Patch(); counting = w[1] == "on"; break;
                    default: Debug.Log($"PERF ui: unknown command {w[0]}"); break;
                }
            }
            Debug.Log("PERF ui done");
        }
    }
}
