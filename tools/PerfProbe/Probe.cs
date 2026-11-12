using UnityEngine;

namespace PerfProbe
{
    /// TEST INFRASTRUCTURE: the same frame-rate measurement in both players. Once a colony is
    /// loaded it keeps the game running at the fastest speed (or `speed.txt`'s; -1 keeps it paused) (when autosaves or events have
    /// paused it, closing dialogs and unpausing, checked every 60 frames) and logs, every 600
    /// frames, the mean frame rate over them from the real-time clock, with the cycle, and (where the
    /// runtime has them: .NET, not Unity's Mono) the window's lock contentions and GC pause. Mods.json
    /// stamp: -2037174645 (the Mono string hash of the folder name PerfProbe, ARCHITECTURE D3).
    public class Mod : KMod.UserMod2
    {
        public override void OnLoad(HarmonyLib.Harmony harmony)
        {
            Worldgen.Install(harmony, path);
            Overlays.Install(harmony, path);
            Rebuilds.Install(harmony, path);
            AllocSites.Install(harmony, path);
            if (System.IO.File.Exists(System.IO.Path.Combine(path, "layout.txt"))) LayoutTrace.Install(harmony);
            var keeper = new GameObject("PerfProbe");
            Object.DontDestroyOnLoad(keeper);
            keeper.AddComponent<Runner>().Path = path;
        }
    }

    /// `layout.txt`: the main menu's message-of-the-day row, traced: the dimension changes its
    /// tiles receive, the layout rebuilds asked for under it, and its layout passes, each with
    /// the frame (the order of events, compared between the players).
    static class LayoutTrace
    {
        static int lines;
        static bool UnderMotd(Transform t)
        {
            for (; t != null; t = t.parent) if (t.name == "MOTD") return true;
            return false;
        }
        static void Log(string text) { if (lines++ < 400) Debug.Log($"PERF layout f{Time.frameCount} {text}"); }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            var element = HarmonyLib.AccessTools.Method("MotdBox_ImageButtonLayoutElement:OnRectTransformDimensionsChange");
            harmony.Patch(element, postfix: new HarmonyLib.HarmonyMethod(typeof(LayoutTrace), nameof(Dimensions)));
            var mark = typeof(UnityEngine.UI.LayoutRebuilder).GetMethod("MarkLayoutForRebuild", new[] { typeof(RectTransform) });
            harmony.Patch(mark, prefix: new HarmonyLib.HarmonyMethod(typeof(LayoutTrace), nameof(Mark)));
            var calc = typeof(UnityEngine.UI.HorizontalLayoutGroup).GetMethod("SetLayoutHorizontal");
            harmony.Patch(calc, postfix: new HarmonyLib.HarmonyMethod(typeof(LayoutTrace), nameof(Horizontal)));
            var registry = typeof(UnityEngine.UI.CanvasUpdateRegistry).GetMethod("IsRebuildingLayout");
            Debug.Log($"PERF layout tracing installed (IsRebuildingLayout {(registry != null ? "present" : "absent")})");
        }

        static void Dimensions(Component __instance)
        {
            var rt = (RectTransform)__instance.transform;
            var le = (UnityEngine.UI.LayoutElement)__instance;
            Log($"tile {__instance.transform.parent?.name} size {rt.rect.size} preferredWidth {le.preferredWidth} active {__instance.gameObject.activeInHierarchy} rebuilding {UnityEngine.UI.CanvasUpdateRegistry.IsRebuildingLayout()}");
        }

        static void Mark(RectTransform rect)
        {
            if (rect != null && UnderMotd(rect)) Log($"mark {rect.name} rebuilding {UnityEngine.UI.CanvasUpdateRegistry.IsRebuildingLayout()}");
        }

