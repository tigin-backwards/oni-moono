using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace E2eDriver
{
    /// E2E_GLOBALS_WATCH=1: logs the game's global shader textures as Shader.GetGlobalTexture
    /// returns them, on the frame any of them changes (a line "[E2eDriver] GLOBALS f<frame> ..."
    /// with the screen size, the time scale and each changed global: null, or its type, name,
    /// size, whether a render texture is created, and its instance id, so a replacement shows).
    /// For comparing when each global is set, destroyed or resized in the two players.
    public class GlobalTextures : MonoBehaviour
    {
        static readonly string[] Fixed =
        {
            "_MRT0", "_MRT1", "_MRT2", "MRT0_Copy", "MRT1_Copy", "MRT2_Copy", "_LitTex", "_SimDebugViewTex",
            "_DigDamageMap", "_GenericNormalNoise", "_StateTransitionMap", "_FallingSolidMap", "_DustTex",
            "_HeatHazeTexture", "_BuildingDamagedTex", "_DiseaseOverlayTex", "_LightBufferTex", "_BlurredTemperature",
            "_WorldZoneTex", "_WorldZoneIndexTex",
        };

        readonly Dictionary<string, string> last = new Dictionary<string, string>();
        List<string> names;
        int namedFlow;
        float lastScale = -1f;

        public static void StartIfAsked(GameObject keeper)
        {
            if (Environment.GetEnvironmentVariable("E2E_GLOBALS_WATCH") == "1") keeper.AddComponent<GlobalTextures>();
        }

        List<string> Names()
        {
            var list = new List<string>(Fixed);
            foreach (PropertyTextures.Property p in Enum.GetValues(typeof(PropertyTextures.Property)))
                if (p != PropertyTextures.Property.Num) list.Add("_" + p + "Tex");
            // The flow offset textures' names are serialized in a prefab.
            var flows = FindObjectsByType<FlowOffsetRenderer>(FindObjectsSortMode.None);
            namedFlow = flows.Length;
            foreach (var f in flows)
                if (!string.IsNullOrEmpty(f.OffsetTextureName) && !list.Contains(f.OffsetTextureName)) list.Add(f.OffsetTextureName);
            return list;
        }

        static string State(Texture t)
        {
            if (ReferenceEquals(t, null)) return "null";
            if (t == null) return "destroyed";
            var s = t.GetType().Name + " '" + t.name + "' " + t.width + "x" + t.height;
            if (t is RenderTexture rt) s += rt.IsCreated() ? " created" : " uncreated";
            return s + " #" + t.GetInstanceID();
        }

        void LateUpdate()
        {
            if (names == null || namedFlow == 0 && FindObjectsByType<FlowOffsetRenderer>(FindObjectsSortMode.None).Length > 0) names = Names();
            var sb = new StringBuilder();
            foreach (var n in names)
            {
                var s = State(Shader.GetGlobalTexture(n));
                if (last.TryGetValue(n, out var was) && was == s) continue;
                if (!last.ContainsKey(n) && s == "null") { last[n] = s; continue; }
                last[n] = s;
                sb.Append(' ').Append(n).Append('=').Append(s).Append(';');
            }
            bool scale = Time.timeScale != lastScale;
            if (sb.Length == 0 && !scale) return;
            lastScale = Time.timeScale;
            Debug.Log("[E2eDriver] GLOBALS f" + Time.frameCount + " screen " + Screen.width + "x" + Screen.height + " timeScale " + Time.timeScale
                + " game " + (Game.Instance != null) + sb);
        }
    }
}
