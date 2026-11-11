using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PerfProbe
{
    /// TEST INFRASTRUCTURE: `overlays.txt` beside the probe: the overlays it lists (one a line, by
    /// mode name: Oxygen, Power, Temperature, TileMode, Light, LiquidConduit, GasConduit, Decor,
    /// Disease, Crop, HarvestWhenReady, Rooms, Suit, Logic, SolidConveyor, Radiation, Priorities,
    /// ThermalConductivity, HeatFlow, None; `Temperature:MODE` with a temperature filter, as
    /// HeatFlow, RelativeTemperature, AdaptiveTemperature or StateChange), each opened in turn and
    /// held for `hold N` frames (a line of its own; 1200 by default). Each hold starts on a frame
    /// that is a multiple of 600, so the engine's --frame-stats script-time windows (every 600
    /// frames) and the probe's walltime windows line up with it: `PERF overlay NAME from fA to fB`
    /// names the frames, and every 600 frames of the hold `PERF overlay NAME window K: MS ms a
    /// frame` gives the frame time. With `walltime.txt` the method timings run over the same
    /// windows. With `speed.txt` -1 the game stays paused, keeping the sim's variation out of
    /// the overlays' costs (overlays update every frame while paused). A line `capture` saves the
    /// screen 300 frames into each hold, as overlay-NAME.png beside the probe. A line `memory`
    /// logs, after each hold, the process's resident memory, the live render textures (how many,
    /// how many created, an estimate of their bytes) and the multiple render target copies among
    /// them (`PERF overlay memory`): for following what toggling overlays leaves allocated.
    static class Overlays
    {
        /// The sweep has started, and ended (Screens' `wait overlays`).
        public static bool Started, Done;

        /// A colony that reaches a victory imperative on load (2minnow) starts the victory
        /// sequence, which hides the HUD and with it the overlay screen; the sweep skips it, as
        /// does any run with `novictory.txt` (the allocation comparisons: the sequence pauses the
        /// game for its video).
        public static void Install(HarmonyLib.Harmony harmony, string path)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(path, "overlays.txt"))
                && !System.IO.File.Exists(System.IO.Path.Combine(path, "novictory.txt"))) return;
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ColonyAchievementTracker), "BeginVictorySequence"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Overlays), nameof(NoVictorySequence)));
        }

        static bool NoVictorySequence(string achievementID)
        {
            Debug.Log($"PERF victory sequence for {achievementID} skipped");
            return false;
        }

        public static IEnumerator Run(string path, bool wallTime)
        {
            Started = true;
            var lines = System.IO.File.ReadAllLines(path);
            int hold = 1200;
            bool capture = false, memory = false;
            var modes = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("hold ")) { hold = int.Parse(line.Substring(5)); continue; }
                if (line == "capture") { capture = true; continue; }
                if (line == "memory") { memory = true; continue; }
                modes.Add(line);
            }
            // The camera and the load settle first, then the first hold starts on a window edge.
            for (int i = 0; i < 300; i++) yield return null;
            // Dialogs the load raised (mod differences, DLC news) hide the HUD, the overlay bar
            // with it, and the overlay screen updates only while that is active.
            Screens.DismissDialogs();
            for (int i = 0; i < 60; i++) yield return null;
            while (Time.frameCount % 600 != 0) yield return null;
            if (memory) LogMemory(0, "start");
            int held = 0;
            if (wallTime) Runner.Instance.StartCoroutine(WallTime.Run(modes.Count * hold / 600));
            foreach (var entry in modes)
            {
                int colon = entry.IndexOf(':');
                string name = colon < 0 ? entry : entry.Substring(0, colon);
                HashedString id = name == "None" ? OverlayModes.None.ID : new HashedString(name);
                OverlayScreen.Instance.ToggleOverlay(id, false);
                if (colon >= 0) SetTemperatureFilter(entry.Substring(colon + 1));
                else if (name == "Temperature") SetTemperatureFilter("AbsoluteTemperature");
                int start = Time.frameCount;
                var cam = Camera.main;
                Grid.GetVisibleExtents(out Vector2I min, out Vector2I max);
                Debug.Log($"PERF overlay {entry} from f{start} to f{start + hold} (mode {OverlayScreen.Instance.GetMode()}, temperature {Game.Instance.temperatureOverlayMode}; "
                    + $"screen enabled {OverlayScreen.Instance.isActiveAndEnabled}, camera {cam.transform.position} size {cam.orthographicSize}, cells {min}-{max}, paused {SpeedControlScreen.Instance.IsPaused})");
                if (!OverlayScreen.Instance.isActiveAndEnabled)
                {
                    var chain = new System.Text.StringBuilder();
                    for (var t = OverlayScreen.Instance.transform; t != null; t = t.parent) chain.Append($" {t.name}:{t.gameObject.activeSelf}");
                    Debug.Log($"PERF overlay screen inactive: enabled {OverlayScreen.Instance.enabled}, chain{chain}");
                }
                float windowStart = Time.realtimeSinceStartup;
                for (int frame = 1; frame <= hold; frame++)
                {
                    yield return null;
                    if (capture && frame == 300) ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), $"overlay-{entry.Replace(':', '-')}.png"));
                    if (frame % 600 != 0) continue;
                    float now = Time.realtimeSinceStartup;
                    Debug.Log($"PERF overlay {entry} window {frame / 600 - 1} f{Time.frameCount}: {(now - windowStart) * 1000f / 600f:F2} ms a frame");
                    windowStart = now;
                }
                if (memory) LogMemory(++held, entry);
            }
            OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, false);
            Debug.Log("PERF overlay done");
            Done = true;
        }

        static void LogMemory(int n, string entry)
        {
            long rss = 0;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/self/status"))
                    if (line.StartsWith("VmRSS:")) rss = long.Parse(line.Substring(6).Trim().Split(' ')[0]);
            }
            catch (System.Exception) { }
            int created = 0, copies = 0;
            long bytes = 0;
            var textures = Resources.FindObjectsOfTypeAll<RenderTexture>();
            foreach (var rt in textures)
            {
                if (rt.name.StartsWith("MRT") && rt.name.EndsWith("_Copy")) copies++;
                if (!rt.IsCreated()) continue;
                created++;
                int texel = rt.format switch
                {
                    RenderTextureFormat.ARGBHalf or RenderTextureFormat.RGFloat => 8,
                    RenderTextureFormat.ARGBFloat => 16,
                    RenderTextureFormat.RHalf or RenderTextureFormat.R16 => 2,
                    RenderTextureFormat.R8 => 1,
                    RenderTextureFormat.Depth or RenderTextureFormat.Shadowmap => 0,
                    _ => 4,
                };
                bytes += (long)rt.width * rt.height * (texel + (rt.depth > 0 ? 4 : 0));
            }
            Debug.Log($"PERF overlay memory {n} {entry} f{Time.frameCount}: rss {rss / 1024} MB, render textures {textures.Length} ({created} created, about {bytes >> 20} MB), MRT copies {copies}, managed {System.GC.GetTotalMemory(false) >> 20} MB");
        }

        /// The temperature overlay's legend filter, as clicking it does.
        internal static void SetTemperatureFilter(string filter)
        {
            var temperature = OverlayLegendModes().Find(m => m is OverlayModes.Temperature);
            if (temperature?.legendFilters == null) return;
            string layer = filter switch
            {
                "HeatFlow" => ToolParameterMenu.FILTERLAYERS.HEATFLOW,
                "RelativeTemperature" => ToolParameterMenu.FILTERLAYERS.RELATIVETEMPERATURE,
                "AdaptiveTemperature" => ToolParameterMenu.FILTERLAYERS.ADAPTIVETEMPERATURE,
                "StateChange" => ToolParameterMenu.FILTERLAYERS.STATECHANGE,
                _ => ToolParameterMenu.FILTERLAYERS.ABSOLUTETEMPERATURE,
            };
            foreach (var toggle in temperature.legendFilters)
                toggle.state = toggle.name == layer ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off;
            temperature.OnFiltersChanged();
        }

        static List<OverlayModes.Mode> OverlayLegendModes()
        {
            var infos = (IDictionary)HarmonyLib.AccessTools.Field(typeof(OverlayScreen), "modeInfos").GetValue(OverlayScreen.Instance);
            var result = new List<OverlayModes.Mode>();
            foreach (var info in infos.Values)
                result.Add((OverlayModes.Mode)HarmonyLib.AccessTools.Field(info.GetType(), "mode").GetValue(info));
            return result;
        }
    }
}
