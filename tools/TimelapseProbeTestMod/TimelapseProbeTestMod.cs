using HarmonyLib;
using KMod;
using UnityEngine;

namespace TimelapseProbeTestMod
{
    /// Measures the Timelapser's snapshots (fix-timelapse-snapshots,
    /// tests/e2e/fix-timelapse-snapshots.yaml). For each capture (CameraController
    /// .RenderForTimelapser): what its base camera's render saw (the build grid on or off,
    /// `_WsToCs`, the light buffer camera's aspect), and, read back from the render texture, the
    /// image's stripes: the columns the screen's frame maps left of the grid (x < 0), where the
    /// game's build grid draws its full colour in a stripe a column. Logs a line a capture:
    /// `TimelapseProbe: ...`.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            harmony.Patch(AccessTools.Method(typeof(CameraController), "RenderCameraForTimelapse"), prefix: new HarmonyMethod(typeof(Mod), nameof(CameraPre)));
            harmony.Patch(AccessTools.Method(typeof(CameraController), "RenderForTimelapser"), postfix: new HarmonyMethod(typeof(Mod), nameof(RenderPost)));
            Debug.Log("TimelapseProbe: installed");
        }

        /// Captures since the load: all of them, and the daily snapshots among them (the
        /// Timelapser's own texture, not a save's colony preview). The values below are the last
        /// snapshot's.
        public static int Captures, Snapshots;
        /// At the last capture's base camera render: the build grid's compositor enabled, the
        /// grid x `_WsToCs` maps the image's left column to, the light buffer camera's aspect
        /// and the texture's.
        public static bool GridOn;
        public static float LeftCellX, LightAspect, TextureAspect;
        /// The last capture's frame: its centre's x, orthographic size and the screen's aspect.
        public static float CentreX, Ortho, ScreenAspect;
        /// In the last capture's image: how many columns the screen's frame maps left of the grid
        /// (the band; 0 when it maps none), the mean brightness step from a column to the next
        /// over the step from a row to the next in the band (about 1 for a plain picture, 3 to 5
        /// for the grid's stripes), and the band's mean brightness over the image's.
        public static int BandColumns;
        public static float BandStripes = -1f, BandLift = -1f;
        /// The game's build tool active at the last capture.
        public static bool BuildToolActive;

        const string SnapshotTexture = "Timelapser.Timelapse";

        static void CameraPre(CameraController __instance, Camera cam, RenderTexture tex)
        {
            if (cam != __instance.baseCamera || tex == null || tex.name != SnapshotTexture) return;
            GridOn = GridCompositor.Instance != null && GridCompositor.Instance.enabled;
            Vector4 ws = Shader.GetGlobalVector("_WsToCs");
            LeftCellX = (ws.z + ws.x) * Grid.WidthInCells;
            LightAspect = __instance.lightBufferCamera != null ? __instance.lightBufferCamera.aspect : -1f;
            TextureAspect = (float)tex.width / tex.height;
            CentreX = cam.transform.position.x;
            Ortho = cam.orthographicSize;
            ScreenAspect = (float)Screen.width / Screen.height;
            BuildToolActive = PlayerController.Instance != null && PlayerController.Instance.ActiveTool == BuildTool.Instance && BuildTool.Instance != null;
        }

        static void RenderPost(RenderTexture tex)
        {
            if (tex == null) return;
            Captures++;
            if (tex.name != SnapshotTexture)
            {
                Debug.Log($"TimelapseProbe: capture {Captures}: {tex.name} {tex.width}x{tex.height}, not a snapshot");
                return;
            }
            Snapshots++;
            var active = RenderTexture.active;
            RenderTexture.active = tex;
            var read = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            read.Apply();
            RenderTexture.active = active;
            Measure(read.GetPixels32(), tex.width, tex.height);
            Object.Destroy(read);
            Debug.Log($"TimelapseProbe: capture {Captures}, snapshot {Snapshots}: {tex.width}x{tex.height}, centre x {CentreX:F1}, ortho {Ortho:F1}, screen aspect {ScreenAspect:F3}, "
                + $"grid {(GridOn ? "on" : "off")}, build tool {(BuildToolActive ? "on" : "off")}, left column at cell x {LeftCellX:F1}, light aspect {LightAspect:F3} "
                + $"(texture {TextureAspect:F3}), band {BandColumns} columns, stripes {BandStripes:F2}, lift {BandLift:F1}");
        }

        /// The band: columns u whose x in the screen's frame, centre + (u - 0.5) * 2 * ortho *
        /// screen aspect, is below 0.
        static void Measure(Color32[] pixels, int width, int height)
        {
            float u0 = 0.5f - CentreX / (2f * Ortho * ScreenAspect);
            BandColumns = Mathf.Clamp(Mathf.FloorToInt(u0 * width), 0, width);
            BandStripes = BandLift = -1f;
            // Rows in the middle half; the band less 3 columns at each end.
            int top = height / 4, bottom = 3 * height / 4, left = 3, right = BandColumns - 3;
            if (right - left < 8) return;
            double across = 0, down = 0, band = 0, all = 0;
            int nAcross = 0, nDown = 0, nBand = 0, nAll = 0;
            for (int y = top; y < bottom; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float v = Brightness(pixels[y * width + x]);
                    all += v; nAll++;
                    if (x < left || x >= right) continue;
                    band += v; nBand++;
                    if (x + 1 < right) { across += Mathf.Abs(Brightness(pixels[y * width + x + 1]) - v); nAcross++; }
                    if (y + 1 < bottom) { down += Mathf.Abs(Brightness(pixels[(y + 1) * width + x]) - v); nDown++; }
                }
            }
            BandStripes = (float)(across / nAcross / (down / nDown + 1e-6));
            BandLift = (float)(band / nBand - all / nAll);
        }

        static float Brightness(Color32 c) => (c.r + c.g + c.b) / 3f;
    }
}
