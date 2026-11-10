using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace E2eDriver
{
    /// The commands (README.md). Each runs on the main thread; one that spans frames sets
    /// request.Pending and answers with request.State once it returns true.
    static class Commands
    {
        public static JToken Run(Request request)
        {
            var a = request.Args;
            switch (request.Command)
            {
                case "ping": return Ping();
                case "ui.find": return UiFind(a);
                case "ui.at": return Ui.Hit((float)(a["x"] ?? throw new DriverError("ui.at needs x")), (float)(a["y"] ?? throw new DriverError("ui.at needs y")), null, (int?)a["hits"] ?? 8);
                case "ui.dump":
                    // settle: N waits up to N frames for the build menu first, and dumps in the
                    // frame it is found settled.
                    if (a["settle"] != null)
                        return BuildMenu.Then(request, (int)a["settle"], check => { var dump = (JObject)UiDump(a); dump["settle"] = check; return dump; });
                    return UiDump(a);
                case "ui.wait": UiWait(request); return null;
                case "world.to_screen": return WorldToScreen(a);
                case "screen.to_cell": return ScreenToCell(a);
                case "state.get": return State.Result(State.Eval((string)a["expr"] ?? throw new DriverError("state.get needs expr")), a);
                case "state.each": return State.Each(a);
                case "camera.get": return CameraGet();
                case "camera.set": CameraSet(request); return null;
                case "canvas.hide": CanvasHide(request); return null;
                case "canvas.show": CanvasShow(request); return null;
                case "setup.call": return State.Result(State.Call(a), a);
                case "seed": return Seeding.Seed(a);
                case "count": return Counting.Start(a);
                case "stopwatch": return MethodTimes.Start(request);
                case "buildmenu.settled": return BuildMenu.Settled(request);
                case "keys.log": return KeyLog.Start(a);
                case "gc.watch": return GcWatch.Start(a);
                case "reports.watch": return Reports.Watch(a);
                case "ui.action": return UiAction(a);
                case "imgui.scrollview": return ImguiProbe.Start(a);
                case "templates.load": return Templates.Load(a);
                case null: throw new DriverError("the request has no cmd");
                default: throw new DriverError("unknown command " + request.Command);
            }
        }

        /// ui.action {action}: the key-down and key-up of one of the game's management-screen
        /// Actions through KScreenManager, as their hotkeys deliver them (`open: {management}`).
        /// The same in U59 and U60: OniMcp's open_management called ManagementMenu.OpenResearch,
        /// which returns bool in U60 (void in U59), so its handler no longer compiled there.
        static readonly string[] UiActions =
        {
            "ManageVitals", "ManageConsumables", "ManagePriorities", "ManageSchedule", "ManageSkills",
            "ManageResearch", "ManageStarmap", "ManageReport", "ManageDatabase",
        };

        static JToken UiAction(JObject a)
        {
            var name = (string)a["action"] ?? throw new DriverError("ui.action needs action");
            if (Array.IndexOf(UiActions, name) < 0) throw new DriverError("ui.action: not a management action: " + name);
            var action = (global::Action)Enum.Parse(typeof(global::Action), name);
            var manager = KScreenManager.Instance ?? throw new DriverError("ui.action: no KScreenManager");
            manager.OnKeyDown(new KButtonEvent(null, InputEventType.KeyDown, action));
            manager.OnKeyUp(new KButtonEvent(null, InputEventType.KeyUp, action));
            return new JObject { ["action"] = name };
        }

        /// Answers `frames` frames from now with what `result` gives then.
        static void Later(Request request, int frames, Func<JToken> result)
        {
            request.Pending = r =>
            {
                if (Time.frameCount - r.StartFrame < frames) return false;
                r.State = result();
                return true;
            };
        }

        static JToken Ping()
        {
            bool ours = AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name.StartsWith("OniMoono", StringComparison.Ordinal) || x.GetName().Name.StartsWith("OniBrutal", StringComparison.Ordinal));
            return new JObject
            {
                ["driver"] = Driver.Version,
                ["player"] = ours ? "ours" : "original",
                ["unity"] = Application.unityVersion,
                ["scene"] = SceneManager.GetActiveScene().name,
                ["screen"] = new JObject { ["w"] = Screen.width, ["h"] = Screen.height },
                ["port"] = Driver.Port,
                ["paused"] = SpeedControlScreen.Instance != null ? (JToken)SpeedControlScreen.Instance.IsPaused : JValue.CreateNull(),
                ["world"] = Grid.WidthInCells > 0 && Game.Instance != null,
            };
        }

        static JToken UiFind(JObject a)
        {
            var found = Ui.Find(a);
            int limit = (int?)a["limit"] ?? 50;
            bool hit = (bool?)a["hit"] ?? true;
            int hits = (int?)a["hits"] ?? 0;
            var elements = new JArray();
            foreach (var rt in found.Take(limit)) elements.Add(Ui.Describe(rt, hit, hits));
            return new JObject { ["count"] = found.Count, ["elements"] = elements };
        }

        static JToken UiDump(JObject a)
        {
            bool inactive = (bool?)a["inactive"] ?? false;
            List<Transform> starts;
            var root = (string)a["root"];
            if (root == null) starts = Ui.TopCanvases(inactive);
            else
            {
                var matches = Ui.Find(new JObject { ["path"] = root, ["inactive"] = inactive });
                // Each match's subtree once: matches inside another match's subtree are dropped.
                var set = new HashSet<Transform>(matches.Select(m => (Transform)m));
                starts = new List<Transform>();
                foreach (var m in matches)
                {
                    bool inner = false;
                    for (var p = m.transform.parent; p != null && !inner; p = p.parent) inner = set.Contains(p);
                    if (!inner) starts.Add(m);
                }
                if (starts.Count == 0) throw new DriverError("no element matches " + root);
            }
            var lines = Ui.Dump(starts, inactive);
            var result = new JObject { ["roots"] = new JArray(starts.Select(s => Ui.PathOf(s))), ["count"] = lines.Count };
            var file = (string)a["file"];
            if (file != null)
            {
                File.WriteAllText(file, string.Join("\n", lines) + (lines.Count > 0 ? "\n" : ""));
                result["file"] = file;
            }
            else result["lines"] = new JArray(lines);
            return result;
        }

        /// ui.wait: the query's first match checked every frame until the condition holds.
        static void UiWait(Request request)
        {
            var a = request.Args;
            var cond = (string)a["cond"] ?? "exists";
            int frames = (int?)a["frames"] ?? 300, stableFrames = (int?)a["stable"] ?? 5;
            var query = new JObject(a) { ["inactive"] = cond == "inactive" || cond == "gone" };
            var known = new[] { "exists", "gone", "active", "inactive", "interactable", "not_interactable", "on", "off", "text", "stable" };
            if (!known.Contains(cond)) throw new DriverError("unknown cond " + cond + " (one of " + string.Join(", ", known) + ")");
            if (cond == "text" && a["value"] == null) throw new DriverError("cond text needs value");
            Rect? last = null;
            int same = 0;
            JToken state = null;
            request.Pending = r =>
            {
                var found = Ui.Find(query);
                var first = found.FirstOrDefault(f => cond == "gone" ? f.gameObject.activeInHierarchy : true);
                state = first == null ? null : Ui.Describe(first, false);
                bool met;
                switch (cond)
                {
                    case "exists": met = first != null; break;
                    case "gone": met = first == null; break;
                    case "active": met = first != null && first.gameObject.activeInHierarchy; break;
                    case "inactive": met = first == null || !first.gameObject.activeInHierarchy; break;
                    case "interactable": met = first != null && (bool?)state["interactable"] == true && (bool)state["groupInteractable"]; break;
                    case "not_interactable": met = first != null && ((bool?)state["interactable"] == false || !(bool)state["groupInteractable"]); break;
                    case "on": met = first != null && (bool?)state["isOn"] == true; break;
                    case "off": met = first != null && (bool?)state["isOn"] == false; break;
                    case "text": met = first != null && Ui.TextMatches(Ui.TextOf(first), (string)a["value"]); break;
                    default:
                        // stable: the same screen rect for `stable` consecutive frames.
                        if (first == null) { last = null; same = 0; met = false; break; }
                        var rect = Ui.ScreenRect(first);
                        same = last.HasValue && Close(last.Value, rect) ? same + 1 : 0;
                        last = rect;
                        met = same >= stableFrames;
                        break;
                }
                int waited = Time.frameCount - r.StartFrame;
                if (met)
                {
                    r.State = new JObject { ["frames"] = waited, ["count"] = found.Count, ["element"] = state };
                    return true;
                }
                if (waited >= frames) throw new WaitTimeout($"timeout: {cond} not met within {frames} frames", new JObject { ["frames"] = waited, ["count"] = found.Count, ["element"] = state });
                return false;
            };
        }

        static bool Close(Rect a, Rect b) => Math.Abs(a.x - b.x) < 0.01f && Math.Abs(a.y - b.y) < 0.01f && Math.Abs(a.width - b.width) < 0.01f && Math.Abs(a.height - b.height) < 0.01f;

        // ---- world and camera ----

        static void NeedWorld()
        {
            if (Grid.WidthInCells <= 0 || Game.Instance == null) throw new DriverError("no world is loaded");
        }

        /// Camera.main, else (the main menu has no camera tagged MainCamera) the enabled camera of
        /// the lowest depth.
        static Camera MainCamera()
        {
            if (Camera.main != null) return Camera.main;
            Camera best = null;
            foreach (var cam in Camera.allCameras)
                if (best == null || cam.depth < best.depth) best = cam;
            return best ?? throw new DriverError("there is no camera");
        }

        /// A cell from {cell: N} or {cell: [x, y]}.
        static int CellOf(JToken token)
        {
            if (token is JArray xy && xy.Count == 2) return Grid.XYToCell((int)xy[0], (int)xy[1]);
            if (token != null && token.Type == JTokenType.Integer) return (int)token;
            throw new DriverError("cell is N or [x, y]");
        }

        /// The world position of {cell} (its centre) or {pos: [x, y(, z)]}.
        static Vector3 WorldPos(JObject a)
        {
            if (a["pos"] is JArray p && p.Count >= 2) return new Vector3((float)p[0], (float)p[1], p.Count > 2 ? (float)p[2] : 0f);
            NeedWorld();
            int cell = CellOf(a["cell"]);
            if (!Grid.IsValidCell(cell)) throw new DriverError("cell out of range");
            return Grid.CellToPosCCC(cell, Grid.SceneLayer.Move);
        }

        static JToken WorldToScreen(JObject a)
        {
            var world = WorldPos(a);
            var p = MainCamera().WorldToScreenPoint(world);
            float x = p.x, y = Screen.height - p.y;
            return new JObject
            {
                ["x"] = Values.Px(x), ["y"] = Values.Px(y),
                ["onscreen"] = p.z > 0 && x >= 0 && y >= 0 && x < Screen.width && y < Screen.height,
                ["world"] = Values.Vec(world),
            };
        }

        static JToken ScreenToCell(JObject a)
        {
            NeedWorld();
            var cam = MainCamera();
            float x = (float?)a["x"] ?? throw new DriverError("screen.to_cell needs x and y"), y = (float?)a["y"] ?? throw new DriverError("screen.to_cell needs x and y");
            var world = cam.ScreenToWorldPoint(new Vector3(x, Screen.height - y, -cam.transform.position.z));
            int cell = Grid.PosToCell(world);
            bool valid = Grid.IsValidCell(cell);
            var result = new JObject { ["cell"] = cell, ["valid"] = valid, ["world"] = new JObject { ["x"] = Values.Num(world.x), ["y"] = Values.Num(world.y) } };
            if (valid)
            {
                var xy = Grid.CellToXY(cell);
                result["xy"] = new JArray(xy.x, xy.y);
                result["worldId"] = (int)Grid.WorldIdx[cell];
            }
            return result;
        }

        static JToken CameraGet()
        {
            var cam = MainCamera();
            var pos = cam.transform.position;
            var result = new JObject
            {
                ["camera"] = cam.name,
                ["main"] = cam == Camera.main,
                ["pos"] = Values.Vec(pos),
                ["ortho"] = Values.Num(cam.orthographicSize),
                ["orthographic"] = cam.orthographic,
                ["pixels"] = new JObject { ["w"] = cam.pixelWidth, ["h"] = cam.pixelHeight },
                ["controller"] = CameraController.Instance != null,
            };
            if (Grid.WidthInCells > 0 && Game.Instance != null)
            {
                int cell = Grid.PosToCell(pos);
                if (Grid.IsValidCell(cell)) { var xy = Grid.CellToXY(cell); result["cell"] = new JArray(xy.x, xy.y); }
            }
            return result;
        }

        /// camera.set: {pos: [x, y] | cell: N or [x, y], ortho}. In a colony through the game's
        /// CameraController.SnapTo (it moves all the game's cameras); without one (the main
        /// menu), or with raw: true, on Camera.main itself. Answers two frames later with the
        /// camera as it then is (the controller may constrain it).
        static void CameraSet(Request request)
        {
            var a = request.Args;
            var cam = MainCamera();
            var pos = a["pos"] == null && a["cell"] == null ? cam.transform.position : WorldPos(a);
            float ortho = (float?)a["ortho"] ?? cam.orthographicSize;
            bool raw = (bool?)a["raw"] ?? false;
            var controller = CameraController.Instance;
            if (controller != null && !raw)
            {
                if ((float?)a["max"] is float max) controller.SetMaxOrthographicSize(max);
                controller.SnapTo(new Vector3(pos.x, pos.y, cam.transform.position.z), ortho);
            }
            else
            {
                cam.transform.position = new Vector3(pos.x, pos.y, cam.transform.position.z);
                cam.orthographicSize = ortho;
            }
            Later(request, 2, CameraGet);
        }

        // ---- canvases ----

        static readonly List<Canvas> hidden = new List<Canvas>();

        /// canvas.hide: every enabled root canvas off (world-space ones only with world: true),
        /// remembered for canvas.show; answers two frames later, once a frame has drawn without.
        static void CanvasHide(Request request)
        {
            bool world = (bool?)request.Args["world"] ?? false;
            var names = new JArray();
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID))
                if (canvas.isRootCanvas && canvas.enabled && (world || canvas.renderMode != RenderMode.WorldSpace))
                {
                    canvas.enabled = false;
                    hidden.Add(canvas);
                    names.Add(Ui.PathOf(canvas.transform));
                }
            var sorted = new JArray(names.Select(n => (string)n).OrderBy(n => n, StringComparer.Ordinal));
            Later(request, 2, () => new JObject { ["hidden"] = names.Count, ["canvases"] = sorted, ["total"] = hidden.Count });
        }

        static void CanvasShow(Request request)
        {
            int shown = 0;
            foreach (var canvas in hidden)
                if (canvas != null) { canvas.enabled = true; shown++; }
            hidden.Clear();
            Later(request, 2, () => new JObject { ["shown"] = shown });
        }
    }

    /// seed: the same random numbers in both players before a random screen opens. Unity's
    /// generator (UnityEngine.Random.InitState), the game's shared KRandom (Util.random), and
    /// every KRandom made without a seed from then on (they seed themselves from
    /// Environment.TickCount; a prefix on KRandom(int) replaces a seed equal to the current tick
    /// count with the next of n+1, n+2, …; the parameterless constructor is too small to patch
    /// reliably, as a JIT may inline it into its callers).
    static class Seeding
    {
        static bool installed, armed;
        static int next;
        public static int Consumed;

        public static JToken Seed(JObject a)
        {
            int n = (int?)a["n"] ?? throw new DriverError("seed needs n");
            bool krandom = (bool?)a["krandom"] ?? true;
            UnityEngine.Random.InitState(n);
            var field = typeof(Util).GetField("random", Values.All);
            bool util = false;
            if (field != null && field.FieldType == typeof(KRandom))
            {
                field.SetValue(null, new KRandom(n));
                util = true;
            }
            int before = Consumed;
            if (krandom)
            {
                if (!installed)
                {
                    var ctor = HarmonyLib.AccessTools.Constructor(typeof(KRandom), new[] { typeof(int) });
                    Mod.Harmony.Patch(ctor, prefix: new HarmonyLib.HarmonyMethod(typeof(Seeding), nameof(Prefix)));
                    installed = true;
                }
                next = n + 1;
                Consumed = 0;
            }
            armed = krandom;
            return new JObject { ["unity"] = n, ["util"] = util, ["krandom"] = krandom, ["consumedBefore"] = before };
        }

        static void Prefix(ref int Seed)
        {
            if (!armed) return;
            int ticks = Environment.TickCount;
            if (Math.Abs(unchecked(ticks - Seed)) <= 2)
            {
                Seed = next++;
                Consumed++;
            }
        }
    }

    /// count: the calls of game methods from now on, through a Harmony prefix on each (README.md).
    /// Added while the game runs, the prefix is what is tested: whether a patch applies to code
    /// compiled before it (ARCHITECTURE D34).
    public static class Counting
    {
        static readonly Dictionary<System.Reflection.MethodBase, int> slots = new Dictionary<System.Reflection.MethodBase, int>();
        static readonly Dictionary<string, int> names = new Dictionary<string, int>();
        static int[] calls = new int[0];

        public static JToken Start(JObject a)
        {
            var methods = a["methods"] as JArray ?? throw new DriverError("count needs methods");
            var before = new JObject();
            foreach (var token in methods)
            {
                var name = (string)token;
                if (!names.TryGetValue(name, out int slot))
                {
                    var method = Resolve(name) ?? throw new DriverError("no method " + name);
                    slot = calls.Length;
                    Array.Resize(ref calls, slot + 1);
                    slots[method] = slot;
                    names[name] = slot;
                    Mod.Harmony.Patch(method, prefix: new HarmonyLib.HarmonyMethod(typeof(Counting), nameof(Prefix)));
                }
                before[name] = System.Threading.Interlocked.Exchange(ref calls[slot], 0);
            }
            return before;
        }

        /// "Type:Method", or "Type:Method(ParamType,...)" for one overload of several (parameter
        /// types by full or short name, as AccessTools.TypeByName takes them).
        internal static System.Reflection.MethodInfo Resolve(string name)
        {
            int open = name.IndexOf('(');
            if (open < 0 || !name.EndsWith(")")) return HarmonyLib.AccessTools.Method(name);
            var inside = name.Substring(open + 1, name.Length - open - 2);
            var types = inside.Length == 0 ? Type.EmptyTypes : inside.Split(',').Select(t =>
                HarmonyLib.AccessTools.TypeByName(t.Trim()) ?? throw new DriverError("no type " + t.Trim() + " in " + name)).ToArray();
            return HarmonyLib.AccessTools.Method(name.Substring(0, open), types);
        }

        /// The calls of a method named in a count, since that count.
        public static int Of(string name) => names.TryGetValue(name, out int slot) ? System.Threading.Volatile.Read(ref calls[slot]) : throw new DriverError("not counted: " + name);

        static void Prefix(System.Reflection.MethodBase __originalMethod)
        {
            if (slots.TryGetValue(__originalMethod, out int slot)) System.Threading.Interlocked.Increment(ref calls[slot]);
        }
    }
}