        static void Horizontal(Component __instance)
        {
            if (__instance.name != "MOTD") return;
            var g = (UnityEngine.UI.HorizontalLayoutGroup)__instance;
            Log($"MOTD row laid out: preferredWidth {g.preferredWidth}, first child at {((RectTransform)__instance.transform.GetChild(0)).anchoredPosition}");
        }
    }

    public class Runner : MonoBehaviour
    {
        public static Runner Instance;

        /// The centre of the 48×28-cell window (about the view at orthographic size 12) of a world
        /// holding the fewest building cells, searched in steps of 8 cells.
        static int BuildingCells(int x, int y, int w, int h)
        {
            int count = 0;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    if (Grid.Objects[Grid.XYToCell(x + i, y + j), (int)ObjectLayer.Building] != null) count++;
            return count;
        }

        /// `truetiles.txt` beside the probe: once the colony is up, what True Tiles holds and which
        /// main textures the game's tile renderers draw with.
        static void ReportTileTextures()
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            var assetsType = FindType("TrueTiles.Cmps.TileAssets");
            var instance = assetsType?.GetProperty("Instance", flags)?.GetValue(null);
            var table = instance == null ? null : assetsType.GetField("textureAssets", flags)?.GetValue(instance) as System.Collections.IDictionary;
            Debug.Log($"PERF truetiles assets: {(assetsType == null ? "no TileAssets" : instance == null ? "no instance" : table == null ? "no table" : table.Count + " defs")}");
            var renderer = World.Instance.blockTileRenderer;
            var infos = typeof(Rendering.BlockTileRenderer).GetField("renderInfo", flags).GetValue(renderer) as System.Collections.IDictionary;
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var info in infos.Values)
            {
                var material = info.GetType().GetField("material", flags).GetValue(info) as Material;
                var name = material == null ? "null" : material.HasProperty("_MainTex") && material.GetTexture("_MainTex") is { } t ? t.name : "none";
                counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            Debug.Log($"PERF truetiles renderers: {infos.Count} [{string.Join(", ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Select(counts, kv => kv.Key + "×" + kv.Value), 30))}]");
        }

        /// The view camera.txt asked for, held there.
        internal (Vector3 At, float Size)? view;

        static Vector3 QuietestView(WorldContainer world)
        {
            const int w = 48, h = 28;
            int bestCount = int.MaxValue, bestX = 0, bestY = 0;
            for (int y = world.WorldOffset.y; y + h <= world.WorldOffset.y + world.WorldSize.y; y += 8)
                for (int x = world.WorldOffset.x; x + w <= world.WorldOffset.x + world.WorldSize.x; x += 8)
                {
                    int count = BuildingCells(x, y, w, h);
                    if (count < bestCount) { bestCount = count; bestX = x; bestY = y; }
                }
            Debug.Log($"PERF quietest view at cells {bestX},{bestY}: {bestCount} building cells");
            return new Vector3(bestX + w * 0.5f, bestY + h * 0.5f, 0f);
        }

        /// `savetest.txt` in the mod's folder (our player with the fast-serialization game tweak):
        /// once the colony is up, pauses and saves in the order the file lists, each with the
        /// game's serializer (g) or the tweak's (f), for comparing the files; consecutive saves with
        /// the game's own show what changes between saves by itself. The tweak is switched through
        /// its test flag by reflection.
        /// `autosave.txt`: the game's own autosave (GameClock.DoAutoSave, as at a cycle's end) three
        /// times, 900 frames apart, each timed as the frames it stalls: the longest frame, and
        /// the total of frames over 100 ms in the 300 frames after it starts. A frame count in
        /// the file waits that many frames first, beside the frame rate windows, so one launch
        /// measures both. The game's own cycle-end autosave is held off through the autosaves:
        /// one overlapping the probe's breaks the save indicator (SaveActive.DoCallBack).
        System.Collections.IEnumerator AutosaveFreeze()
        {
            SpeedControlScreen.Instance.Unpause(false);
            int.TryParse(System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "autosave.txt")).Trim(), out int delay);
            for (int i = 0; i < delay; i++) yield return null;
            Video.NoAutosave(true);
            Debug.Log($"PERF autosave start f{Time.frameCount} after {delay} frames");
            var doAutoSave = HarmonyLib.AccessTools.Method(typeof(GameClock), "DoAutoSave");
            for (int n = 1; n <= 3; n++)
            {
                for (int i = 0; i < 900; i++) yield return null;
                // Let through for the probe's own call only, as in Video.Actions.
                Video.NoAutosave(false);
                doAutoSave.Invoke(GameClock.Instance, new object[] { GameClock.Instance.GetCycle() });
                Video.NoAutosave(true);
                // 20 s of real time, whatever the frame rate: the game starts writing a few
                // seconds after the autosave is asked for (300 frames, as before, ended before it
                // at 80 fps).
                float longest = 0f, stalled = 0f, last = Time.realtimeSinceStartup, end = last + 20f;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    float now = Time.realtimeSinceStartup, frame = (now - last) * 1000f;
                    last = now;
                    if (frame > longest) longest = frame;
                    if (frame > 100f) stalled += frame;
                }
                Debug.Log($"PERF autosave {n}: longest frame {longest:F0} ms, frames over 100 ms {stalled:F0} ms");
            }
            Debug.Log("PERF autosave done");
        }

        System.Collections.IEnumerator SaveTest()
        {
            SpeedControlScreen.Instance.Pause(false);
            for (int i = 0; i < 120; i++) yield return null;
            var flag = (System.Type.GetType("OniMoono.Host.GameTweaks.FastSerialization, OniMoono") ?? System.Type.GetType("OniBrutal.Host.GameTweaks.FastSerialization, OniMoono") ?? System.Type.GetType("OniBrutal.Host.GameTweaks.FastSerialization, OniBrutal"))?.GetField("Enabled");
            if (flag == null) { Debug.Log("PERF savetest: fast-serialization not present"); yield break; }
            var dir = System.IO.Path.GetDirectoryName(SaveLoader.GetActiveSaveFilePath());
            // savetest.txt lists the saves to make, g (the game's serializer) or f (the tweak's).
            var order = System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "savetest.txt")).Split(new[] { ' ', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (order.Length == 0) order = new[] { "g", "g", "f" };
            for (int n = 0; n < order.Length; n++)
            {
                bool fast = order[n] == "f";
                string name = $"savetest-{n + 1}-{(fast ? "fast" : "game")}";
                flag.SetValue(null, fast);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                SaveLoader.Instance.Save(System.IO.Path.Combine(dir, name + ".sav"), false, false);
                Debug.Log($"PERF savetest {name} {watch.Elapsed.TotalMilliseconds:F0} ms");
                for (int i = 0; i < 30; i++) yield return null;
            }
            flag.SetValue(null, true);
            Debug.Log("PERF savetest done");
        }

        /// The mod's folder.
        public string Path;

        /// Counts OnTransformChildrenChanged.
        /// MonoBehaviour.Invoke targets: each call is logged with the frame and phase it arrived in.
        public class InvokeTarget : MonoBehaviour
        {
            public string Phase = "start";
            public System.Collections.Generic.List<string> Calls = new();
            void Update() => Phase = "update";
            void LateUpdate() => Phase = "late";
            void Hit() { Calls.Add($"f{Time.frameCount} after {Phase} at {Time.time:F2}"); }
            void Repeat() { Calls.Add($"repeat f{Time.frameCount}"); }
        }

        public class ChildWatcher : MonoBehaviour
        {
            public int Count;
            void OnTransformChildrenChanged() => Count++;
        }

        /// Behaviours the defect review asked about, logged as "PERF measure ...": which changes of
        /// a transform's children send OnTransformChildrenChanged, and how graphics that draw no
        /// geometry or sit under a CanvasGroup at alpha 0 are given depths and hit by raycasts.
        /// The hover card's drawn widgets: how many are active, and the first one's screen position.
        static string Widgets()
        {
            if (HoverTextScreen.Instance == null) return "none";
            var root = HoverTextScreen.Instance.transform.childCount > 0 ? HoverTextScreen.Instance.transform.GetChild(0) : null;
            int active = 0;
            string first = "";
            foreach (var t in HoverTextScreen.Instance.GetComponentsInChildren<RectTransform>(false))
            {
                if (t == HoverTextScreen.Instance.transform) continue;
                active++;
                if (first.Length == 0) first = $" first {t.name} at {t.position}";
            }
            return active + first + (root != null ? $" root {root.name} scale {root.localScale}" : "");
        }

        Vector2 guiPointer = new Vector2(-1f, -1f);

        /// IMGUI's pointer position, from whatever event reaches the probe (Repaint at least).
        void OnGUI()
        {
            if (Event.current != null && (Event.current.type == EventType.Repaint || Event.current.isMouse)) guiPointer = Event.current.mousePosition;
            if (measureGuiFrames > 0) MeasureGui();
        }

        /// `gui.txt` beside the probe: IMGUI's toolbar and modal window, over a few frames.
        int measureGuiFrames;
        readonly System.Collections.Generic.List<string> modalCalls = new();

        void MeasureGui()
        {
            var e = Event.current;
            if (e.type == EventType.Layout && measureGuiFrames == 3)
            {
                string Style(string name) { var st = GUI.skin.FindStyle(name); return st == null ? "none" : $"{st.name} fixed {st.fixedWidth}x{st.fixedHeight} margin {st.margin} padding {st.padding} border {st.border} stretch {st.stretchWidth}"; }
                Debug.Log($"PERF measure gui styles: button [{Style("button")}] left [{Style("buttonleft")}] mid [{Style("buttonmid")}] right [{Style("buttonright")}] window [{Style("window")}]");
            }
            GUILayout.BeginArea(new Rect(10, 10, 400, 200));
            int selected = GUILayout.Toolbar(1, new[] { "A", "BB", "CCC" });
            var toolbar = GUILayoutUtility.GetLastRect();
            GUILayout.Label("after");
            var after = GUILayoutUtility.GetLastRect();
            int wide = GUILayout.Toolbar(0, new[] { "Long label one", "x" }, GUILayout.Width(300));
            var wideRect = GUILayoutUtility.GetLastRect();
            GUILayout.EndArea();
            var modal = GUI.ModalWindow(4242, new Rect(100, 100, 200, 120), id => { modalCalls.Add($"{Event.current.type}:{id}"); GUI.Label(new Rect(10, 30, 100, 20), "in modal"); }, "Modal");
            if (e.type == EventType.Repaint)
            {
                Debug.Log($"PERF measure gui toolbar f{Time.frameCount}: selected {selected} rect {toolbar} after {after} wide {wide} {wideRect} buttons {GUI.skin.button.CalcSize(new GUIContent("A"))} {GUI.skin.button.CalcSize(new GUIContent("BB"))} {GUI.skin.button.CalcSize(new GUIContent("CCC"))}");
                Debug.Log($"PERF measure gui modal f{Time.frameCount}: returned {modal} calls [{string.Join(",", modalCalls)}]");
                modalCalls.Clear();
                measureGuiFrames--;
            }
        }

        /// The EventSystem's first raycast hits at the pointer: path, raycaster, depth.
        static string Hits(UnityEngine.EventSystems.EventSystem events)
        {
            if (events == null) return "";
            var results = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            events.RaycastAll(new UnityEngine.EventSystems.PointerEventData(events) { position = Input.mousePosition }, results);
            string Path(Transform t) => t == null ? "" : Path(t.parent) + "/" + t.name;
            return string.Join("; ", results.GetRange(0, System.Math.Min(4, results.Count)).ConvertAll(r => $"{Path(r.gameObject.transform)} by {(r.module == null ? "none" : r.module.GetType().Name)} depth {r.depth} sort {r.sortingOrder}"));
        }

        System.Collections.IEnumerator Pointer()
        {
            for (int n = 0; n < 6; n++)
            {
                for (int i = 0; i < 120; i++) yield return null;
                var events = UnityEngine.EventSystems.EventSystem.current;
                var tool = PlayerController.Instance != null ? PlayerController.Instance.ActiveTool : null;
                Debug.Log($"PERF pointer f{Time.frameCount} position {Input.mousePosition} gui {guiPointer} screen {Screen.width}x{Screen.height} present {Input.mousePresent} focused {Application.isFocused}"
                    + $" over-ui {(events != null ? events.IsPointerOverGameObject().ToString() : "no event system")}"
                    + $" module {(events != null && events.currentInputModule != null ? events.currentInputModule.GetType().Name : "none")}"
                    + $" tool {(tool != null ? tool.GetType().Name : "none")} hover-card {(HoverTextScreen.Instance != null && HoverTextScreen.Instance.gameObject.activeInHierarchy)}"
                    + $" show-hover {(tool != null && tool.ShowHoverUI())} widgets {Widgets()} hits [{Hits(events)}]");
            }
        }

        System.Collections.IEnumerator Measure()
        {
            var parent = new GameObject("MeasureParent");
            var watcher = parent.AddComponent<ChildWatcher>();
            var prefab = new GameObject("MeasurePrefab");
            int before = watcher.Count;
            var a = Instantiate(prefab, parent.transform);
            Debug.Log($"PERF measure children: Instantiate(original, parent) {watcher.Count - before}");
            before = watcher.Count;
            Instantiate(prefab, Vector3.zero, Quaternion.identity, parent.transform);
            Debug.Log($"PERF measure children: Instantiate(original, position, rotation, parent) {watcher.Count - before}");
            yield return null;
            before = watcher.Count;
            Destroy(a);
            Debug.Log($"PERF measure children: Destroy(child) same frame {watcher.Count - before}");
            yield return null;
            Debug.Log($"PERF measure children: Destroy(child) by the next frame {watcher.Count - before}");
            before = watcher.Count;
            var b = new GameObject("B");
            b.transform.SetParent(parent.transform);
            Debug.Log($"PERF measure children: SetParent {watcher.Count - before}");
            before = watcher.Count;
            DestroyImmediate(b);
            Debug.Log($"PERF measure children: DestroyImmediate(child) {watcher.Count - before}");
            before = watcher.Count;
            var inactive = new GameObject("Inactive");
            inactive.SetActive(false);
            inactive.transform.SetParent(parent.transform);
            Debug.Log($"PERF measure children: SetParent of an inactive child {watcher.Count - before}");
            before = watcher.Count;
            Destroy(inactive);
            yield return null;
            Debug.Log($"PERF measure children: Destroy(inactive child) {watcher.Count - before}");
            Destroy(parent);
            Destroy(prefab);

            var canvasGo = new GameObject("MeasureCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            UnityEngine.UI.Graphic Make(string name, Transform under, Vector2 at, System.Type type)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(under, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(80f, 80f);
                rt.anchoredPosition = at;
                return (UnityEngine.UI.Graphic)go.AddComponent(type);
            }
            var empty = Make("EmptyGraphic", canvasGo.transform, new Vector2(-200f, 0f), typeof(NonDrawingGraphic));
            var groupGo = new GameObject("AlphaZeroGroup", typeof(RectTransform));
            groupGo.transform.SetParent(canvasGo.transform, false);
            groupGo.AddComponent<CanvasGroup>().alpha = 0f;
            var hidden = Make("UnderAlphaZero", groupGo.transform, new Vector2(200f, 0f), typeof(UnityEngine.UI.Image));
            var shown = Make("Shown", canvasGo.transform, new Vector2(0f, 200f), typeof(UnityEngine.UI.Image));
            for (int i = 0; i < 3; i++) yield return null;
            Debug.Log($"PERF measure depth: empty {empty.depth}, under alpha 0 {hidden.depth}, shown {shown.depth}");
            var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            foreach (var g in new[] { empty, hidden, shown })
            {
                var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                { position = RectTransformUtility.WorldToScreenPoint(null, g.rectTransform.position) };
                hits.Clear();
                UnityEngine.EventSystems.EventSystem.current.RaycastAll(data, hits);
                Debug.Log($"PERF measure raycast at {g.name}: first {(hits.Count > 0 ? hits[0].gameObject.name : "none")}, hits {string.Join(",", hits.ConvertAll(h => h.gameObject.name))}");
            }
            Destroy(canvasGo);
            var current = Screen.currentResolution;
            Debug.Log($"PERF measure screen: current {current.width}x{current.height} @{current.refreshRateRatio.value:F2}, window {Screen.width}x{Screen.height}, modes {string.Join(" ", System.Array.ConvertAll(Screen.resolutions, r => $"{r.width}x{r.height}@{r.refreshRateRatio.value:F2}"))}");
            // SetParent keeping the world transform under a parent scaled to zero (HoverTextDrawer's
            // pools parent their roots so, under a canvas not yet scaled), and a new overlay
            // canvas's scale before and after its first frame.
            var zero = new GameObject("ZeroScaled", typeof(RectTransform));
            zero.transform.localScale = Vector3.zero;
            var plain = new GameObject("Plain");
            plain.transform.SetParent(zero.transform);
            var rect = new GameObject("Rect");
            rect.AddComponent<RectTransform>().SetParent(zero.transform);
            var scaled = new GameObject("Scaled");
            scaled.transform.localScale = new Vector3(2f, 3f, 4f);
            scaled.transform.SetParent(zero.transform);
            Debug.Log($"PERF measure zero parent: plain {plain.transform.localScale} rect {rect.transform.localScale} scaled {scaled.transform.localScale}, lossy {plain.transform.lossyScale}");
            var halfAxis = new GameObject("HalfZero");
            halfAxis.transform.localScale = new Vector3(2f, 0f, 1f);
            var under = new GameObject("UnderHalfZero");
            under.transform.SetParent(halfAxis.transform);
            Debug.Log($"PERF measure (2,0,1) parent: {under.transform.localScale}");
            Destroy(zero); Destroy(halfAxis);
            var fresh = new GameObject("FreshCanvas", typeof(RectTransform));
            var freshCanvas = fresh.AddComponent<Canvas>();
            freshCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Debug.Log($"PERF measure new overlay canvas scale: at once {fresh.transform.localScale}");
            yield return null;
            Debug.Log($"PERF measure new overlay canvas scale: next frame {fresh.transform.localScale}");
            Destroy(fresh);
            // An overlay canvas saved at scale zero (as prefab canvases are): when its scale is applied.
            var saved = new GameObject("SavedCanvas", typeof(RectTransform));
            saved.SetActive(false);
            saved.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            saved.transform.localScale = Vector3.zero;
            var savedChild = new GameObject("SavedChild", typeof(RectTransform));
            savedChild.transform.SetParent(saved.transform, false);
            saved.SetActive(true);
            var keptChild = new GameObject("KeptChild");
            keptChild.AddComponent<RectTransform>().SetParent(savedChild.transform);
            Debug.Log($"PERF measure zero-saved overlay canvas: on activation {saved.transform.localScale}, a child's lossy {savedChild.transform.lossyScale}, kept-world child {keptChild.transform.localScale}");
            var prefabCanvas = Instantiate(saved);
            Debug.Log($"PERF measure zero-saved overlay canvas: instantiated {prefabCanvas.transform.localScale}");
            saved.transform.localScale = Vector3.zero;
            Debug.Log($"PERF measure zero-saved overlay canvas: zeroed while active {saved.transform.localScale}");
            yield return null;
            Debug.Log($"PERF measure zero-saved overlay canvas: next frame {saved.transform.localScale}, instantiated {prefabCanvas.transform.localScale}");
            Destroy(saved); Destroy(prefabCanvas);
            // What activation applies besides the scale, and a Dropdown-like clone: a nested,
            // anchored canvas-bearing template instantiated at the root, then nested again.
            string Rect(Transform t) { var r = (RectTransform)t; return $"anchors {r.anchorMin}-{r.anchorMax} pivot {r.pivot} size {r.sizeDelta} pos {r.anchoredPosition} scale {r.localScale}"; }
            var odd = new GameObject("OddCanvas", typeof(RectTransform));
            odd.SetActive(false);
            odd.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var oddRect = (RectTransform)odd.transform;
            oddRect.anchorMin = new Vector2(0.2f, 0.3f); oddRect.anchorMax = new Vector2(0.4f, 0.5f); oddRect.pivot = new Vector2(0f, 1f);
            oddRect.sizeDelta = new Vector2(10f, 20f); oddRect.anchoredPosition = new Vector2(5f, 6f); oddRect.localScale = Vector3.zero;
            odd.SetActive(true);
            Debug.Log($"PERF measure odd overlay canvas on activation: {Rect(odd.transform)}");
            var host = new GameObject("HostCanvas", typeof(RectTransform));
            host.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(host.transform, false);
            var templateRect = (RectTransform)template.transform;
            templateRect.anchorMin = new Vector2(0f, 0f); templateRect.anchorMax = new Vector2(1f, 0f); templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.sizeDelta = new Vector2(0f, 150f); templateRect.anchoredPosition = new Vector2(0f, 2f);
            var nested = template.AddComponent<Canvas>();
            nested.overrideSorting = true;
            var clone = Instantiate(template);
            Debug.Log($"PERF measure clone at the root: {Rect(clone.transform)}");
            clone.transform.SetParent(host.transform, false);
            Debug.Log($"PERF measure clone nested again: {Rect(clone.transform)}");
            yield return null;
            Debug.Log($"PERF measure clone next frame: {Rect(clone.transform)}");
            Destroy(odd); Destroy(host); Destroy(clone);
            // Which enables lay a root canvas out at once.
            GameObject Zeroed(string name, bool active, bool setMode)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.SetActive(active);
                var c = go.AddComponent<Canvas>();
                if (setMode) c.renderMode = RenderMode.ScreenSpaceOverlay;
                go.transform.localScale = Vector3.zero;
                return go;
            }
            var defaultMode = Zeroed("DefaultMode", false, false);
            defaultMode.SetActive(true);
            Debug.Log($"PERF measure enable: activated, render mode never set {Rect(defaultMode.transform)}");
            var toggled = Zeroed("Toggled", true, true);
            toggled.GetComponent<Canvas>().enabled = false;
            toggled.GetComponent<Canvas>().enabled = true;
            Debug.Log($"PERF measure enable: component re-enabled {Rect(toggled.transform)}");
            var reactivated = Zeroed("Reactivated", true, true);
            reactivated.SetActive(false);
            reactivated.SetActive(true);
            Debug.Log($"PERF measure enable: object re-activated {Rect(reactivated.transform)}");
            var inactivePrefab = Zeroed("InactivePrefab", false, true);
            var fromInactive = Instantiate(inactivePrefab);
            Debug.Log($"PERF measure enable: instantiated inactive {Rect(fromInactive.transform)}");
            fromInactive.SetActive(true);
            Debug.Log($"PERF measure enable: instantiated inactive, then activated {Rect(fromInactive.transform)}");
            var activeZero = Zeroed("ActiveZero", true, true);
            var fromActive = Instantiate(activeZero);
            Debug.Log($"PERF measure enable: instantiated active zero-scaled {Rect(fromActive.transform)}");
            var nestedHost = new GameObject("NestedHost", typeof(RectTransform));
            nestedHost.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var underHost = Zeroed("UnderHost", false, true);
            underHost.transform.SetParent(nestedHost.transform, false);
            underHost.transform.localScale = Vector3.zero;
            underHost.SetActive(true);
            Debug.Log($"PERF measure enable: nested canvas activated {Rect(underHost.transform)}");
            var moved = Zeroed("MovedOut", true, true);
            moved.transform.SetParent(nestedHost.transform, false);
            moved.transform.localScale = Vector3.zero;
            moved.transform.SetParent(null, false);
            Debug.Log($"PERF measure enable: active nested canvas moved to the root {Rect(moved.transform)}");
            var probeDefault = new GameObject("DefaultCanvas", typeof(RectTransform));
            var defaultCanvas = probeDefault.AddComponent<Canvas>();
            Debug.Log($"PERF measure enable: AddComponent<Canvas> render mode {defaultCanvas.renderMode}, active default-mode canvas laid out {probeDefault.transform.localScale} size {((RectTransform)probeDefault.transform).sizeDelta}");
            Destroy(probeDefault);
            foreach (var g in new[] { defaultMode, toggled, reactivated, inactivePrefab, fromInactive, activeZero, fromActive, nestedHost, moved }) Destroy(g);
            // Serializable class fields of a component made by AddComponent (the warp conduit
            // receiver's prefab adds a ConduitSecondaryOutput and never sets its port info).
            var holder = new GameObject("SerializableHolder");
            var output = holder.AddComponent<ConduitSecondaryOutput>();
            Debug.Log($"PERF measure AddComponent serializable class field: active {(output.portInfo == null ? "null" : $"instance {output.portInfo.conduitType}")}");
            var inactiveHolder = new GameObject("InactiveHolder");
            inactiveHolder.SetActive(false);
            var inactiveOutput = inactiveHolder.AddComponent<ConduitSecondaryOutput>();
            Debug.Log($"PERF measure AddComponent serializable class field: inactive {(inactiveOutput.portInfo == null ? "null" : "instance")}");
            var copy = Instantiate(holder);
            Debug.Log($"PERF measure Instantiate serializable class field: source {(output.portInfo == null ? "null" : $"instance {output.portInfo.conduitType}")}, clone {(copy.GetComponent<ConduitSecondaryOutput>().portInfo == null ? "null" : "instance")}");
            var arrayHolder = new GameObject("ArrayHolder");
            var interacts = arrayHolder.AddComponent<Activatable>();
            Debug.Log($"PERF measure AddComponent array field: workAnims {(interacts.workAnims == null ? "null" : "length " + interacts.workAnims.Length)}");
            var arrayCopy = Instantiate(arrayHolder);
            Debug.Log($"PERF measure Instantiate array field: source {(interacts.workAnims == null ? "null" : "length " + interacts.workAnims.Length)}, clone {(arrayCopy.GetComponent<Activatable>().workAnims == null ? "null" : "length " + arrayCopy.GetComponent<Activatable>().workAnims.Length)}");
            // A null array field (inactive, so the component is not awoken).
            var tileHolder = new GameObject("TileHolder");
            tileHolder.SetActive(false);
            var tileable = tileHolder.AddComponent<AnimTileable>();
            var tileCopy = Instantiate(tileHolder);
            Debug.Log($"PERF measure Instantiate null array field: source {(tileable.tags == null ? "null" : "length " + tileable.tags.Length)}, clone {(tileCopy.GetComponent<AnimTileable>().tags == null ? "null" : "length " + tileCopy.GetComponent<AnimTileable>().tags.Length)}");
            Destroy(tileHolder); Destroy(tileCopy);
            Destroy(copy); Destroy(arrayHolder); Destroy(arrayCopy);
            Destroy(holder); Destroy(inactiveHolder);
            Debug.Log($"PERF measure colors: darkGray {Color.darkGray} orange {Color.orange} violet {Color.violet}");
            // MonoBehaviour.Invoke: when it calls, and on disabled or inactive targets.
            var enabledTarget = new GameObject("InvokeEnabled").AddComponent<InvokeTarget>();
            var disabledTarget = new GameObject("InvokeDisabled").AddComponent<InvokeTarget>();
            disabledTarget.enabled = false;
            var inactiveTarget = new GameObject("InvokeInactive").AddComponent<InvokeTarget>();
            var zeroTarget = new GameObject("InvokeZero").AddComponent<InvokeTarget>();
            var repeatTarget = new GameObject("InvokeRepeat").AddComponent<InvokeTarget>();
            var started = Time.time;
            enabledTarget.Invoke("Hit", 0.2f);
            disabledTarget.Invoke("Hit", 0.2f);
            inactiveTarget.Invoke("Hit", 0.2f);
            inactiveTarget.gameObject.SetActive(false);
            zeroTarget.Invoke("Hit", 0f);
            repeatTarget.InvokeRepeating("Repeat", 0f, 0.1f);
            Debug.Log($"PERF measure invoke: started f{Time.frameCount} at {started:F2}, IsInvoking {enabledTarget.IsInvoking("Hit")} {enabledTarget.IsInvoking()}, inactive IsInvoking {inactiveTarget.IsInvoking()}");
            enabledTarget.Invoke("Missing", 0f);
            float until = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < until) yield return null;
            repeatTarget.CancelInvoke("Repeat");
            Debug.Log($"PERF measure invoke: enabled [{string.Join("; ", enabledTarget.Calls)}] disabled [{string.Join("; ", disabledTarget.Calls)}] inactive [{string.Join("; ", inactiveTarget.Calls)}] zero [{string.Join("; ", zeroTarget.Calls)}] repeats {repeatTarget.Calls.Count} after-cancel IsInvoking {repeatTarget.IsInvoking()} timeScale {Time.timeScale}");
            foreach (var t in new MonoBehaviour[] { enabledTarget, disabledTarget, inactiveTarget, zeroTarget, repeatTarget }) Destroy(t.gameObject);
            // measure-bundle beside the probe: an asset bundle with UI prefabs. Which of its loaded
            // objects DestroyImmediate removes without allowDestroyingAssets.
            var bundlePath = System.IO.Path.Combine(Path, "measure-bundle");
            if (System.IO.File.Exists(bundlePath))
            {
                var bundle = AssetBundle.LoadFromFile(bundlePath);
                GameObject withText = null, other = null;
                foreach (var name in bundle.GetAllAssetNames())
                {
                    var asset = bundle.LoadAsset<GameObject>(name);
                    if (asset == null) continue;
                    if (withText == null && asset.GetComponentInChildren<UnityEngine.UI.Text>(true) != null) withText = asset;
                    else if (other == null) other = asset;
                }
                if (withText != null)
                {
                    var text = withText.GetComponentInChildren<UnityEngine.UI.Text>(true);
                    var textHolder = text.gameObject;
                    DestroyImmediate(text);
                    Debug.Log($"PERF measure bundle prefab: component destroyed {text == null}, then AddComponent<Text> {(textHolder.AddComponent<UnityEngine.UI.Text>() != null ? "added" : "null")}");
                    var child = withText.transform.childCount > 0 ? withText.transform.GetChild(0).gameObject : null;
                    if (child != null)
                    {
                        DestroyImmediate(child);
                        Debug.Log($"PERF measure bundle prefab: child GameObject destroyed {child == null}");
                    }
                }
                if (other != null)
                {
                    DestroyImmediate(other);
                    Debug.Log($"PERF measure bundle prefab: root GameObject destroyed {other == null}");
                }
                var texture = new Texture2D(2, 2);
                Debug.Log("PERF measure bundle: done");
                Destroy(texture);
            }
            // A prefab from the game's own data (not a scene object, not from a bundle): its
            // Transform's sibling component destroyed without allowDestroyingAssets.
            GameObject dataPrefab = null;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (!go.scene.IsValid() && go.transform.parent == null && go.GetComponents<Component>().Length > 1 && go.name.Length > 0) { dataPrefab = go; break; }
            if (dataPrefab != null)
            {
                var victim = dataPrefab.GetComponents<Component>()[1];
                var victimType = victim.GetType().Name;
                DestroyImmediate(victim);
                Debug.Log($"PERF measure data prefab {dataPrefab.name}: {victimType} destroyed {victim == null}");
            }
            MeasurePatchedFrames();
            try { MeasureTmpColors(); } catch (System.MissingMethodException e) { Debug.Log("PERF measure tmp colors: " + e.Message); }
            try { MeasurePackTextures(); } catch (System.MissingMethodException e) { Debug.Log("PERF measure pack: " + e.Message); }
            try { JsonProbe.Measure(); } catch (System.MissingMethodException e) { Debug.Log("PERF measure json: " + e.Message); }
            try { MeasureShaderProperties(); } catch (System.Exception e) when (e is System.MissingMethodException || e is System.TypeLoadException) { Debug.Log("PERF measure shader props: " + e.Message); }
            yield return MeasureInputActivation();
            yield return MeasureModRendering();
            MeasurePopularModApis();
            MeasureDefaultControls();
            MeasureMissingScripts();
            MeasureLineRenderer();
            MeasureZeroScale();
            yield return MeasureZeroScaledMenu();
            yield return MeasureLateInputField();
            MeasureThirdHundredApis();
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "gui.txt"))) measureGuiFrames = 3;
            // PlayerPrefs (a mod keeps its settings in them): defaults, and the values written to the
            // player's prefs file by Save (read from the file after the run).
            Debug.Log($"PERF measure prefs defaults: int {PlayerPrefs.GetInt("PerfProbeMissing")} float {PlayerPrefs.GetFloat("PerfProbeMissing")} string [{PlayerPrefs.GetString("PerfProbeMissing")}] has {PlayerPrefs.HasKey("PerfProbeMissing")} default-arg {PlayerPrefs.GetInt("PerfProbeMissing", 7)}");
            PlayerPrefs.SetInt("PerfProbeInt", -42);
            PlayerPrefs.SetFloat("PerfProbeFloat", 1.5f);
            PlayerPrefs.SetFloat("PerfProbeFloatThird", 1f / 3f);
            PlayerPrefs.SetString("PerfProbeString", "héllo, <x> & \"y\"\nline2");
            PlayerPrefs.SetString("PerfProbeEmpty", "");
            PlayerPrefs.SetInt("PerfProbeRetyped", 5);
            PlayerPrefs.SetString("PerfProbeRetyped", "now a string");
            Debug.Log($"PERF measure prefs read back: int {PlayerPrefs.GetInt("PerfProbeInt")} float {PlayerPrefs.GetFloat("PerfProbeFloat"):R} string [{PlayerPrefs.GetString("PerfProbeString")}] retyped as int {PlayerPrefs.GetInt("PerfProbeRetyped")} as string [{PlayerPrefs.GetString("PerfProbeRetyped")}] int as float {PlayerPrefs.GetFloat("PerfProbeInt")} has {PlayerPrefs.HasKey("PerfProbeInt")}");
            PlayerPrefs.SetInt("PerfProbeDeleted", 1);
            PlayerPrefs.DeleteKey("PerfProbeDeleted");
            PlayerPrefs.Save();
            Debug.Log($"PERF measure prefs saved: deleted has {PlayerPrefs.HasKey("PerfProbeDeleted")}");
            // Exceptions nothing catches on other threads (mods' background work): a thread, a
            // thread-pool item, an async void method's continuation.
            new System.Threading.Thread(() => throw new System.InvalidOperationException("PERF thread exception")).Start();
            System.Threading.ThreadPool.QueueUserWorkItem(_ => throw new System.InvalidOperationException("PERF pool exception"));
            System.Threading.Tasks.Task.Run(() => AsyncVoidThrows());
            for (int i = 0; i < 120; i++) yield return null;
            Debug.Log($"PERF measure threads: still running after background exceptions, thread {System.Threading.Thread.CurrentThread.ManagedThreadId}");
            Debug.Log("PERF measure done");
        }

        /// A type by assembly-qualified name among the loaded assemblies (Mono's Type.GetType does
        /// not find a mod's, which KMod loads with LoadFrom).
        static System.Type FindType(string qualifiedName)
        {
            var parts = qualifiedName.Split(',');
            var name = parts[0].Trim();
            var assembly = parts.Length > 1 ? parts[1].Trim() : null;
            foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
                if (assembly == null || a.GetName().Name == assembly)
                    if (a.GetType(name) is { } found) return found;
            return null;
        }

        System.Collections.IEnumerator Select(string typeName)
        {
            for (int i = 0; i < 200; i++) yield return null;
            var type = FindType(typeName);
            if (type == null) { Debug.Log($"PERF select {typeName}: no such type"); yield break; }
            KSelectable target = null;
            foreach (var found in FindObjectsByType(type, FindObjectsSortMode.InstanceID))
                if (found is Component c && c.GetComponent<KSelectable>() is { } selectable && c.gameObject.GetMyWorldId() == ClusterManager.Instance.activeWorldId)
                {
                    target = selectable;
                    break;
                }
            if (target == null) { Debug.Log($"PERF select {typeName}: none on the active world"); yield break; }
            // Dialogs over the colony (the save's mod differences) closed first.
            foreach (var dialog in FindObjectsByType<KModalScreen>(FindObjectsSortMode.None)) dialog.Deactivate();
            for (int i = 0; i < 10; i++) yield return null;
            CameraController.Instance.SnapTo(target.transform.position, 8f);
            SelectTool.Instance.Select(target);
            Debug.Log($"PERF select {target.name} at {target.transform.position}");
            for (int i = 0; i < 90; i++) yield return null;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Path, "select.png"));
            int logged = 0;
            void Walk(Transform t, string path, int depth)
            {
                if (!t.gameObject.activeInHierarchy || logged > 300) return;
                if (t is RectTransform r && depth <= 6)
                {
                    logged++;
                    Debug.Log($"PERF select ui {path} {r.rect.width:F0}x{r.rect.height:F0} at {r.position.x:F0},{r.position.y:F0}");
                }
                foreach (Transform child in t) Walk(child, path + "/" + child.name, depth + 1);
            }
            if (DetailsScreen.Instance != null) Walk(DetailsScreen.Instance.transform, DetailsScreen.Instance.name, 0);
            for (int i = 0; i < 10; i++) yield return null;
            // A second line: a side screen type (assembly-qualified) and the path of a button in it,
            // pressed as a left click is (pointer down and up on it), then the screen captured
            // 60 frames later (select-pressed.png).
            var lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(Path, "select.txt"));
            if (lines.Length < 2 || lines[1].Trim().Length == 0) yield break;
            var parts = lines[1].Trim().Split('|');
            var screenType = FindType(parts[0]);
            Component screen = null;
            if (screenType != null)
                foreach (var found in FindObjectsByType(screenType, FindObjectsSortMode.None))
                    if (found is Component c && c.gameObject.activeInHierarchy) { screen = c; break; }
            var button = screen != null ? screen.transform.Find(parts[1]) : null;
            if (button == null) { Debug.Log($"PERF select press: no {parts[1]} in an active {parts[0]}"); yield break; }
            var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left, position = button.position };
            UnityEngine.EventSystems.ExecuteEvents.Execute(button.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(button.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
            Debug.Log($"PERF select pressed {button.name} under {screen.name}");
            for (int i = 0; i < 60; i++) yield return null;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Path, "select-pressed.png"));
            logged = 0;
            if (DetailsScreen.Instance != null) Walk(DetailsScreen.Instance.transform, DetailsScreen.Instance.name, 0);
            for (int i = 0; i < 10; i++) yield return null;
        }

        // Unity API the most subscribed mods call (DGSM, No Drop, Better Info Cards, Mod Manager).
        void MeasurePopularModApis()
        {
            var colors = new System.Text.StringBuilder();
            foreach (var text in new[] { "#FF0000", "#f00", "#F008", "#FF000080", "#12345", "#1234567", "FF0000", "#GG0000", " #FF0000", "#FF0000 ", "", "red", "Red", "RED", "cyan", "blue", "darkblue", "lightblue", "purple", "yellow", "lime", "fuchsia", "white", "silver", "grey", "gray", "black", "orange", "brown", "maroon", "green", "olive", "navy", "teal", "aqua", "magenta", "clear", "pink", "#ABCDEF12", "#abc", "#ABCD" })
            {
                bool ok = ColorUtility.TryParseHtmlString(text, out var c);
                colors.Append($" [{text}]={ok}:{(Color32)c}");
            }
            Debug.Log($"PERF measure htmlcolor:{colors}");
            try { Debug.Log($"PERF measure htmlcolor null: {ColorUtility.TryParseHtmlString(null, out var n)} {n}"); }
            catch (System.Exception e) { Debug.Log($"PERF measure htmlcolor null: {e.GetType().Name}"); }
            Debug.Log($"PERF measure allowThreadedTextureCreation {Texture.allowThreadedTextureCreation}");
            // Random.ColorHSV against the same seed's plain draws: which draws it takes, in what order.
            Random.InitState(1234);
            var hsv = Random.ColorHSV(0f, 1f, 0f, 1f, 0f, 1f);
            var after = Random.value;
            Random.InitState(1234);
            var draws = new float[8];
            for (int i = 0; i < draws.Length; i++) draws[i] = Random.value;
            Debug.Log($"PERF measure colorhsv: {hsv.r:R},{hsv.g:R},{hsv.b:R},{hsv.a:R} next {after:R} draws {string.Join(",", System.Array.ConvertAll(draws, d => d.ToString("R")))}");
            Random.InitState(1234);
            var hsv2 = Random.ColorHSV(0.2f, 0.4f, 0.5f, 0.6f, 0.7f, 0.9f);
            var hsva = Random.ColorHSV(0.2f, 0.4f, 0.5f, 0.6f, 0.7f, 0.9f, 0.1f, 0.3f);
            var plain = Random.ColorHSV();
            Debug.Log($"PERF measure colorhsv ranges: {hsv2.r:R},{hsv2.g:R},{hsv2.b:R},{hsv2.a:R} alpha {hsva.r:R},{hsva.g:R},{hsva.b:R},{hsva.a:R} plain {plain.r:R},{plain.g:R},{plain.b:R},{plain.a:R}");
            Random.InitState(1234);
            var w = new float[20];
            for (int i = 0; i < w.Length; i++) w[i] = Random.value;
            Debug.Log($"PERF measure colorhsv draws20 {string.Join(",", System.Array.ConvertAll(w, d => d.ToString("R")))}");
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.InstanceID))
                if (canvas.isActiveAndEnabled)
                    Debug.Log($"PERF measure canvas {canvas.name} root {canvas.isRootCanvas} mode {canvas.renderMode} pixelRect {canvas.pixelRect} scale {canvas.scaleFactor} screen {Screen.width}x{Screen.height}");
            var worldCanvas = new GameObject("PerfWorldCanvas").AddComponent<Canvas>();
            worldCanvas.renderMode = RenderMode.WorldSpace;
            Debug.Log($"PERF measure canvas world space: pixelRect {worldCanvas.pixelRect} camera {(worldCanvas.worldCamera == null ? "null" : worldCanvas.worldCamera.name)} main {(Camera.main == null ? "null" : Camera.main.pixelRect.ToString())}");
            var cameraCanvas = new GameObject("PerfCameraCanvas").AddComponent<Canvas>();
            cameraCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            Debug.Log($"PERF measure canvas camera mode without a camera: pixelRect {cameraCanvas.pixelRect}");
            Destroy(worldCanvas.gameObject);
            Destroy(cameraCanvas.gameObject);
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (from, to) => Debug.Log($"PERF measure scene activeSceneChanged f{Time.frameCount} [{from.name}|{from.IsValid()}|{from.buildIndex}] -> [{to.name}|{to.buildIndex}|{to.isLoaded}]");
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => Debug.Log($"PERF measure scene sceneLoaded f{Time.frameCount} {scene.name} {mode} active {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene => Debug.Log($"PERF measure scene sceneUnloaded f{Time.frameCount} [{scene.name}|{scene.IsValid()}]");
        }

        // uGUI's DefaultControls (the dropdown and scroll view mods build from code): each object
        // made, with what it carries, for the clean-room specification.
        void MeasureDefaultControls()
        {
            // Each resource a sprite named after its field, so the dump shows where each goes.
            Sprite Named(string name)
            {
                var sprite = Sprite.Create(new Texture2D(4, 4), new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
                sprite.name = name;
                return sprite;
            }
            var resources = new UnityEngine.UI.DefaultControls.Resources
            {
                standard = Named("standard"), background = Named("background"), inputField = Named("inputField"),
                knob = Named("knob"), checkmark = Named("checkmark"), dropdown = Named("dropdown"), mask = Named("mask"),
            };
            Debug.Log($"PERF measure controls factory {UnityEngine.UI.DefaultControls.factory?.GetType().FullName ?? "null"}");
            Dump(UnityEngine.UI.DefaultControls.CreatePanel(resources), "panel");
            Dump(UnityEngine.UI.DefaultControls.CreateButton(resources), "button");
            Dump(UnityEngine.UI.DefaultControls.CreateText(resources), "text");
            Dump(UnityEngine.UI.DefaultControls.CreateImage(resources), "image");
            Dump(UnityEngine.UI.DefaultControls.CreateRawImage(resources), "rawimage");
            Dump(UnityEngine.UI.DefaultControls.CreateSlider(resources), "slider");
            Dump(UnityEngine.UI.DefaultControls.CreateScrollbar(resources), "scrollbar");
            Dump(UnityEngine.UI.DefaultControls.CreateToggle(resources), "toggle");
            Dump(UnityEngine.UI.DefaultControls.CreateInputField(resources), "inputfield");
            Dump(UnityEngine.UI.DefaultControls.CreateDropdown(resources), "dropdown");
            Dump(UnityEngine.UI.DefaultControls.CreateScrollView(resources), "scrollview");
            var empty = new UnityEngine.UI.DefaultControls.Resources();
            Dump(UnityEngine.UI.DefaultControls.CreateDropdown(empty), "dropdown-empty");
            Dump(UnityEngine.UI.DefaultControls.CreateScrollView(empty), "scrollview-empty");
        }

        static string V2(Vector2 v) => $"({v.x:0.###},{v.y:0.###})";

        static void Dump(GameObject root, string what)
        {
            void Walk(Transform t, string path)
            {
                var line = new System.Text.StringBuilder($"PERF measure controls {what} {path} active={t.gameObject.activeSelf} layer={t.gameObject.layer}");
                foreach (var c in t.GetComponents<Component>())
                {
                    line.Append(" | ").Append(c.GetType().Name);
                    switch (c)
                    {
                        case RectTransform r:
                            line.Append($" amin{V2(r.anchorMin)} amax{V2(r.anchorMax)} pivot{V2(r.pivot)} size{V2(r.sizeDelta)} pos{V2(r.anchoredPosition)}"); break;
                        case UnityEngine.UI.Image i:
                            line.Append($" sprite={(i.sprite == null ? "null" : i.sprite.name)} type={i.type} color={(Color32)i.color} raycast={i.raycastTarget} fillCenter={i.fillCenter} preserve={i.preserveAspect} fill={i.fillMethod}/{i.fillAmount} ppu={i.pixelsPerUnitMultiplier} material={(i.material == null ? "null" : i.material.name)}"); break;
                        case UnityEngine.UI.RawImage ri:
                            line.Append($" texture={(ri.texture == null ? "null" : ri.texture.name)} color={(Color32)ri.color} uv={ri.uvRect} raycast={ri.raycastTarget}"); break;
                        case UnityEngine.UI.Text x:
                            line.Append($" text=[{x.text}] font={(x.font == null ? "null" : x.font.name)} size={x.fontSize} style={x.fontStyle} align={x.alignment} color={(Color32)x.color} rich={x.supportRichText} raycast={x.raycastTarget} hover={x.horizontalOverflow} vover={x.verticalOverflow} spacing={x.lineSpacing} best={x.resizeTextForBestFit} geomAlign={x.alignByGeometry}"); break;
                        case UnityEngine.UI.Slider sl:
                            line.Append($" dir={sl.direction} min={sl.minValue} max={sl.maxValue} value={sl.value} whole={sl.wholeNumbers} fill={sl.fillRect?.name} handle={sl.handleRect?.name} target={sl.targetGraphic?.name} transition={sl.transition} colors={(Color32)sl.colors.normalColor}/{(Color32)sl.colors.highlightedColor}/{(Color32)sl.colors.pressedColor}/{(Color32)sl.colors.selectedColor}/{(Color32)sl.colors.disabledColor} nav={sl.navigation.mode}"); break;
                        case UnityEngine.UI.InputField inf:
                            line.Append($" textComponent={inf.textComponent?.name} placeholder={inf.placeholder?.name} text=[{inf.text}] content={inf.contentType} lineType={inf.lineType} limit={inf.characterLimit} blink={inf.caretBlinkRate} caretWidth={inf.caretWidth} selection={(Color32)inf.selectionColor} readOnly={inf.readOnly} target={inf.targetGraphic?.name} transition={inf.transition} colors={(Color32)inf.colors.normalColor}/{(Color32)inf.colors.highlightedColor}/{(Color32)inf.colors.pressedColor}/{(Color32)inf.colors.selectedColor}/{(Color32)inf.colors.disabledColor}"); break;
                        case UnityEngine.UI.Dropdown d:
                            line.Append($" template={d.template?.name} caption={d.captionText?.name} captionImage={d.captionImage?.name} item={d.itemText?.name} itemImage={d.itemImage?.name} options=[{string.Join(",", d.options.ConvertAll(o => o.text))}] value={d.value} target={d.targetGraphic?.name} transition={d.transition} colors={(Color32)d.colors.normalColor}/{(Color32)d.colors.highlightedColor}/{(Color32)d.colors.pressedColor}/{(Color32)d.colors.selectedColor}/{(Color32)d.colors.disabledColor} nav={d.navigation.mode} fade={d.alphaFadeSpeed}"); break;
                        case UnityEngine.UI.ScrollRect sr:
                            line.Append($" content={sr.content?.name} viewport={sr.viewport?.name} h={sr.horizontal} v={sr.vertical} hbar={sr.horizontalScrollbar?.name} vbar={sr.verticalScrollbar?.name} hvis={sr.horizontalScrollbarVisibility} vvis={sr.verticalScrollbarVisibility} hspace={sr.horizontalScrollbarSpacing} vspace={sr.verticalScrollbarSpacing} move={sr.movementType} elastic={sr.elasticity} inertia={sr.inertia} decel={sr.decelerationRate} sens={sr.scrollSensitivity}"); break;
                        case UnityEngine.UI.Scrollbar sb:
                            line.Append($" dir={sb.direction} handle={sb.handleRect?.name} size={sb.size} value={sb.value} steps={sb.numberOfSteps} target={sb.targetGraphic?.name} transition={sb.transition} colors={(Color32)sb.colors.normalColor}/{(Color32)sb.colors.highlightedColor}/{(Color32)sb.colors.pressedColor}/{(Color32)sb.colors.selectedColor}/{(Color32)sb.colors.disabledColor} nav={sb.navigation.mode}"); break;
                        case UnityEngine.UI.Toggle tg:
                            line.Append($" isOn={tg.isOn} graphic={tg.graphic?.name} target={tg.targetGraphic?.name} group={(tg.group == null ? "null" : tg.group.name)} transition={tg.transition} toggleTransition={tg.toggleTransition} colors={(Color32)tg.colors.normalColor}/{(Color32)tg.colors.highlightedColor}/{(Color32)tg.colors.pressedColor}/{(Color32)tg.colors.selectedColor}/{(Color32)tg.colors.disabledColor} nav={tg.navigation.mode}"); break;
                        case UnityEngine.UI.Mask m:
                            line.Append($" show={m.showMaskGraphic}"); break;
                        case UnityEngine.UI.Selectable sel:
                            line.Append($" target={sel.targetGraphic?.name} transition={sel.transition} colors={(Color32)sel.colors.normalColor}/{(Color32)sel.colors.highlightedColor}/{(Color32)sel.colors.pressedColor}/{(Color32)sel.colors.selectedColor}/{(Color32)sel.colors.disabledColor} fade={sel.colors.fadeDuration} mult={sel.colors.colorMultiplier} nav={sel.navigation.mode}"); break;
                        case CanvasRenderer cr:
                            line.Append($" cull={cr.cullTransparentMesh}"); break;
                    }
                }
                Debug.Log(line.ToString());
                foreach (Transform child in t) Walk(child, path + "/" + child.name);
            }
            Walk(root.transform, root.name);
            Destroy(root);
        }

        // Transforms with a zero scale component (Rocketry Expanded scales a UI image by (1, 1),
        // z 0): inverse transforms, the inverse matrix, and UI hit tests.
        void MeasureZeroScale()
        {
            var a = new GameObject("PerfZeroA").transform;
            a.position = new Vector3(100, 50, 0);
            a.localScale = new Vector3(1, 1, 0);
            Debug.Log($"PERF measure zeroscale point {a.InverseTransformPoint(new Vector3(150, 60, 7)):F3} vector {a.InverseTransformVector(new Vector3(3, 4, 5)):F3} direction {a.InverseTransformDirection(new Vector3(3, 4, 5)):F3} matrix {a.worldToLocalMatrix.ToString("F3").Replace("\n", " ")}");
            a.localScale = new Vector3(2, 0, 3);
            Debug.Log($"PERF measure zeroscale y0 point {a.InverseTransformPoint(new Vector3(150, 60, 7)):F3}");
            a.localScale = Vector3.zero;
            Debug.Log($"PERF measure zeroscale all point {a.InverseTransformPoint(new Vector3(150, 60, 7)):F3}");
            var parent = new GameObject("PerfZeroParent").transform;
            parent.localScale = new Vector3(2, 2, 0);
            var child = new GameObject("PerfZeroChild").transform;
            child.SetParent(parent, false);
            child.localPosition = new Vector3(10, 20, 30);
            child.localScale = new Vector3(1, 1, 1);
            Debug.Log($"PERF measure zeroscale nested point {child.InverseTransformPoint(new Vector3(40, 60, 9)):F3} position {child.position:F3}");
            Destroy(a.gameObject); Destroy(child.gameObject); Destroy(parent.gameObject);
            var canvas = new GameObject("PerfZeroCanvas").AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var img = new GameObject("PerfZeroImage", typeof(RectTransform)).GetComponent<RectTransform>();
            img.SetParent(canvas.transform, false);
            img.sizeDelta = new Vector2(24, 24);
            img.anchoredPosition = Vector2.zero;
            img.localScale = new Vector3(1, 1, 0);
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            Debug.Log($"PERF measure zeroscale ui: centre {RectTransformUtility.RectangleContainsScreenPoint(img, centre)} inside {RectTransformUtility.RectangleContainsScreenPoint(img, centre + new Vector2(10, 10))} outside {RectTransformUtility.RectangleContainsScreenPoint(img, new Vector2(10, 10))} local {(RectTransformUtility.ScreenPointToLocalPointInRectangle(img, new Vector2(10, 10), null, out var local) ? local.ToString("F2") : "false")}");
            Destroy(canvas.gameObject);
        }

        // A menu popping in from scale 0 (the game's OptionsMenu Content): which of its buttons the
        // EventSystem hits while the parent's scale is 0, half, and 1.
        System.Collections.IEnumerator MeasureZeroScaledMenu()
        {
            var canvas = new GameObject("PerfZeroMenuCanvas").AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            canvas.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(canvas.transform, false);
            content.sizeDelta = new Vector2(400, 300);
            UnityEngine.UI.Image Button(string name, Vector2 position)
            {
                var b = new GameObject(name, typeof(RectTransform)).AddComponent<UnityEngine.UI.Image>();
                b.rectTransform.SetParent(content, false);
                b.rectTransform.sizeDelta = new Vector2(200, 30);
                b.rectTransform.anchoredPosition = position;
                return b;
            }
            Button("Centre", Vector2.zero);
            Button("Below", new Vector2(0, -50));
            Button("Above", new Vector2(0, 50));
            var events = UnityEngine.EventSystems.EventSystem.current;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            string Hit(Vector2 point)
            {
                if (events == null) return "no event system";
                var results = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                events.RaycastAll(new UnityEngine.EventSystems.PointerEventData(events) { position = point }, results);
                return string.Join(",", results.FindAll(r => r.gameObject.transform.IsChildOf(canvas.transform)).ConvertAll(r => r.gameObject.name));
            }
            foreach (var scale in new[] { Vector3.one, Vector3.zero, Vector3.one * 0.5f, new Vector3(0, 1, 1), new Vector3(1, 0, 1), Vector3.one })
            {
                content.localScale = scale;
                yield return null;
                yield return null;
                var line = new System.Text.StringBuilder($"PERF measure zeroscale menu scale {scale}:");
                foreach (var (label, offset) in new[] { ("centre", Vector2.zero), ("below50", new Vector2(0, -50)), ("above50", new Vector2(0, 50)), ("right90", new Vector2(90, 0)), ("far", new Vector2(-400, 250)) })
                    line.Append($" {label}=[{Hit(centre + offset)}]");
                line.Append($" contains(below,centre)={RectTransformUtility.RectangleContainsScreenPoint((RectTransform)content.Find("Below"), centre)} contains(below,above50)={RectTransformUtility.RectangleContainsScreenPoint((RectTransform)content.Find("Below"), centre + new Vector2(0, 50))}");
                Debug.Log(line.ToString());
            }
            // Raycast padding (positive shrinks the hit area) and the offset of RectangleContainsScreenPoint.
            var padded = Button("Padded", new Vector2(0, -120));
            padded.raycastPadding = new Vector4(10, 5, 10, 5);
            yield return null;
            yield return null;
            var below120 = centre + new Vector2(0, -120);
            var paddedRect = padded.rectTransform;
            Debug.Log($"PERF measure zeroscale padding: in95=[{Hit(below120 + new Vector2(95, 0))}] in85=[{Hit(below120 + new Vector2(85, 0))}] up13=[{Hit(below120 + new Vector2(0, 13))}] up8=[{Hit(below120 + new Vector2(0, 8))}]"
                + $" contains(105,+10)={RectTransformUtility.RectangleContainsScreenPoint(paddedRect, below120 + new Vector2(105, 0), null, new Vector4(10, 10, 10, 10))}"
                + $" contains(95,+10)={RectTransformUtility.RectangleContainsScreenPoint(paddedRect, below120 + new Vector2(95, 0), null, new Vector4(10, 10, 10, 10))}"
                + $" contains(95,-10)={RectTransformUtility.RectangleContainsScreenPoint(paddedRect, below120 + new Vector2(95, 0), null, new Vector4(-10, -10, -10, -10))}"
                + $" contains(105,-10)={RectTransformUtility.RectangleContainsScreenPoint(paddedRect, below120 + new Vector2(105, 0), null, new Vector4(-10, -10, -10, -10))}"
                + $" contains(left105,right+10)={RectTransformUtility.RectangleContainsScreenPoint(paddedRect, below120 + new Vector2(-105, 0), null, new Vector4(0, 0, 10, 0))}"
                + $" contains(right105,right+10)={RectTransformUtility.RectangleContainsScreenPoint(paddedRect, below120 + new Vector2(105, 0), null, new Vector4(0, 0, 10, 0))}");
            Destroy(canvas.gameObject);
        }

        // The Unity API of the third hundred Workshop mods: MinMaxCurve, the main and emission
        // modules' setters, the noise module, Texture2D.blackTexture, Screen.safeArea and
        // Mesh.RecalculateNormals.
        /// A Harmony-patched method's frame as a stack trace reports it (Customize Plants walks the
        /// stack for the class whose CreatePrefab created a plant).
        void MeasurePatchedFrames()
        {
            var harmony = new HarmonyLib.Harmony("PerfProbe.Frames");
            harmony.Patch(typeof(Runner).GetMethod(nameof(PatchedVictim), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Runner).GetMethod(nameof(NoOpPrefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            var frames = PatchedVictim();
            for (int i = 0; i < 3 && i < frames.Length; i++)
            {
                var m = frames[i].GetMethod();
                Debug.Log($"PERF measure frame {i}: {(m == null ? "null" : $"{m.GetType().FullName} name {m.Name} declaring {m.DeclaringType?.FullName ?? "null"} reflected {m.ReflectedType?.FullName ?? "null"} module {m.Module?.Name ?? "null"} static {m.IsStatic} params {m.GetParameters().Length}")}");
            }
            harmony.UnpatchAll("PerfProbe.Frames");
        }

        static void NoOpPrefix() { }

        static bool awake;

        System.Collections.IEnumerator PointerLog()
        {
            for (int i = 0; i < 160; i++)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                Debug.Log($"PERF pointerlog {System.DateTime.Now:HH:mm:ss.f} mousePosition {Input.mousePosition} screen {Screen.width}x{Screen.height}");
            }
        }

        /// `focuslog.txt`: from the colony on, every 2 s of real time the frames rendered in it and
        /// Application.isFocused, to compare focused and unfocused stretches within one run.
        System.Collections.IEnumerator FocusLog()
        {
            while (Game.Instance == null) yield return null;
            int start = Time.frameCount;
            float t0 = Time.realtimeSinceStartup;
            while (true)
            {
                yield return null;
                float now = Time.realtimeSinceStartup;
                if (now - t0 < 2f) continue;
                Debug.Log($"PERF focuslog {System.DateTime.Now:HH:mm:ss.f} frames {Time.frameCount - start} in {now - t0:F3} s focused {Application.isFocused}");
                start = Time.frameCount;
                t0 = now;
            }
        }

        struct CursorPoint { public int X, Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetCursorPos(out CursorPoint point);

        /// Where Windows puts the cursor, in screen pixels (with awake.txt, to compare with what
        /// Input.mousePosition reports when the cursor is outside the window).
        static string OsCursor()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return "";
            return GetCursorPos(out var p) ? $" os cursor ({p.X}, {p.Y})" : "";
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static System.Diagnostics.StackFrame[] PatchedVictim() => CaptureFrames();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static System.Diagnostics.StackFrame[] CaptureFrames() => new System.Diagnostics.StackTrace().GetFrames();

        void MeasureThirdHundredApis()
        {
            string Curve(ParticleSystem.MinMaxCurve c) => $"mode {c.mode} constant {c.constant} min {c.constantMin} max {c.constantMax} mult {c.curveMultiplier} curve {(c.curve == null ? "null" : c.curve.length.ToString())} curveMin {(c.curveMin == null ? "null" : c.curveMin.length.ToString())} curveMax {(c.curveMax == null ? "null" : c.curveMax.length.ToString())} eval {c.Evaluate(0.5f)}/{c.Evaluate(0.5f, 0.25f)}";
            Debug.Log($"PERF measure minmaxcurve 3: {Curve(new ParticleSystem.MinMaxCurve(3f))}");
            Debug.Log($"PERF measure minmaxcurve 1,2: {Curve(new ParticleSystem.MinMaxCurve(1f, 2f))}");
            var ramp = AnimationCurve.Linear(0, 0, 1, 4);
            Debug.Log($"PERF measure minmaxcurve 2*ramp: {Curve(new ParticleSystem.MinMaxCurve(2f, ramp))}");
            Debug.Log($"PERF measure minmaxcurve 1*(ramp,const1): {Curve(new ParticleSystem.MinMaxCurve(1f, ramp, AnimationCurve.Constant(0, 1, 1)))}");
            ParticleSystem.MinMaxCurve implicitCurve = 7f;
            Debug.Log($"PERF measure minmaxcurve implicit 7: {Curve(implicitCurve)} default: {Curve(default)}");
            var mc = new ParticleSystem.MinMaxCurve(5f); mc.constantMin = 1f;
            Debug.Log($"PERF measure minmaxcurve set min on constant: {Curve(mc)}");
            var go = new GameObject("PerfApiParticles");
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main; var emission = ps.emission; var noise = ps.noise;
            Debug.Log($"PERF measure particle defaults: lifetime [{Curve(main.startLifetime)}] rate [{Curve(emission.rateOverTime)}] speed {main.simulationSpeed} prewarm {main.prewarm} max {main.maxParticles} noise enabled {noise.enabled} strength [{Curve(noise.strength)}] mult {noise.strengthMultiplier}");
            main.startLifetime = 2.5f; emission.rateOverTime = 40f; main.simulationSpeed = 0.5f; main.prewarm = true; main.maxParticles = -3; noise.strength = 0.75f;
            Debug.Log($"PERF measure particle set: lifetime [{Curve(main.startLifetime)}] rate [{Curve(emission.rateOverTime)}] speed {main.simulationSpeed} prewarm {main.prewarm} max {main.maxParticles} noise enabled {noise.enabled} strength [{Curve(noise.strength)}] mult {noise.strengthMultiplier}");
            main.maxParticles = 250;
            Debug.Log($"PERF measure particle max 250: {main.maxParticles}");
            Destroy(go);
            var black = Texture2D.blackTexture;
            Debug.Log($"PERF measure blackTexture: {black.name} {black.width}x{black.height} {black.format} mips {black.mipmapCount} readable {black.isReadable} pixel {(black.isReadable ? ((Color32)black.GetPixel(0, 0)).ToString() : "-")} same {ReferenceEquals(black, Texture2D.blackTexture)} filter {black.filterMode} wrap {black.wrapMode}");
            Debug.Log($"PERF measure safeArea: {Screen.safeArea} screen {Screen.width}x{Screen.height}");
            var mesh = new Mesh();
            // Two triangles sharing vertex 0, of different areas and angles at it, and a lone one.
            mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 3), new Vector3(5, 5, 5), new Vector3(6, 5, 5), new Vector3(5, 6, 5) };
            mesh.triangles = new[] { 0, 1, 2, 0, 3, 1, 4, 6, 5 };
            mesh.RecalculateNormals();
            Debug.Log($"PERF measure normals: {string.Join(" ", System.Array.ConvertAll(mesh.normals, n => n.ToString("F5")))}");
            var dup = new Mesh();
            dup.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(0, 0, 3), new Vector3(1, 0, 0) };
            dup.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            dup.RecalculateNormals();
            Debug.Log($"PERF measure normals split: {string.Join(" ", System.Array.ConvertAll(dup.normals, n => n.ToString("F5")))}");
            var degenerate = new Mesh();
            degenerate.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(9, 9, 9) };
            degenerate.triangles = new[] { 0, 1, 2 };
            degenerate.RecalculateNormals();
            Debug.Log($"PERF measure normals degenerate: {string.Join(" ", System.Array.ConvertAll(degenerate.normals, n => n.ToString("F5")))}");
            Destroy(mesh); Destroy(dup); Destroy(degenerate);
        }

        // An input field built in code with its text component assigned after it was enabled (as
        // ONI Together builds its chat field): whether the caret exists after a canvas update, a
        // frame, and a disable and enable, and whether anything is logged.
        /// TMPro_ExtensionMethods' colour helpers (Paint Your Pipes multiplies pipe colours with
        /// Multiply): every pair of a set of channel values, and the alpha rules.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void MeasureTmpColors()
        {
            var values = new byte[] { 0, 1, 2, 3, 15, 16, 64, 100, 127, 128, 129, 191, 200, 254, 255 };
            var multiply = new System.Text.StringBuilder();
            var tint = new System.Text.StringBuilder();
            foreach (var a in values)
                foreach (var b in values)
                {
                    var m = TMPro.TMPro_ExtensionMethods.Multiply(new Color32(a, b, a, b), new Color32(b, a, a, 255));
                    multiply.Append($" {a}x{b}={m.r},{m.g},{m.b},{m.a}");
                    var t = TMPro.TMPro_ExtensionMethods.Tint(new Color32(a, b, a, b), new Color32(b, a, a, 255));
                    tint.Append($" {a}x{b}={t.r},{t.g},{t.b},{t.a}");
                }
            Debug.Log("PERF measure tmp multiply:" + multiply);
            Debug.Log("PERF measure tmp tint32:" + tint);
            var c1 = new Color(0.25f, 0.5f, 0.75f, 0.8f); var c2 = new Color(0.9f, 0.1f, 0.3f, 0.4f);
            Debug.Log($"PERF measure tmp minalpha: {TMPro.TMPro_ExtensionMethods.MinAlpha(c1, c2)} {TMPro.TMPro_ExtensionMethods.MinAlpha(c2, c1)}");
            var k1 = new Color32(10, 20, 30, 40); var k2 = new Color32(10, 20, 30, 41); var k3 = new Color32(10, 21, 30, 40);
            Debug.Log($"PERF measure tmp compare32: same {TMPro.TMPro_ExtensionMethods.Compare(k1, k1)} alpha {TMPro.TMPro_ExtensionMethods.Compare(k1, k2)} green {TMPro.TMPro_ExtensionMethods.Compare(k1, k3)} rgb-alpha {TMPro.TMPro_ExtensionMethods.CompareRGB(k1, k2)} rgb-green {TMPro.TMPro_ExtensionMethods.CompareRGB(k1, k3)}");
            var f1 = new Color(0.1f, 0.2f, 0.3f, 0.4f); var f2 = new Color(0.1f, 0.2f, 0.3f, 0.5f); var f3 = new Color(0.1f, 0.2000001f, 0.3f, 0.4f);
            Debug.Log($"PERF measure tmp compare: same {TMPro.TMPro_ExtensionMethods.Compare(f1, f1)} alpha {TMPro.TMPro_ExtensionMethods.Compare(f1, f2)} tiny {TMPro.TMPro_ExtensionMethods.Compare(f1, f3)} rgb-alpha {TMPro.TMPro_ExtensionMethods.CompareRGB(f1, f2)} rgb-tiny {TMPro.TMPro_ExtensionMethods.CompareRGB(f1, f3)}");
        }

        /// Texture2D.PackTextures (LocalCanvas packs players' images into one atlas): the atlas's
        /// size and format and each texture's rect, for several sets, and whether each texture's
        /// texels land in its rect.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void MeasurePackTextures()
        {
            Texture2D Solid(int w, int h, int i, TextureFormat format = TextureFormat.RGBA32)
            {
                var t = new Texture2D(w, h, format, false);
                var c = new Color32((byte)(40 + i * 23), (byte)(200 - i * 17), (byte)(i * 51 % 256), 255);
                var px = new Color32[w * h];
                for (int k = 0; k < px.Length; k++) px[k] = c;
                t.SetPixels32(px);
                t.Apply();
                return t;
            }
            void Case(string name, int[][] sizes, int padding, int maxSize, TextureFormat format = TextureFormat.RGBA32)
            {
                var textures = new Texture2D[sizes.Length];
                for (int i = 0; i < sizes.Length; i++) textures[i] = Solid(sizes[i][0], sizes[i][1], i, format);
                var atlas = new Texture2D(2, 2);
                var rects = atlas.PackTextures(textures, padding, maxSize, false);
                var line = new System.Text.StringBuilder($"PERF measure pack {name}: atlas {atlas.width}x{atlas.height} {atlas.format} mips {atlas.mipmapCount} readable {atlas.isReadable} rects");
                for (int i = 0; i < rects.Length; i++)
                {
                    var r = rects[i];
                    int cx = (int)((r.x + r.width / 2) * atlas.width), cy = (int)((r.y + r.height / 2) * atlas.height);
                    var want = textures[i].GetPixel(0, 0);
                    var got = atlas.isReadable ? atlas.GetPixel(cx, cy) : Color.clear;
                    bool match = Mathf.Abs(want.r - got.r) < 0.02f && Mathf.Abs(want.g - got.g) < 0.02f && Mathf.Abs(want.b - got.b) < 0.02f;
                    line.Append($" [{r.x * atlas.width:F1},{r.y * atlas.height:F1} {r.width * atlas.width:F1}x{r.height * atlas.height:F1}{(match ? "" : " MISMATCH")}]");
                }
                if (atlas.isReadable) line.Append($" corner {atlas.GetPixel(atlas.width - 1, atlas.height - 1)}");
                Debug.Log(line.ToString());
            }
            Case("four", new[] { new[] { 64, 64 }, new[] { 32, 32 }, new[] { 128, 16 }, new[] { 16, 128 } }, 2, 2048);
            Case("four-nopad", new[] { new[] { 64, 64 }, new[] { 32, 32 }, new[] { 128, 16 }, new[] { 16, 128 } }, 0, 2048);
            Case("one", new[] { new[] { 100, 60 } }, 2, 2048);
            Case("odd", new[] { new[] { 100, 60 }, new[] { 37, 81 }, new[] { 250, 13 }, new[] { 9, 9 }, new[] { 64, 200 } }, 4, 2048);
            var many = new int[24][];
            for (int i = 0; i < many.Length; i++) many[i] = new[] { 16 + i * 11 % 97, 16 + i * 29 % 113 };
            Case("many", many, 2, 2048);
            Case("over-max", new[] { new[] { 512, 512 }, new[] { 512, 512 }, new[] { 512, 256 } }, 2, 512);
            Case("same", new[] { new[] { 64, 64 }, new[] { 64, 64 }, new[] { 64, 64 }, new[] { 64, 64 }, new[] { 64, 64 } }, 1, 2048);
            Case("rgb24", new[] { new[] { 64, 64 }, new[] { 32, 32 } }, 2, 2048, TextureFormat.RGB24);
            Case("scale-odd", new[] { new[] { 300, 300 }, new[] { 300, 300 }, new[] { 300, 150 } }, 2, 256);
            // A horizontal ramp packed over the maximum size: how scaled texels are resampled.
            var ramp = new Texture2D(300, 4, TextureFormat.RGBA32, false);
            var rampPx = new Color32[300 * 4];
            for (int y = 0; y < 4; y++) for (int x = 0; x < 300; x++) rampPx[y * 300 + x] = new Color32((byte)(x * 255 / 299), (byte)(x % 2 == 0 ? 255 : 0), 0, 255);
            ramp.SetPixels32(rampPx);
            ramp.Apply();
            var rampAtlas = new Texture2D(2, 2);
            var rampRect = rampAtlas.PackTextures(new[] { ramp, ramp, ramp }, 0, 256, false)[0];
            var rampRow = new System.Text.StringBuilder();
            int rx0 = (int)(rampRect.x * rampAtlas.width), ry0 = (int)(rampRect.y * rampAtlas.height), rw = (int)Mathf.Round(rampRect.width * rampAtlas.width);
            for (int x = 0; x < Mathf.Min(rw, 24); x++) { var c = rampAtlas.GetPixel(rx0 + x, ry0); rampRow.Append($" {(int)Mathf.Round(c.r * 255)}/{(int)Mathf.Round(c.g * 255)}"); }
            Debug.Log($"PERF measure pack ramp: atlas {rampAtlas.width}x{rampAtlas.height} rect {rampRect.x * rampAtlas.width},{rampRect.y * rampAtlas.height} {rampRect.width * rampAtlas.width}x{rampRect.height * rampAtlas.height} row{rampRow}");
            var atlas2 = new Texture2D(2, 2);
            var r2 = atlas2.PackTextures(new[] { Solid(64, 64, 0), Solid(32, 32, 1) }, 2);
            Debug.Log($"PERF measure pack default-max: atlas {atlas2.width}x{atlas2.height} mips {atlas2.mipmapCount} rects {r2.Length}");
            var atlas3 = new Texture2D(2, 2);
            var r3 = atlas3.PackTextures(new[] { Solid(64, 64, 0), Solid(32, 32, 1) }, 2, 2048, true);
            Debug.Log($"PERF measure pack no-longer-readable: readable {atlas3.isReadable} rects {r3.Length}");
        }

        System.Collections.IEnumerator LocalCanvasPainting()
        {
            var stages = Db.GetArtableStages();
            var local = new System.Collections.Generic.List<Database.ArtableStage>();
            foreach (var stage in stages.resources) if (stage.id.StartsWith("LocalCanvas_")) local.Add(stage);
            var paintings = FindObjectsByType<Painting>(FindObjectsSortMode.InstanceID);
            if (paintings.Length == 0)
            {
                // None in the save: one built where the view is, as sandbox's instant build does.
                var def = Assets.GetBuildingDef("Canvas");
                int cell = Grid.PosToCell(CameraController.Instance.transform.position);
                var built = def.Build(cell, Orientation.Neutral, null, def.DefaultElements(), 293f, false, GameClock.Instance.GetTime());
                for (int i = 0; i < 30; i++) yield return null;
                paintings = new[] { built.GetComponent<Painting>() };
                Debug.Log($"PERF localcanvas: built a Canvas at cell {cell}");
            }
            Debug.Log($"PERF localcanvas: {local.Count} local stages ({string.Join(", ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Select(local, st => st.id + " for " + st.prefabId), 4))}), {paintings.Length} paintings");
            foreach (var painting in paintings)
            {
                var prefab = painting.GetComponent<KPrefabID>().PrefabTag.Name;
                var stage = local.Find(st => st.prefabId == prefab && st.id.EndsWith("_LookingGreat"));
                if (stage == null) continue;
                painting.SetStage(stage.id, false);
                CameraController.Instance.SnapTo(painting.transform.position, 6f);
                for (int i = 0; i < 60; i++) yield return null;
                Debug.Log($"PERF localcanvas: {prefab} at {painting.transform.position} set to {stage.id}, current {painting.CurrentStage}");
                // The world alone: screen-space UI (an achievement alert covers the view) hidden.
                foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.InstanceID))
                    if (canvas.isRootCanvas && canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
                for (int i = 0; i < 5; i++) yield return null;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Path, "localcanvas.png"));
                for (int i = 0; i < 10; i++) yield return null;
                yield break;
            }
            Debug.Log("PERF localcanvas: no painting with a local stage");
        }

        static readonly System.Collections.Generic.SortedDictionary<string, int> placeChecks = new System.Collections.Generic.SortedDictionary<string, int>();

        static void PlaceChecked(BuildingDef __instance, bool __result, string fail_reason)
        {
            string key = $"{__instance.PrefabID} {(__result ? "ok" : fail_reason)}";
            placeChecks[key] = placeChecks.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        System.Collections.IEnumerator BlueprintRoundTrip()
        {
            // Long enough after the load for both players' world inventories to have counted every
            // tag once (the build menu's material checks read them).
            while (!ClusterManager.Instance.activeWorld.worldInventory.HasValidCount) yield return null;
            for (int i = 0; i < 60; i++) yield return null;
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            var state = FindType("BlueprintsV2.BlueprintData.BlueprintState");
            var assets = FindType("BlueprintsV2.ModAssets");
            var files = assets?.GetNestedType("BlueprintFileHandling", flags);
            if (state == null || files == null) { Debug.Log("PERF blueprints: mod types not found"); yield break; }
            int cx, cy;
            Grid.PosToXY(CameraController.Instance.transform.position, out cx, out cy);
            var topLeft = new Vector2I(cx - 10, cy + 6);
            var bottomRight = new Vector2I(cx + 10, cy - 6);
            var bp = state.GetMethod("CreateBlueprint", flags).Invoke(null, new object[] { topLeft, bottomRight, null, false });
            var bpType = bp.GetType();
            string Counts(object b) => $"{((System.Collections.ICollection)bpType.GetProperty("BuildingConfigurations").GetValue(b)).Count} buildings, {((System.Collections.ICollection)bpType.GetProperty("DigLocations").GetValue(b)).Count} digs";
            Debug.Log($"PERF blueprints: created {Counts(bp)}");
            bpType.GetMethod("Rename", flags).Invoke(bp, new object[] { "perfprobe", true });
            var file = (string)bpType.GetProperty("FilePath").GetValue(bp);
            Debug.Log($"PERF blueprints: written {(System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : -1)} bytes as {System.IO.Path.GetFileName(file)}");
            var load = new object[] { file, null };
            bool loaded = (bool)files.GetMethod("LoadBlueprint", flags).Invoke(null, load);
            var read = load[1];
            Debug.Log($"PERF blueprints: read back {loaded}: {Counts(read)}");
            var techItems = Db.Get().TechItems;
            Debug.Log("PERF blueprints: tech items complete:" + string.Concat(System.Linq.Enumerable.Select(new[] { "Tile", "GasConduit", "InsulatedGasConduit", "LiquidConduitRadiant", "WireRefinedHighWattage", "GasConduitBridge", "InsulationTile", "ExteriorWall" },
                id => $" {id} {techItems.IsTechItemComplete(id)}")));
            var tech = techItems.TryGet("InsulatedGasConduit")?.ParentTech;
            if (tech != null) Debug.Log($"PERF blueprints: InsulatedGasConduit's tech {tech.Id}: complete {tech.IsComplete()} research {Research.Instance.Get(tech)?.IsComplete()}");
            var inv = ClusterManager.Instance.activeWorld.worldInventory;
            var invT = HarmonyLib.Traverse.Create(inv);
            var tags = new System.Collections.Generic.List<string>();
            foreach (var key in ((System.Collections.IDictionary)invT.Field("Inventory").GetValue()).Keys) tags.Add(key.ToString());
            Debug.Log($"PERF blueprints: inventory f{Time.frameCount}: valid {inv.HasValidCount} index {invT.Field("accessibleUpdateIndex").GetValue()} of {tags.Count}; BuildableRaw at {tags.IndexOf("BuildableRaw")}, RefinedMetal at {tags.IndexOf("RefinedMetal")}; first {string.Join(",", System.Linq.Enumerable.Take(tags, 8))}");
            var stateFor = typeof(PlanScreen).GetMethod("GetBuildableStateForDef", flags);
            foreach (var id in new[] { "GasConduit", "InsulatedGasConduit", "LiquidConduitRadiant", "WireRefinedHighWattage", "InsulationTile", "ExteriorWall" })
            {
                var def = Assets.GetBuildingDef(id);
                Debug.Log($"PERF blueprints: {id}: plan state {stateFor.Invoke(PlanScreen.Instance, new object[] { def })} materials met {ProductInfoScreen.MaterialsMet(def.CraftRecipe)} available {def.IsAvailable()} sandbox {Game.Instance.SandboxModeActive} instant {DebugHandler.InstantBuildMode}"
                    + string.Concat(System.Linq.Enumerable.Select(def.CraftRecipe.Ingredients, ing => $" [{ing.tag} needs {ing.amount} has {ClusterManager.Instance.activeWorld.worldInventory.GetAmount(ing.tag, true)}]")));
            }
            int before = FindObjectsByType<Constructable>(FindObjectsSortMode.None).Length;
            assets.GetField("SelectedBlueprint", flags).SetValue(null, read);
            var origin = new Vector2I(cx + 20, cy + 6);
            state.GetMethod("VisualizeBlueprint", flags, null, new[] { typeof(Vector2I), bpType }, null).Invoke(null, new object[] { origin, read });
            for (int i = 0; i < 5; i++) yield return null;
            // Every placement check the game answers while the blueprint is placed, by building
            // and reason (the most specific overload, which the others call).
            var placeHarmony = new HarmonyLib.Harmony("PerfProbe.Place");
            var valid = typeof(BuildingDef).GetMethod("IsValidPlaceLocation", new[] { typeof(GameObject), typeof(int), typeof(Orientation), typeof(bool), typeof(string).MakeByRefType(), typeof(bool) });
            placeChecks.Clear();
            placeHarmony.Patch(valid, postfix: new HarmonyLib.HarmonyMethod(typeof(Runner).GetMethod(nameof(PlaceChecked), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            state.GetMethod("UseBlueprint", flags).Invoke(null, new object[] { 0UL, origin, null });
            placeHarmony.UnpatchAll("PerfProbe.Place");
            Debug.Log("PERF blueprints: placement checks: " + string.Join("; ", System.Linq.Enumerable.Select(placeChecks, kv => $"{kv.Key} {kv.Value}")));
            for (int i = 0; i < 60; i++) yield return null;
            int after = FindObjectsByType<Constructable>(FindObjectsSortMode.None).Length;
            Debug.Log($"PERF blueprints: placed at {origin.x},{origin.y}: construction sites {before} -> {after}");
            for (int i = 0; i < 300; i++) yield return null;
            var byPrefab = new System.Collections.Generic.SortedDictionary<string, int>();
            foreach (var c in FindObjectsByType<Constructable>(FindObjectsSortMode.None))
            {
                var name = c.GetComponent<KPrefabID>().PrefabTag.Name;
                byPrefab[name] = byPrefab.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            Debug.Log($"PERF blueprints: 300 frames later {System.Linq.Enumerable.Sum(byPrefab.Values)} sites: {string.Join(", ", System.Linq.Enumerable.Select(byPrefab, kv => kv.Key + " " + kv.Value))}");
            CameraController.Instance.SnapTo(Grid.CellToPosCCC(Grid.XYToCell(origin.x + 10, origin.y - 6), Grid.SceneLayer.Building), 10f);
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.InstanceID))
                if (canvas.isRootCanvas && canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
            for (int i = 0; i < 30; i++) yield return null;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Path, "blueprints.png"));
        }

        /// Shader.GetPropertyCount/Name/Type (Morphs Not Included looks for a material's texture
        /// properties with them): five of the game's shaders, and an index out of range.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void MeasureShaderProperties()
        {
            var names = new[] { "Sprites/Default", "UI/Default", "Klei/BatchedAnimation", "Klei/Substance", "TextMeshPro/Distance Field" };
            foreach (var name in names)
            {
                var shader = Shader.Find(name);
                if (shader == null) { Debug.Log($"PERF measure shader props {name}: not found"); continue; }
                var line = new System.Text.StringBuilder($"PERF measure shader props {name}: {shader.GetPropertyCount()}:");
                for (int i = 0; i < shader.GetPropertyCount(); i++) line.Append($" {shader.GetPropertyName(i)}={(int)shader.GetPropertyType(i)}");
                Debug.Log(line.ToString());
            }
            var any = Shader.Find("Sprites/Default");
            foreach (int index in new[] { -1, any.GetPropertyCount() })
            {
                try { Debug.Log($"PERF measure shader props name({index}): {any.GetPropertyName(index)}"); }
                catch (System.Exception e) { Debug.Log($"PERF measure shader props name({index}): {e.GetType().Name}: {e.Message}"); }
                try { Debug.Log($"PERF measure shader props type({index}): {any.GetPropertyType(index)}"); }
                catch (System.Exception e) { Debug.Log($"PERF measure shader props type({index}): {e.GetType().Name}: {e.Message}"); }
            }
        }

        static readonly System.Collections.Generic.List<string> activationEvents = new System.Collections.Generic.List<string>();
        static void ActivatedInternal(TMPro.TMP_InputField __instance) => activationEvents.Add($"internal f{Time.frameCount} focused {__instance.isFocused}");
        static void DeactivatedNoArgs() => activationEvents.Add($"deactivate() f{Time.frameCount} from {new System.Diagnostics.StackFrame(2).GetMethod()?.Name}");
        static void DeactivatedBool(bool clearSelection) => activationEvents.Add($"deactivate({clearSelection}) f{Time.frameCount}");

        /// When TMP_InputField's private ActivateInputFieldInternal runs (Favorite Buildings patches
        /// it, with both DeactivateInputField overloads, to know when the player is typing).
        System.Collections.IEnumerator MeasureInputActivation()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var internalMethod = typeof(TMPro.TMP_InputField).GetMethod("ActivateInputFieldInternal", flags);
            if (internalMethod == null) { Debug.Log("PERF measure activation: no ActivateInputFieldInternal"); yield break; }
            var harmony = new HarmonyLib.Harmony("PerfProbe.Activation");
            System.Reflection.MethodInfo Own(string name) => typeof(Runner).GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            harmony.Patch(internalMethod, prefix: new HarmonyLib.HarmonyMethod(Own(nameof(ActivatedInternal))));
            harmony.Patch(typeof(TMPro.TMP_InputField).GetMethod("DeactivateInputField", System.Type.EmptyTypes), prefix: new HarmonyLib.HarmonyMethod(Own(nameof(DeactivatedNoArgs))));
            harmony.Patch(typeof(TMPro.TMP_InputField).GetMethod("DeactivateInputField", new[] { typeof(bool) }), prefix: new HarmonyLib.HarmonyMethod(Own(nameof(DeactivatedBool))));
            var canvas = new GameObject("PerfActivationCanvas").AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var field = new GameObject("PerfActivation", typeof(UnityEngine.UI.Image), typeof(TMPro.TMP_InputField));
            field.transform.SetParent(canvas.transform, false);
            var viewport = new GameObject("TextViewport", typeof(UnityEngine.UI.RectMask2D));
            viewport.transform.SetParent(field.transform, false);
            var text = new GameObject("Text", typeof(TMPro.TextMeshProUGUI));
            text.transform.SetParent(viewport.transform, false);
            var input = field.GetComponent<TMPro.TMP_InputField>();
            input.textViewport = (RectTransform)viewport.transform;
            input.textComponent = text.GetComponent<TMPro.TextMeshProUGUI>();
            yield return null;
            string Step(string name, System.Action act)
            {
                activationEvents.Add($"{name} at f{Time.frameCount}");
                act();
                activationEvents.Add($"{name} returned, focused {input.isFocused} selected {UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject == field}");
                return name;
            }
            System.Collections.IEnumerator Frames(int n) { for (int i = 0; i < n; i++) { yield return null; activationEvents.Add($"f{Time.frameCount} focused {input.isFocused}"); } }
            Step("activate", input.ActivateInputField);
            yield return Frames(2);
            Step("activate again", input.ActivateInputField);
            yield return Frames(2);
            Step("deactivate", input.DeactivateInputField);
            yield return Frames(1);
            Step("deactivate(true)", () => input.DeactivateInputField(true));
            yield return Frames(1);
            Step("select", () => UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(field));
            yield return Frames(2);
            Step("deselect", () => UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null));
            yield return Frames(2);
            input.shouldActivateOnSelect = false;
            Step("select without activate-on-select", () => UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(field));
            yield return Frames(2);
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
            input.shouldActivateOnSelect = true;
            field.SetActive(false);
            Step("activate while inactive", input.ActivateInputField);
            yield return Frames(2);
            field.SetActive(true);
            activationEvents.Add($"reactivated object f{Time.frameCount} focused {input.isFocused}");
            yield return Frames(2);
            Debug.Log("PERF measure activation: " + string.Join(" | ", activationEvents));
            harmony.UnpatchAll("PerfProbe.Activation");
            Destroy(canvas.gameObject);
        }

        System.Collections.IEnumerator MeasureLateInputField()
        {
            var logged = new System.Collections.Generic.List<string>();
            Application.LogCallback catcher = (text, trace, type) => { if (type != LogType.Log) logged.Add(type + ": " + text); };
            Application.logMessageReceived += catcher;
            var canvas = new GameObject("PerfInputCanvas").AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var field = new GameObject("PerfInput", typeof(UnityEngine.UI.Image), typeof(TMPro.TMP_InputField));
            field.transform.SetParent(canvas.transform, false);
            ((RectTransform)field.transform).sizeDelta = new Vector2(300, 30);
            var input = field.GetComponent<TMPro.TMP_InputField>();
            var viewport = new GameObject("TextViewport", typeof(UnityEngine.UI.RectMask2D), typeof(UnityEngine.UI.Image));
            viewport.transform.SetParent(field.transform, false);
            var viewportRect = (RectTransform)viewport.transform;
            viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one;
            var text = new GameObject("Text", typeof(TMPro.TextMeshProUGUI));
            text.transform.SetParent(viewport.transform, false);
            input.textViewport = viewportRect;
            input.textComponent = text.GetComponent<TMPro.TextMeshProUGUI>();
            string Children() => string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, viewport.transform.childCount), i => viewport.transform.GetChild(i).name));
            var line = new System.Text.StringBuilder($"PERF measure late input field: assigned [{Children()}]");
            input.text = "hello";
            Canvas.ForceUpdateCanvases();
            line.Append($" forced [{Children()}]");
            yield return null;
            yield return null;
            line.Append($" frames [{Children()}]");
            field.SetActive(false);
            field.SetActive(true);
            line.Append($" reenabled [{Children()}]");
            Canvas.ForceUpdateCanvases();
            yield return null;
            line.Append($" after [{Children()}] logged [{string.Join(" | ", logged)}]");
            Application.logMessageReceived -= catcher;
            Debug.Log(line.ToString());
            Destroy(canvas.gameObject);
        }

        // LineRenderer geometry (mods draw lasers and debug lines with it): BakeMesh of several
        // configurations seen by an orthographic camera looking along +Z.
        void MeasureLineRenderer()
        {
            var camera = new GameObject("PerfLineCamera").AddComponent<Camera>();
            camera.orthographic = true;
            camera.transform.position = new Vector3(0, 0, -10);
            camera.enabled = false;
            var fresh = new GameObject("PerfLineDefaults").AddComponent<LineRenderer>();
            var defaultPositions = new Vector3[fresh.positionCount];
            fresh.GetPositions(defaultPositions);
            Debug.Log($"PERF measure line defaults: count {fresh.positionCount} positions [{string.Join(" ", defaultPositions)}] width {fresh.startWidth}/{fresh.endWidth} mult {fresh.widthMultiplier} curve [{string.Join(" ", System.Array.ConvertAll(fresh.widthCurve.keys, k => $"{k.time}:{k.value}"))}] colors {(Color32)fresh.startColor}/{(Color32)fresh.endColor} world {fresh.useWorldSpace} loop {fresh.loop} corner {fresh.numCornerVertices} cap {fresh.numCapVertices} align {fresh.alignment} texture {fresh.textureMode} scale {fresh.textureScale} material {(fresh.sharedMaterial == null ? "null" : fresh.sharedMaterial.name)} shadows {fresh.shadowCastingMode} gradient [{string.Join(" ", System.Array.ConvertAll(fresh.colorGradient.colorKeys, k => $"{k.time}:{(Color32)k.color}"))}|{string.Join(" ", System.Array.ConvertAll(fresh.colorGradient.alphaKeys, k => $"{k.time}:{k.alpha}"))}]");
            Destroy(fresh.gameObject);

            void Bake(string what, System.Action<LineRenderer> setup, bool useTransform = false)
            {
                var go = new GameObject("PerfLine " + what);
                var line = go.AddComponent<LineRenderer>();
                line.startColor = new Color(1, 0, 0, 1);
                line.endColor = new Color(0, 0, 1, 0.5f);
                setup(line);
                var mesh = new Mesh();
                line.BakeMesh(mesh, camera, useTransform);
                var v = mesh.vertices;
                var uv = mesh.uv;
                var c = mesh.colors32;
                var n = mesh.normals;
                var t = mesh.triangles;
                var sb = new System.Text.StringBuilder($"PERF measure line {what}: {v.Length} vertices {t.Length / 3} triangles");
                for (int i = 0; i < v.Length; i++)
                    sb.Append($" | {v[i].x:0.####},{v[i].y:0.####},{v[i].z:0.####} uv {(i < uv.Length ? $"{uv[i].x:0.####},{uv[i].y:0.####}" : "-")} c {(i < c.Length ? $"{c[i].r},{c[i].g},{c[i].b},{c[i].a}" : "-")}{(i < n.Length ? $" n {n[i].x:0.##},{n[i].y:0.##},{n[i].z:0.##}" : "")}");
                sb.Append(" | tris ").Append(string.Join(",", t));
                Debug.Log(sb.ToString());
                Destroy(mesh);
                Destroy(go);
            }
            Bake("two", l => { l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); l.widthMultiplier = 1; });
            Bake("corner", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0) }); l.widthMultiplier = 1; });
            Bake("corner-round", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0) }); l.widthMultiplier = 1; l.numCornerVertices = 2; l.numCapVertices = 2; });
            Bake("acute", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 2, 0) }); l.widthMultiplier = 1; });
            Bake("curve", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(10, 0, 0) }); l.widthMultiplier = 2; l.widthCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0.2f)); });
            Bake("widths", l => { l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); l.startWidth = 2; l.endWidth = 0.5f; });
            Bake("tile", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(10, 0, 0) }); l.widthMultiplier = 0.5f; l.textureMode = LineTextureMode.Tile; l.textureScale = new Vector2(2, 1); });
            Bake("per-segment", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(10, 0, 0) }); l.textureMode = LineTextureMode.RepeatPerSegment; });
            Bake("distribute", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(10, 0, 0) }); l.textureMode = LineTextureMode.DistributePerSegment; });
            Bake("transformz", l => { l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); l.alignment = LineAlignment.TransformZ; });
            Bake("loop", l => { l.positionCount = 4; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 10, 0) }); l.loop = true; });
            Bake("view-depth", l => { l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 10) }); });
            Bake("local", l => { l.useWorldSpace = false; l.transform.position = new Vector3(5, 5, 0); l.transform.rotation = Quaternion.Euler(0, 0, 90); l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); });
            Bake("local-transform", l => { l.useWorldSpace = false; l.transform.position = new Vector3(5, 5, 0); l.transform.rotation = Quaternion.Euler(0, 0, 90); l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); }, useTransform: true);
            Bake("world-transform", l => { l.transform.position = new Vector3(5, 5, 0); l.positionCount = 2; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); }, useTransform: true);
            Bake("gradient", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(10, 0, 0) }); var g = new Gradient(); g.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.green, 0.5f), new GradientColorKey(Color.blue, 1) }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.2f, 1) }); l.colorGradient = g; });
            Bake("one", l => { l.positionCount = 1; l.SetPosition(0, Vector3.one); });
            Bake("duplicate", l => { l.positionCount = 3; l.SetPositions(new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(10, 0, 0) }); });
            Destroy(camera.gameObject);
        }

        // `missing-scripts` beside the probe names an asset bundle whose prefabs reference script
        // classes that do not exist at run time (a mod's): how Unity loads them.
        void MeasureMissingScripts()
        {
            var file = System.IO.Path.Combine(Path, "missing-scripts");
            if (!System.IO.File.Exists(file)) return;
            var bundle = AssetBundle.LoadFromFile(System.IO.File.ReadAllText(file).Trim());
            Debug.Log($"PERF measure missing scripts: bundle {(bundle == null ? "null" : bundle.name)}");
            if (bundle == null) return;
            foreach (var go in bundle.LoadAllAssets<GameObject>())
            {
                void Walk(Transform t, string path)
                {
                    var components = t.GetComponents<Component>();
                    int nulls = 0;
                    foreach (var c in components) if (c == null) nulls++;
                    var behaviours = t.GetComponents<MonoBehaviour>();
                    int nullBehaviours = 0;
                    foreach (var b in behaviours) if (b == null) nullBehaviours++;
                    if (nulls > 0 || nullBehaviours > 0)
                    {
                        // The slots in order (each component's type, "null" for a missing one), and
                        // what the single queries give.
                        var order = string.Join(",", System.Array.ConvertAll(components, c => ReferenceEquals(c, null) ? "null" : c == null ? "fake-null" : c.GetType().Name));
                        var first = t.GetComponent<MonoBehaviour>();
                        var firstText = ReferenceEquals(first, null) ? "null" : first == null ? "fake-null" : first.GetType().Name;
                        Debug.Log($"PERF measure missing scripts: {path} components {components.Length} null {nulls} behaviours {behaviours.Length} null {nullBehaviours} order [{order}] GetComponent<MonoBehaviour> {firstText} GetComponent<Behaviour> {(t.GetComponent<Behaviour>() is { } b2 ? b2.GetType().Name : "null")} InChildren<MonoBehaviour> {(t.GetComponentInChildren<MonoBehaviour>(true) is { } b3 ? b3.GetType().Name : "null")}");
                    }
                    foreach (Transform child in t) Walk(child, path + "/" + child.name);
                }
                Walk(go.transform, go.name);
                // A clone keeps the missing scripts' places (or not).
                var clone = Instantiate(go);
                void WalkClone(Transform t, string path)
                {
                    var components = t.GetComponents<Component>();
                    int nulls = 0;
                    foreach (var c in components) if (ReferenceEquals(c, null) || c == null) nulls++;
                    if (nulls > 0) Debug.Log($"PERF measure missing scripts: clone {path} components {components.Length} null {nulls}");
                    foreach (Transform child in t) WalkClone(child, path + "/" + child.name);
                }
                WalkClone(clone.transform, go.name);
                Debug.Log($"PERF measure missing scripts: FindObjectsByType<MonoBehaviour> nulls {System.Array.FindAll(FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None), m => m == null).Length}");
                Destroy(clone);
            }
            Debug.Log($"PERF measure bundle scenes: [{string.Join(",", bundle.GetAllScenePaths())}] assets {bundle.GetAllAssetNames().Length}");
            // The same bytes from memory while the file's bundle is loaded, then after unloading it.
            var bytes = System.IO.File.ReadAllBytes(System.IO.File.ReadAllText(file).Trim());
            var twice = AssetBundle.LoadFromMemory(bytes);
            Debug.Log($"PERF measure bundle from memory while loaded: {(twice == null ? "null" : twice.name)}");
            bundle.Unload(true);
            var fromMemory = AssetBundle.LoadFromMemory(bytes);
            Debug.Log($"PERF measure bundle from memory: {(fromMemory == null ? "null" : fromMemory.name)} assets {(fromMemory == null ? -1 : fromMemory.GetAllAssetNames().Length)} scenes [{(fromMemory == null ? "" : string.Join(",", fromMemory.GetAllScenePaths()))}] loaded {System.Linq.Enumerable.Count(AssetBundle.GetAllLoadedAssetBundles(), b => b == fromMemory)}");
            if (fromMemory != null) fromMemory.Unload(true);
            try { Debug.Log($"PERF measure bundle from null memory: {(AssetBundle.LoadFromMemory(null) == null ? "null" : "bundle")}"); }
            catch (System.Exception e) { Debug.Log($"PERF measure bundle from null memory: {e.GetType().Name}"); }
            Debug.Log($"PERF measure bundle from junk memory: {(AssetBundle.LoadFromMemory(new byte[] { 1, 2, 3, 4 }) == null ? "null" : "bundle")}");
        }

        static async void AsyncVoidThrows()
        {
            await System.Threading.Tasks.Task.Delay(10);
            throw new System.InvalidOperationException("PERF async void exception");
        }

        // Members mods call (Fast Track, Fast Save): renderer defaults, the mesh update flags, GPU
        // readback, PNGs from raw arrays.
        System.Collections.IEnumerator MeasureModRendering()
        {
            var go = new GameObject("MeasureRenderer");
            var renderer = go.AddComponent<MeshRenderer>();
            Debug.Log($"PERF measure renderer: allowOcclusionWhenDynamic {renderer.allowOcclusionWhenDynamic} motionVectorGenerationMode {renderer.motionVectorGenerationMode}");
            renderer.allowOcclusionWhenDynamic = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            Debug.Log($"PERF measure renderer: after setting {renderer.allowOcclusionWhenDynamic} {renderer.motionVectorGenerationMode}");
            Destroy(go);

            var mesh = new Mesh();
            mesh.SetVertices(new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(5, 5, 0) });
            mesh.SetTriangles(new[] { 0, 1, 3 }, 0);
            var fewer = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 2, 0) };
            mesh.SetVertices(fewer, 0, 3, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            Debug.Log($"PERF measure mesh: fewer vertices than indexed, DontValidateIndices: vertexCount {mesh.vertexCount} bounds {mesh.bounds}");
            mesh.SetVertices(fewer, 0, 3);
            Debug.Log($"PERF measure mesh: the same without flags: vertexCount {mesh.vertexCount} bounds {mesh.bounds}");
            var wide = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(3, 0, 0), new Vector3(0, 3, 0), new Vector3(9, 9, 0) };
            mesh.SetVertices(wide, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            Debug.Log($"PERF measure mesh: DontRecalculateBounds: vertexCount {mesh.vertexCount} last {mesh.vertices[3]} bounds {mesh.bounds}");
            mesh.SetVertices(wide, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            Debug.Log($"PERF measure mesh: DontValidateIndices, all four: bounds {mesh.bounds}");
            var far = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(0, 4, 0), new Vector3(20, 20, 0) };
            mesh.SetVertices(far, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.Default);
            Debug.Log($"PERF measure mesh: Default, all four: bounds {mesh.bounds}");
            mesh.SetVertices(wide);
            Debug.Log($"PERF measure mesh: no flags, all four: bounds {mesh.bounds}");
            mesh.SetVertices(far, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers);
            Debug.Log($"PERF measure mesh: DontNotifyMeshUsers, all four: bounds {mesh.bounds}");
            mesh.SetVertices(wide, 1, 3, UnityEngine.Rendering.MeshUpdateFlags.Default);
            Debug.Log($"PERF measure mesh: Default, a range: vertexCount {mesh.vertexCount} bounds {mesh.bounds}");
            mesh.SetUVs(0, new System.Collections.Generic.List<Vector2> { Vector2.zero, Vector2.one, Vector2.up, Vector2.right }, 1, 3, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            mesh.SetColors(new System.Collections.Generic.List<Color> { Color.red, Color.green, Color.blue, Color.white }, 1, 3, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            Debug.Log($"PERF measure mesh: uv {string.Join(" ", mesh.uv)} colors {string.Join(" ", mesh.colors)}");
            mesh.SetUVs(0, new System.Collections.Generic.List<Vector2> { Vector2.zero, Vector2.one, Vector2.up, Vector2.right }, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            mesh.SetColors(new System.Collections.Generic.List<Color> { Color.red, Color.green, Color.blue, Color.white }, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            Debug.Log($"PERF measure mesh: all four: uv {string.Join(" ", mesh.uv)} colors {string.Join(" ", mesh.colors)} bounds {mesh.bounds}");
            Destroy(mesh);
            var bare = new Mesh();
            bare.SetVertices(far);
            Debug.Log($"PERF measure mesh bounds: no triangles, SetVertices: {bare.bounds}");
            bare.vertices = wide.ToArray();
            Debug.Log($"PERF measure mesh bounds: no triangles, vertices =: {bare.bounds}");
            bare.SetTriangles(new[] { 0, 1, 2 }, 0);
            Debug.Log($"PERF measure mesh bounds: SetTriangles 0 1 2: {bare.bounds}");
            bare.vertices = far.ToArray();
            Debug.Log($"PERF measure mesh bounds: with triangles, vertices =: {bare.bounds}");
            bare.SetVertices(wide);
            Debug.Log($"PERF measure mesh bounds: with triangles, SetVertices: {bare.bounds}");
            bare.SetTriangles(new[] { 0, 1, 3 }, 0, false);
            Debug.Log($"PERF measure mesh bounds: SetTriangles without calculateBounds: {bare.bounds}");
            bare.triangles = new[] { 0, 1, 3 };
            Debug.Log($"PERF measure mesh bounds: triangles =: {bare.bounds}");
            bare.RecalculateBounds();
            Debug.Log($"PERF measure mesh bounds: RecalculateBounds: {bare.bounds}");
            var three = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
            bare.Clear();
            Debug.Log($"PERF measure mesh bounds: Clear: {bare.bounds}");
            bare.SetVertices(far);
            Debug.Log($"PERF measure mesh bounds: after Clear, SetVertices far: {bare.bounds}");
            bare.SetVertices(three);
            Debug.Log($"PERF measure mesh bounds: no triangles, 4 -> 3 vertices: {bare.bounds}");
            bare.SetVertices(wide);
            Debug.Log($"PERF measure mesh bounds: no triangles, 3 -> 4 vertices: {bare.bounds}");
            bare.SetTriangles(new[] { 0, 1, 2 }, 0, true);
            Debug.Log($"PERF measure mesh bounds: SetTriangles 0 1 2 calculateBounds: {bare.bounds}");
            bare.bounds = new Bounds(new Vector3(100, 100, 0), Vector3.one);
            bare.SetTriangles(new[] { 0, 1, 3 }, 0, true);
            Debug.Log($"PERF measure mesh bounds: bounds set, then SetTriangles 0 1 3: {bare.bounds}");
            bare.subMeshCount = 2;
            bare.SetTriangles(new[] { 0, 1, 2 }, 1, true);
            Debug.Log($"PERF measure mesh bounds: second submesh 0 1 2: {bare.bounds}");
            bare.SetVertices(far);
            Debug.Log($"PERF measure mesh bounds: with triangles, SetVertices far (4 -> 4): {bare.bounds}");
            Destroy(bare);

            var noise = new System.Text.StringBuilder();
            for (int j = 0; j < 16; j++)
                for (int i = 0; i < 16; i++)
                    noise.Append(' ').Append(Mathf.PerlinNoise(i * 0.37f + 0.13f, j * 0.53f + 0.29f).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < 16; i++)
                noise.Append(' ').Append(Mathf.PerlinNoise(-3.7f + i * 1.9f, 250.1f + i * 0.77f).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            Debug.Log($"PERF measure perlin:{noise}");
            // Its lattice: the value at whole coordinates and the slope there along x and y.
            var lattice = new System.Text.StringBuilder();
            for (int j = 0; j < 16; j++)
                for (int i = 0; i < 16; i++)
                {
                    float at = Mathf.PerlinNoise(i, j);
                    lattice.Append(' ').Append(at.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                        .Append(',').Append(((Mathf.PerlinNoise(i + 0.01f, j) - at) / 0.01f).ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                        .Append(',').Append(((Mathf.PerlinNoise(i, j + 0.01f) - at) / 0.01f).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                }
            Debug.Log($"PERF measure perlin lattice:{lattice}");
            var period = new System.Text.StringBuilder();
            foreach (float shift in new[] { 16f, 64f, 128f, 256f, 512f, 1024f })
                period.Append($" {shift}:{Mathf.PerlinNoise(1.3f + shift, 2.7f):F4}/{Mathf.PerlinNoise(1.3f, 2.7f + shift):F4}");
            Debug.Log($"PERF measure perlin period: base {Mathf.PerlinNoise(1.3f, 2.7f):F4}{period} negative {Mathf.PerlinNoise(-1.3f, -2.7f):F4}");
            Debug.Log($"PERF measure readback: supportsAsyncGPUReadback {SystemInfo.supportsAsyncGPUReadback}");
            Debug.Log($"PERF measure formats: RGBA32 {UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, false)} sRGB {UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, true)} ARGB32 {UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.ARGB32, false)} RGB24 {UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGB24, false)}");
            var raw = new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120 };
            var png = ImageConversion.EncodeArrayToPNG(raw, UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, false), 3, 2);
            var check = new Texture2D(1, 1);
            Debug.Log($"PERF measure png: {png.Length} bytes, loads {check.LoadImage(png)} {check.width}x{check.height} {string.Join(" ", check.GetPixels32())}");
            var rgb = new byte[] { 255, 0, 0, 0, 255, 0, 9, 9, 0, 0, 255, 10, 20, 30, 9, 9 };
            var png3 = ImageConversion.EncodeArrayToPNG(rgb, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8_UNorm, 2, 2, 8);
            Debug.Log($"PERF measure png: RGB rows of 8 bytes: {(png3 == null ? "null" : png3.Length + " bytes, loads " + check.LoadImage(png3) + " " + check.width + "x" + check.height + " " + check.format + " " + string.Join(" ", check.GetPixels32()))}");
            var gray = ImageConversion.EncodeArrayToPNG(new byte[] { 0, 100, 200, 255 }, UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm, 2, 2);
            Debug.Log($"PERF measure png: R8: {(gray == null ? "null" : gray.Length + " bytes, loads " + check.LoadImage(gray) + " " + check.format + " " + string.Join(" ", check.GetPixels32()))}");
            var bgra = ImageConversion.EncodeArrayToPNG(new byte[] { 255, 0, 0, 255 }, UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_UNorm, 1, 1);
            Debug.Log($"PERF measure png: BGRA: {(bgra == null ? "null" : bgra.Length + " bytes, loads " + check.LoadImage(bgra) + " " + string.Join(" ", check.GetPixels32()))}");
            var srgb = ImageConversion.EncodeArrayToPNG(new byte[] { 100, 150, 200, 255 }, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, 1, 1);
            Debug.Log($"PERF measure png: SRGB: {(srgb == null ? "null" : srgb.Length + " bytes, loads " + check.LoadImage(srgb) + " " + string.Join(" ", check.GetPixels32()))}");
            var table = new System.Text.StringBuilder();
            foreach (TextureFormat f in System.Enum.GetValues(typeof(TextureFormat)))
                table.Append($" {(int)f}:{(int)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(f, false)}/{(int)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(f, true)}");
            Debug.Log($"PERF measure formats: table{table}");
            Destroy(check);

            // A render texture filled with one colour, read back asynchronously.
            var rt = new RenderTexture(4, 2, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            var fill = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            var texels = new Color32[8];
            for (int i = 0; i < texels.Length; i++) texels[i] = new Color32(255, 128, 64, 255);
            fill.SetPixels32(texels);
            fill.Apply();
            Graphics.Blit(fill, rt);
            Destroy(fill);
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var request = UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                Debug.Log($"PERF measure readback: requested, done {request.done} hasError {request.hasError} {request.width}x{request.height}x{request.depth} layers {request.layerCount}");
                int frames = 0;
                while (!request.done && frames < 120) { frames++; yield return null; }
                var data = request.GetData<byte>();
                Debug.Log($"PERF measure readback: done after {frames} frames, hasError {request.hasError}, {data.Length} bytes, first {data[0]} {data[1]} {data[2]} {data[3]}");
                yield return null;
                try { Debug.Log($"PERF measure readback: data a frame later {request.GetData<byte>().Length}"); }
                catch (System.Exception e) { Debug.Log($"PERF measure readback: data a frame later {e.GetType().Name}: {e.Message}"); }
            }
            var cleared = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
            cleared.Create();
            var old = RenderTexture.active;
            RenderTexture.active = cleared;
            GL.Clear(true, true, new Color(1f, 0.5f, 0.25f, 1f));
            var read = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 2, 2), 0, 0);
            RenderTexture.active = old;
            Debug.Log($"PERF measure active render texture: GL.Clear then ReadPixels {read.GetPixels32()[0]}");
            Destroy(read);
            cleared.Release();
            Destroy(cleared);
            rt.Release();
            Destroy(rt);
        }

        System.Collections.IEnumerator Start()
        {
            Instance = this;
            // `fpsclock.txt`, `noautosave.txt`, `actions.txt`: comparison videos (Video).
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "fpsclock.txt"))) StartCoroutine(Video.Clock());
            if (Worldgen.Mode == "run") yield return Worldgen.Run();
            // `measure.txt`: engine behaviours measured at the main menu, the same in either player
            // (Measure).
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "measure.txt")))
            {
                while (MainMenu.Instance == null) yield return null;
                for (int i = 0; i < 60; i++) yield return null;
                yield return Measure();
            }
            // `menuresize.txt` "W H": at the main menu the window goes to W×H and 120 frames later
            // the screen is captured (menuresized.png), the same in either player.
            var menuResize = System.IO.Path.Combine(Path, "menuresize.txt");
            if (System.IO.File.Exists(menuResize))
            {
                var size = System.Array.ConvertAll(System.IO.File.ReadAllText(menuResize).Split(new[] { ' ', '\n' }, System.StringSplitOptions.RemoveEmptyEntries), int.Parse);
                while (MainMenu.Instance == null) yield return null;
                for (int i = 0; i < 120; i++) yield return null;
                Screen.SetResolution(size[0], size[1], FullScreenMode.Windowed);
                for (int i = 0; i < (size.Length > 2 ? size[2] : 120); i++) yield return null;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Path, "menuresized.png"));
                for (int i = 0; i < 10; i++) yield return null;
            }
            // `awake.txt`: frames are not held back, the same in both players, for measuring on a
            // desktop: the game keeps running at full speed while its window is out of focus
            // (GenericGameSettings.sleepWhenOutOfFocus off), so the result does not depend on
            // where the user clicks, and vsync is off (QualitySettings.vSyncCount 0).
            // `pointerlog.txt`: Input.mousePosition every quarter second for 40 s from the menu, with
            // the time, to follow a pointer a script moves outside and inside the window.
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "pointerlog.txt"))) StartCoroutine(PointerLog());
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "focuslog.txt"))) StartCoroutine(FocusLog());
            awake = System.IO.File.Exists(System.IO.Path.Combine(Path, "awake.txt"));
            if (awake)
            {
                var sleep = typeof(Klei.GenericGameSettings).GetProperty("sleepWhenOutOfFocus");
                sleep.GetSetMethod(true).Invoke(Klei.GenericGameSettings.instance, new object[] { false });
                QualitySettings.vSyncCount = 0;
                Debug.Log($"PERF awake: sleepWhenOutOfFocus {sleep.GetValue(Klei.GenericGameSettings.instance)}, vSyncCount {QualitySettings.vSyncCount}");
            }
            // `load.txt` in the mod's folder names a save (full path) to load from the main menu,
            // as the load screen does: for loading a given save in either player.
            var load = System.IO.Path.Combine(Path, "load.txt");
            if (System.IO.File.Exists(load))
            {
                while (MainMenu.Instance == null) yield return null;
                for (int i = 0; i < 120; i++) yield return null;
                var save = System.IO.File.ReadAllText(load).Trim();
                Debug.Log("PERF loading " + save);
                LoadScreen.DoLoad(save);
            }
            while (Game.Instance == null || SpeedControlScreen.Instance == null) yield return null;
            Debug.Log($"PERF colony f{Time.frameCount} cycle {GameClock.Instance.GetCycle()}{RuntimeCounters.Jit()}{RuntimeCounters.Gc()}");
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "truetiles.txt"))) ReportTileTextures();
            // `localcanvas.txt`: LocalCanvas's artwork stages, and a painting set to its first local
            // image and captured (localcanvas.png).
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "localcanvas.txt"))) StartCoroutine(LocalCanvasPainting());
            // `blueprints.txt`: Blueprints Expanded's blueprint of the area in view, written, read
            // back and placed 30 cells to the right (blueprints.png).
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "blueprints.txt"))) StartCoroutine(BlueprintRoundTrip());
            // `pointer.txt`: what the game's hover handling reads (PlayerController.UpdateHover, the
            // select tool's hover card) while the mouse has not moved, every 120 frames, 6 times.
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "pointer.txt"))) StartCoroutine(Pointer());
            // `walltime.txt`: wall time of thin game methods inside the scripts' updates (WallTime).
            // `overlays.txt`: overlays opened in turn (Overlays), which then run the walltime windows.
            var overlaysFile = System.IO.Path.Combine(Path, "overlays.txt");
            bool overlays = System.IO.File.Exists(overlaysFile);
            WallTime.Extra = System.IO.Path.Combine(Path, "walltime.txt");
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "walltime.txt")) && !overlays) StartCoroutine(WallTime.Run(10));
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "savetest.txt"))) { yield return SaveTest(); yield break; }
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "autosave.txt")))
            {
                if (System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "autosave.txt")).Trim().Length > 0) StartCoroutine(AutosaveFreeze());
                else { yield return AutosaveFreeze(); yield break; }
            }
            // `camera.txt`: where the view stays, as world cells `x y` with an optional orthographic
            // size, `top` (the active world's top centre) or `quiet` (the discovered planetoid with
            // the fewest building cells made active, and its 48×28-cell window with the fewest, for
            // comparing a quiet view). The view is put back if the game moves it.
            var cameraFile = System.IO.Path.Combine(Path, "camera.txt");
            if (System.IO.File.Exists(cameraFile))
            {
                // After the load has placed the camera at the save's position.
                for (int i = 0; i < 200; i++) yield return null;
                var words = System.IO.File.ReadAllText(cameraFile).Split(new[] { ' ', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
                var world = ClusterManager.Instance.activeWorld;
                if (words[0] == "quiet")
                {
                    WorldContainer emptiest = null;
                    int fewest = int.MaxValue;
                    foreach (var candidate in ClusterManager.Instance.WorldContainers)
                    {
                        if (!candidate.IsDiscovered || candidate.IsModuleInterior) continue;
                        int count = BuildingCells(candidate.WorldOffset.x, candidate.WorldOffset.y, candidate.WorldSize.x, candidate.WorldSize.y);
                        Debug.Log($"PERF world {candidate.id} {candidate.GetComponent<ClusterGridEntity>()?.Name}: {count} building cells");
                        if (count < fewest) { fewest = count; emptiest = candidate; }
                    }
                    if (emptiest != world)
                    {
                        ClusterManager.Instance.SetActiveWorld(emptiest.id);
                        for (int i = 0; i < 120; i++) yield return null;
                        world = emptiest;
                    }
                }
                bool named = words[0] == "top" || words[0] == "quiet";
                Vector3 at = words[0] == "top" ? new Vector3(world.WorldOffset.x + world.WorldSize.x * 0.5f, world.WorldOffset.y + world.WorldSize.y - 12f, 0f)
                    : words[0] == "quiet" ? QuietestView(world)
                    : new Vector3(float.Parse(words[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(words[1], System.Globalization.CultureInfo.InvariantCulture), 0f);
                float size = words.Length > (named ? 1 : 2) ? float.Parse(words[^1], System.Globalization.CultureInfo.InvariantCulture) : 12f;
                at.z = CameraController.Instance.transform.position.z;
                CameraController.Instance.SnapTo(at, size);
                Debug.Log($"PERF camera at {at} size {size}");
                view = (at, size);
            }
            if (overlays) StartCoroutine(Overlays.Run(overlaysFile, System.IO.File.Exists(WallTime.Extra)));
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "noautosave.txt"))) Video.NoAutosave(true);
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "actions.txt"))) StartCoroutine(Video.Actions(this, Path));
            // `rebuild.txt` (Rebuilds), `screens.txt` (Screens), `starmap.txt` (Starmap), `cameras.txt` (Cameras).
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "cameras.txt"))) StartCoroutine(Cameras.Run(System.IO.Path.Combine(Path, "cameras.txt")));
            // `ab.txt`: a change against itself switched off, alternating in one run (Toggle).
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "ab.txt"))) StartCoroutine(Toggle.Run(System.IO.Path.Combine(Path, "ab.txt")));
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "rebuild.txt"))) StartCoroutine(Rebuilds.Run(System.IO.Path.Combine(Path, "rebuild.txt")));
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "screens.txt"))) StartCoroutine(Screens.Run(System.IO.Path.Combine(Path, "screens.txt")));
            if (System.IO.File.Exists(System.IO.Path.Combine(Path, "starmap.txt"))) StartCoroutine(Starmap.Run(System.IO.Path.Combine(Path, "starmap.txt")));
            // `counts.txt` lists component type names (Assembly-CSharp first, then -firstpass): how
            // many of each exist, active or not, logged once the colony is up.
            var counts = System.IO.Path.Combine(Path, "counts.txt");
            if (System.IO.File.Exists(counts))
                foreach (var name in System.IO.File.ReadAllLines(counts))
                {
                    if (name.Trim().Length == 0) continue;
                    var type = System.Type.GetType(name.Trim() + ", Assembly-CSharp") ?? System.Type.GetType(name.Trim() + ", Assembly-CSharp-firstpass");
                    if (type == null || !typeof(Object).IsAssignableFrom(type)) Debug.Log($"PERF count {name}: not a component type");
                    else Debug.Log($"PERF count {name} {FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None).Length}");
                }
            // `select.txt`: an assembly-qualified component type; 200 frames into the colony the first
            // selectable object with it on the active world is selected, the view centred on it,
            // and 90 frames later the screen captured (select.png) and the details screen's active
            // side screens logged with their sizes: for comparing a mod's side screen in either
            // player.
            var selectFile = System.IO.Path.Combine(Path, "select.txt");
            if (System.IO.File.Exists(selectFile)) yield return Select(System.IO.File.ReadAllLines(selectFile)[0].Trim());
            // `speed.txt`: the game speed kept, 0 (normal) to 2 (fastest, the default), or -1: paused.
            var speedFile = System.IO.Path.Combine(Path, "speed.txt");
            int speed = System.IO.File.Exists(speedFile) ? int.Parse(System.IO.File.ReadAllText(speedFile).Trim()) : 2;
            // `pausefirst.txt` "FRAMES": the colony is held paused for its first FRAMES frames
            // (a multiple of 600 keeps the windows apart), then runs at speed.txt's speed: one
            // launch measures a still colony and a running one (scripts/perf-spread.py).
            var pauseFile = System.IO.Path.Combine(Path, "pausefirst.txt");
            int pauseFirst = System.IO.File.Exists(pauseFile) ? int.Parse(System.IO.File.ReadAllText(pauseFile).Trim()) : 0;
            int keptSpeed = speed;
            if (pauseFirst > 0) speed = -1;
            // `resize.txt` "FRAMES W H": that many frames into the colony the window goes to W×H
            // (Screen.SetResolution, windowed); the game's error screen it raises is closed 60
            // frames later and the screen captured at 120 (resized.png beside the probe), the
            // same in either player.
            var resizeFile = System.IO.Path.Combine(Path, "resize.txt");
            int[] resize = System.IO.File.Exists(resizeFile) ? System.Array.ConvertAll(System.IO.File.ReadAllText(resizeFile).Split(new[] { ' ', '\n' }, System.StringSplitOptions.RemoveEmptyEntries), int.Parse) : null;
            int colonyFrames = 0;
            float windowStart = Time.realtimeSinceStartup;
            int frames = 0;
            long contentions = RuntimeCounters.LockContentions(), paused = RuntimeCounters.GcPauseMs();
            long allocatedBytes = RuntimeCounters.Allocated(), gen0 = System.GC.CollectionCount(0);
            // `alloc.txt`: managed allocation counters every window (AllocMeter), after a calibration.
            bool alloc = System.IO.File.Exists(System.IO.Path.Combine(Path, "alloc.txt"));
            if (alloc) { AllocMeter.Start(); AllocMeter.Calibrate(); }
            // `fps.txt`: a frame-rate cap (Application.targetFrameRate, vsync off), e.g. to measure
            // one player at the other's frame rate.
            var fpsCap = System.IO.Path.Combine(Path, "fps.txt");
            if (System.IO.File.Exists(fpsCap))
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = int.Parse(System.IO.File.ReadAllText(fpsCap).Trim());
                Debug.Log($"PERF target frame rate {Application.targetFrameRate}");
            }
            while (true)
            {
                yield return null;
                if (Game.Instance == null) yield break;
                if (alloc) AllocMeter.Frame();
                // Only when the game has stopped: the search for dialogs walks the whole scene, a
                // cost the measurement must not carry.
                if (speed < 0 && Time.frameCount % 60 == 0 && !SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Pause(false);
                else if (speed >= 0 && Time.frameCount % 60 == 0 && (SpeedControlScreen.Instance.IsPaused || SpeedControlScreen.Instance.GetSpeed() != speed))
                {
                    Screens.DismissDialogs();
                    SpeedControlScreen.Instance.SetSpeed(speed);
                    SpeedControlScreen.Instance.Unpause(false);
                }
                if (Time.frameCount % 60 == 0 && view is { } v && Vector2.Distance(CameraController.Instance.transform.position, v.At) > 1f)
                {
                    CameraController.Instance.SnapTo(v.At, v.Size);
                    Debug.Log($"PERF camera moved by the game; put back at {v.At}");
                }
                colonyFrames++;
                // The load's dialogs (MOD DIFFERENCES) close while held paused, as they do running.
                if (pauseFirst > 0 && (colonyFrames == 60 || colonyFrames == 300)) Screens.DismissDialogs();
                if (pauseFirst > 0 && colonyFrames == pauseFirst)
                {
                    speed = keptSpeed;
                    Debug.Log($"PERF phase run f{Time.frameCount} speed {speed}");
                }
                if (resize != null && colonyFrames == resize[0])
                {
                    Debug.Log($"PERF resize to {resize[1]}x{resize[2]}");
                    Screen.SetResolution(resize[1], resize[2], FullScreenMode.Windowed);
                }
                // The game's error screen for the resize's exception (KCanvasScaler, the same in
                // the original player) closes, so the capture shows the world at the new size.
                if (resize != null && colonyFrames == resize[0] + 60)
                {
                    KCrashReporter.terminateOnError = false;
                    var errorCanvas = GameObject.Find(KCrashReporter.error_canvas_name);
                    if (errorCanvas != null) Destroy(errorCanvas);
                }
                if (resize != null && colonyFrames == resize[0] + 120)
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Path, "resized.png"));
                if (++frames < 600) continue;
                float now = Time.realtimeSinceStartup;
                long c = RuntimeCounters.LockContentions(), g = RuntimeCounters.GcPauseMs();
                long a = RuntimeCounters.Allocated(), g0 = System.GC.CollectionCount(0);
                string runtime = c < 0 ? "" : $" contentions {c - contentions} gc-pause {g - paused} ms allocated {(a - allocatedBytes) / 1048576.0:F0} MiB gen0 {g0 - gen0}";
                contentions = c; paused = g; allocatedBytes = a; gen0 = g0;
                Debug.Log($"PERF f{Time.frameCount} fps {frames / (now - windowStart):F2} cycle {GameClock.Instance.GetCycle()} time {GameClock.Instance.GetTime():F0} paused {SpeedControlScreen.Instance.IsPaused}{runtime}");
                if (alloc) Debug.Log(AllocMeter.Window());
                if (alloc && AllocSites.Enabled) Debug.Log(AllocSites.Window());
                if (awake) Debug.Log($"PERF pointer: mousePosition {Input.mousePosition} present {Input.mousePresent} focused {Application.isFocused}{OsCursor()}");
                windowStart = now;
                frames = 0;
            }
        }
    }

    /// .NET's runtime counters, read by reflection: the probe builds against the game's Mono
    /// libraries, which lack them (-1 there).
    static class RuntimeCounters
    {
        static readonly System.Reflection.PropertyInfo contention = typeof(System.Threading.Monitor).GetProperty("LockContentionCount");
        static readonly System.Reflection.MethodInfo pause = typeof(System.GC).GetMethod("GetTotalPauseDuration", System.Type.EmptyTypes);

        public static long LockContentions() => contention == null ? -1 : (long)contention.GetValue(null);
        static readonly System.Type jitInfo = System.Type.GetType("System.Runtime.JitInfo");

        /// Time spent compiling so far, summed over all threads, the compiles (a method compiled
        /// again at a higher tier counts again), and the calling thread's compile time (.NET;
        /// empty on Mono).
        public static string Jit()
        {
            if (jitInfo == null) return "";
            var time = (System.TimeSpan)jitInfo.GetMethod("GetCompilationTime").Invoke(null, new object[] { false });
            var count = (long)jitInfo.GetMethod("GetCompiledMethodCount").Invoke(null, new object[] { false });
            var own = (System.TimeSpan)jitInfo.GetMethod("GetCompilationTime").Invoke(null, new object[] { true });
            return $" jit {time.TotalMilliseconds:F0} ms over {count} methods ({own.TotalMilliseconds:F0} ms on this thread)";
        }

        /// The GC's pauses and collections (gen 0/1/2) so far (.NET; empty on Mono).
        public static string Gc() => pause == null || allocated == null ? "" : $" gc-pause {GcPauseMs()} ms collections {System.GC.CollectionCount(0)}/{System.GC.CollectionCount(1)}/{System.GC.CollectionCount(2)} allocated {(long)allocated.Invoke(null, new object[] { false }) >> 20} MiB";
        static readonly System.Reflection.MethodInfo allocated = typeof(System.GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });

        /// Bytes allocated so far (.NET; -1 on Mono).
        public static long Allocated() => allocated == null ? -1 : (long)allocated.Invoke(null, new object[] { false });

        public static long GcPauseMs() => pause == null ? -1 : (long)((System.TimeSpan)pause.Invoke(null, null)).TotalMilliseconds;
    }
}
