using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// fix-timelapse-snapshots' frame arithmetic (TimelapseSnapshots): plain Unity math with no
    /// game types, so the tests compile it in.
    public static class TimelapseFrame
    {
        /// The top-right and bottom-left world corners of an orthographic camera at `centre`
        /// with half-height `orthographicSize`, drawing into a `width` by `height` target (as
        /// Camera.ViewportToWorldPoint gives them at (1, 1) and (0, 0) for an unrotated camera).
        public static (Vector2 TopRight, Vector2 BottomLeft) Corners(Vector3 centre, float orthographicSize, int width, int height)
        {
            float halfWidth = orthographicSize * width / height;
            return (new Vector2(centre.x + halfWidth, centre.y + orthographicSize), new Vector2(centre.x - halfWidth, centre.y - orthographicSize));
        }

        /// Game.LateUpdate's `_WsToCs` for a frame with those corners: screen UV (1 - uv, top
        /// right first) to the grid's UV, on a grid of `gridWidth` by `gridHeight` cells.
        public static Vector4 Cells(Vector2 topRight, Vector2 bottomLeft, float gridWidth, float gridHeight) =>
            new(topRight.x / gridWidth, topRight.y / gridHeight, (bottomLeft.x - topRight.x) / gridWidth, (bottomLeft.y - topRight.y) / gridHeight);

        /// Game.LateUpdate's `_WsToCcs`: the same to the active world's UV, the world at `offset`
        /// with `size` cells, of which the top `hiddenYOffset` rows are not shown.
        public static Vector4 World(Vector2 topRight, Vector2 bottomLeft, Vector2 offset, Vector2 size, float hiddenYOffset)
        {
            float height = size.y - hiddenYOffset;
            return new Vector4((topRight.x - offset.x) / size.x, (topRight.y - offset.y) / height, (bottomLeft.x - topRight.x) / size.x, (bottomLeft.y - topRight.y) / height);
        }

        /// The grid x a frame's `_WsToCs` maps a target's horizontal UV `u` to (what the build
        /// grid and the light compositor compute per pixel), on a grid `gridWidth` cells wide.
        public static float CellX(Vector4 wsToCs, float u, float gridWidth) => (wsToCs.z * (1f - u) + wsToCs.x) * gridWidth;
    }
}
