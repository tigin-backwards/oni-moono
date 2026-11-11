using OniMoono.GameTweaks;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// fix-timelapse-snapshots' frame (TimelapseFrame): the snapshot's corners are the ones its
    /// camera draws, and Game.LateUpdate's `_WsToCs` taken for the screen instead maps the left of
    /// a 2:3 snapshot to cells left of the grid (the build grid's stripes).
    public class TimelapseFrameTests : EngineTest
    {
        static Camera SnapshotCamera(Vector3 centre, float ortho, int width, int height)
        {
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            OniMoono.Engine.Assets.ProjectSettings.Load();
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = ortho;
            camera.transform.position = centre;
            camera.targetTexture = new RenderTexture(width, height, 0);
            return camera;
        }

        [Fact]
        public void TheCornersAreTheCamerasViewport()
        {
            var centre = new Vector3(80.5f, 120.25f, -100f);
            var camera = SnapshotCamera(centre, 30f, 512, 768);
            var (topRight, bottomLeft) = TimelapseFrame.Corners(centre, 30f, 512, 768);
            Vector3 tr = camera.ViewportToWorldPoint(new Vector3(1f, 1f, centre.z)), bl = camera.ViewportToWorldPoint(new Vector3(0f, 0f, centre.z));
            Assert.Equal(tr.x, topRight.x, 3);
            Assert.Equal(tr.y, topRight.y, 3);
            Assert.Equal(bl.x, bottomLeft.x, 3);
            Assert.Equal(bl.y, bottomLeft.y, 3);
        }

        [Fact]
        public void TheScreensFrameMapsTheSnapshotsLeftOffTheGrid()
        {
            // A start world at the grid's left edge: the pod at x = 20, the snapshot at its
            // minimum orthographic size, 18; the screen 16:9.
            var centre = new Vector3(20f, 100f, -100f);
            var screen = TimelapseFrame.Corners(centre, 18f, 1280, 720);
            var snapshot = TimelapseFrame.Corners(centre, 18f, 512, 768);
            var screenCells = TimelapseFrame.Cells(screen.TopRight, screen.BottomLeft, 256f, 384f);
            var snapshotCells = TimelapseFrame.Cells(snapshot.TopRight, snapshot.BottomLeft, 256f, 384f);
            // The game's: the snapshot's left column at x = 20 - 18 * 16 / 9, 12 cells left of the grid.
            Assert.Equal(-12f, TimelapseFrame.CellX(screenCells, 0f, 256f), 3);
            // The tweak's: the cells the snapshot shows, 20 - 18 * 2 / 3 to 20 + 12.
            Assert.Equal(8f, TimelapseFrame.CellX(snapshotCells, 0f, 256f), 3);
            Assert.Equal(32f, TimelapseFrame.CellX(snapshotCells, 1f, 256f), 3);
            Assert.Equal(20f, TimelapseFrame.CellX(snapshotCells, 0.5f, 256f), 3);
        }

        [Fact]
        public void TheWorldsUvIsFromItsOffset()
        {
            var (topRight, bottomLeft) = TimelapseFrame.Corners(new Vector3(200f, 150f, -100f), 20f, 512, 768);
            var world = TimelapseFrame.World(topRight, bottomLeft, new Vector2(160f, 100f), new Vector2(128f, 160f), 32f);
            // _WsToCcs.xy is the top right's world UV, zw the frame's extent in it (negative).
            Assert.Equal((200f + 20f * 512f / 768f - 160f) / 128f, world.x, 5);
            Assert.Equal((170f - 100f) / 128f, world.y, 5);
            Assert.Equal(-40f * 512f / 768f / 128f, world.z, 5);
            Assert.Equal(-40f / 128f, world.w, 5);
        }

        [Fact]
        public void ResetAspectGoesBackToTheTargets()
        {
            // The light buffer camera's aspect is set for the snapshot, then reset (TimelapseSnapshots).
            var camera = SnapshotCamera(new Vector3(0f, 0f, -100f), 10f, 1280, 720);
            camera.aspect = 512f / 768f;
            Assert.Equal(512f / 768f, camera.aspect, 5);
            camera.ResetAspect();
            Assert.Equal(1280f / 720f, camera.aspect, 5);
        }
    }
}
