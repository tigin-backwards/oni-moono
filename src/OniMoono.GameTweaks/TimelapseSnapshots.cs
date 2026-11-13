using System;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-timelapse-snapshots` (Bug fixes, on by default, the player's choice; it
    /// changes the images the game writes, so it is left out of `exact`): the Timelapser's
    /// snapshots (the daily colony images in RetiredColonies, and a save's colony preview) drawn
    /// for the snapshot's own frame, not the screen's.
    ///
    /// The Timelapser frames the colony in two frames: at the end of one it moves the cameras to
    /// the snapshot's position and orthographic size and turns the overlay off; at the end of the
    /// next it renders the base and overlay cameras into its render texture
    /// (CameraController.RenderForTimelapser, at the texture's aspect: 2:3 for 512x768) and
    /// writes it. In between, the frame's own LateUpdates set what the post-effects read for the
    /// screen's frame (16:9), not the texture's:
    /// - Game.LateUpdate sets `_WsToCs` and `_WsToCcs` (screen UV to grid and to world UV) from
    ///   Camera.main at the screen's aspect, 2.67 times wider than the snapshot's. The light
    ///   compositor's heat, cold, dust, radiation and out-of-world effects land at the wrong
    ///   cells, and the build grid (GridCompositor) maps the left of the image to cells left of
    ///   the grid, where its fmod keeps the sign and every column draws the full grid colour:
    ///   the vertical stripes, whenever a building is picked for placement (BuildTool and
    ///   PlaceTool turn the grid on; the Timelapser waits only for no drag).
    /// - The light buffer (LightBuffer, its own camera into a screen-sized texture) is drawn
    ///   for the screen's frame and sampled with the snapshot's UV: lights are squeezed 2.67
    ///   times sideways.
    /// The building preview (layer Place, which the snapshot's culling mask keeps) did not show
    /// in the original's snapshots: it is left as it is.
    ///
    /// Here, for the capture's render only, the grid compositor is off and those globals are set
    /// for the snapshot's frame (the base camera's position and orthographic size at the
    /// texture's aspect, with Game.LateUpdate's formulas); in the capture's frame the light
    /// buffer camera draws at the texture's aspect. Each is set back as it was right after the
    /// render, also when it throws. The screen is hidden behind the Timelapser's freeze frame in
    /// that frame, so drawing its lights at the snapshot's aspect changes nothing a player sees.
    /// What differs from the game: the snapshots, and nothing else. U60 reads the texture back
    /// asynchronously after the render; its requests are queued then, so the restore after the
    /// render changes nothing in them.
    [GameMember(GameUse.Prefix | GameUse.Finalizer | GameUse.Mirrors, "System.Void CameraController::RenderForTimelapser(UnityEngine.RenderTexture&)", "ca4c5a42c91bb52b linux-744825 win-744825", "f3eee7968b175f9d linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void CameraController::RenderCameraForTimelapse(UnityEngine.Camera,UnityEngine.RenderTexture&,UnityEngine.LayerMask,System.Single)", "dccdf57307fd4f12 linux-744825 win-744825", "80ddb7d2861897f5 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Prefix, "System.Void LightBuffer::LateUpdate()")]
    [GameMember(GameUse.Uses, "UnityEngine.Camera CameraController::baseCamera")]
    [GameMember(GameUse.Uses, "UnityEngine.Camera CameraController::lightBufferCamera")]
    [GameMember(GameUse.Uses, "GridCompositor GridCompositor::Instance")]
    [GameMember(GameUse.Uses, "Timelapser Game::timelapser")]
    [GameMember(GameUse.Uses, "System.Boolean Timelapser::screenshotActive")]
    [GameMember(GameUse.Uses, "UnityEngine.RenderTexture Timelapser::bufferRenderTexture")]
    [GameMember(GameUse.Mirrors, "System.Void Game::LateUpdate()", "ddef0e828352db67 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761654 win-762107 win-762230 win-763023")]
    public static class TimelapseSnapshots
    {
        public const string Name = "fix-timelapse-snapshots";

        static readonly int WsToCs = Shader.PropertyToID("_WsToCs"), WsToCcs = Shader.PropertyToID("_WsToCcs");

        /// The light buffer camera's aspect before LightBuffer.LateUpdate's prefix set the
        /// snapshot's, or NaN while it is the game's own.
        static float lightAspect = float.NaN;
        static Camera lightCamera;

        public static void Install(Harmony harmony)
        {
            var render = AccessTools.Method(typeof(CameraController), nameof(CameraController.RenderForTimelapser));
            harmony.Patch(render,
                prefix: new HarmonyMethod(typeof(TimelapseSnapshots), nameof(RenderPrefix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(TimelapseSnapshots), nameof(RenderFinalizer)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(LightBuffer), "LateUpdate"),
                prefix: new HarmonyMethod(typeof(TimelapseSnapshots), nameof(LightBufferPrefix)));
        }

        /// What the capture's render changed, to set back after it.
        public struct Saved
        {
            public bool Set;
            public Vector4 WsToCs, WsToCcs;
            public GridCompositor Grid;
            public bool GridEnabled;
        }

        [MirrorsGame("System.Void CameraController::RenderForTimelapser(UnityEngine.RenderTexture&)")]
        static void RenderPrefix(CameraController __instance, ref RenderTexture tex, out Saved __state)
        {
            __state = default;
            var camera = __instance.baseCamera;
            if (tex == null || camera == null || !camera.orthographic || tex.height <= 0) return;
            __state.Set = true;
            __state.WsToCs = Shader.GetGlobalVector(WsToCs);
            __state.WsToCcs = Shader.GetGlobalVector(WsToCcs);
            __state.Grid = GridCompositor.Instance;
            if (__state.Grid != null)
            {
                __state.GridEnabled = __state.Grid.enabled;
                __state.Grid.enabled = false;
            }
            Vector3 centre = camera.transform.position;
            var (topRight, bottomLeft) = TimelapseFrame.Corners(centre, camera.orthographicSize, tex.width, tex.height);
            Shader.SetGlobalVector(WsToCs, TimelapseFrame.Cells(topRight, bottomLeft, Grid.WidthInCells, Grid.HeightInCells));
            var world = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorld : null;
            if (world != null)
            {
                Shader.SetGlobalVector(WsToCcs, TimelapseFrame.World(topRight, bottomLeft, new Vector2(world.WorldOffset.x, world.WorldOffset.y),
                    new Vector2(world.WorldSize.x, world.WorldSize.y), world.HiddenYOffset));
            }
        }

        static Exception RenderFinalizer(Exception __exception, Saved __state)
        {
            if (__state.Set)
            {
                Shader.SetGlobalVector(WsToCs, __state.WsToCs);
                Shader.SetGlobalVector(WsToCcs, __state.WsToCcs);
                if (__state.Grid != null) __state.Grid.enabled = __state.GridEnabled;
            }
            RestoreLightAspect();
            return __exception;
        }

        /// In the frame the Timelapser captures in, the light buffer camera draws at the
        /// snapshot's aspect (the frame's screen is hidden behind the Timelapser's freeze frame).
        static void LightBufferPrefix()
        {
            var timelapser = Game.Instance != null ? Game.Instance.timelapser : null;
            var controller = CameraController.Instance;
            var target = timelapser != null && timelapser.screenshotActive ? timelapser.bufferRenderTexture : null;
            if (target == null || controller == null || controller.lightBufferCamera == null || target.height <= 0)
            {
                RestoreLightAspect();
                return;
            }
            var camera = controller.lightBufferCamera;
            if (float.IsNaN(lightAspect))
            {
                lightCamera = camera;
                lightAspect = camera.aspect;
            }
            camera.aspect = (float)target.width / target.height;
        }

        /// The light buffer camera's own aspect again: the automatic one (the screen's), or the
        /// one set on it before, when that differs.
        static void RestoreLightAspect()
        {
            if (float.IsNaN(lightAspect)) return;
            if (lightCamera != null)
            {
                lightCamera.ResetAspect();
                if (lightCamera.aspect != lightAspect) lightCamera.aspect = lightAspect;
            }
            lightCamera = null;
            lightAspect = float.NaN;
        }
    }
}
