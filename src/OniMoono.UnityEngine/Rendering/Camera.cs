using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Platform;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public partial class Camera
    {
        public delegate void CameraCallback(Camera cam);

        public static CameraCallback onPreCull;
        public static CameraCallback onPreRender;
        public static CameraCallback onPostRender;

        public Camera() { }

        internal CameraNative Data => (CameraNative)NativeOrThrow;

        internal override void EngineOnEnable() => Cameras.Add(this);
        internal override void EngineOnDisable() => Cameras.Remove(this);

        // ----- Settings -----

        public float nearClipPlane { get => Data.Near; set => Data.Near = value; }
        public float farClipPlane { get => Data.Far; set => Data.Far = value; }

        /// Vertical field of view in degrees, clamped to [1e-5, 179].
        public float fieldOfView
        {
            get => Data.FieldOfView;
            set => Data.FieldOfView = Mathf.Clamp(value, 1e-5f, 179f);
        }

        public bool orthographic { get => Data.Orthographic; set => Data.Orthographic = value; }
        public float orthographicSize { get => Data.OrthographicSize; set => Data.OrthographicSize = value; }
        public float depth { get => Data.Depth; set => Data.Depth = value; }
        public int cullingMask { get => Data.CullingMask; set => Data.CullingMask = value; }
        public int eventMask { get => Data.EventMask; set => Data.EventMask = value; }
        public Color backgroundColor { get => Data.BackgroundColor; set => Data.BackgroundColor = value; }
        public CameraClearFlags clearFlags { get => Data.ClearFlags; set => Data.ClearFlags = value; }
        public DepthTextureMode depthTextureMode { get => Data.DepthTextureMode; set => Data.DepthTextureMode = value; }
        public RenderingPath renderingPath { get => Data.RenderingPath; set => Data.RenderingPath = value; }
        public bool allowHDR { get => Data.AllowHDR; set => Data.AllowHDR = value; }
        public bool allowMSAA { get => Data.AllowMSAA; set => Data.AllowMSAA = value; }
        public bool useOcclusionCulling { get => Data.OcclusionCulling; set => Data.OcclusionCulling = value; }
        public TransparencySortMode transparencySortMode { get => Data.TransparencySortMode; set => Data.TransparencySortMode = value; }
        public Vector3 transparencySortAxis { get => Data.TransparencySortAxis; set => Data.TransparencySortAxis = value; }

        public void ResetTransparencySortSettings()
        {
            var d = Data;
            d.TransparencySortMode = TransparencySortMode.Default;
            d.TransparencySortAxis = new Vector3(0f, 0f, 1f);
        }

        public int targetDisplay { get => Data.TargetDisplay; set => Data.TargetDisplay = value; }

        /// Rendering into a texture; assigning one clears target buffers set with SetTargetBuffers.
        public RenderTexture targetTexture
        {
            get => Data.TargetTexture;
            set
            {
                var d = Data;
                d.TargetTexture = value;
                d.TargetColorBuffers = null;
            }
        }

        public void SetTargetBuffers(RenderBuffer colorBuffer, RenderBuffer depthBuffer) => SetTargetBuffers([colorBuffer], depthBuffer);

        /// Renders into the given surfaces (multiple render targets) instead of targetTexture.
        public void SetTargetBuffers(RenderBuffer[] colorBuffer, RenderBuffer depthBuffer)
        {
            if (colorBuffer == null) throw new ArgumentNullException(nameof(colorBuffer));
            var d = Data;
            d.TargetColorBuffers = (RenderBuffer[])colorBuffer.Clone();
            d.TargetDepthBuffer = depthBuffer;
        }

        // ----- Viewport -----

        /// Normalized viewport rectangle, kept as set (out of range and NaN too); pixelRect clips
        /// it to the target.
        public Rect rect
        {
            get => Data.Rect;
            set => Data.Rect = value;
        }

        public Rect pixelRect
        {
            get => Data.PixelRect;
            set
            {
                // times the reciprocal, as the original (21.5 / 768 an ulp over the quotient)
                var size = Data.TargetSize;
                float kx = 1f / size.x, ky = 1f / size.y;
                rect = new Rect(value.x * kx, value.y * ky, value.width * kx, value.height * ky);
            }
        }

        public int pixelWidth => Data.PixelWidth;
        public int pixelHeight => Data.PixelHeight;
        public int scaledPixelWidth => Data.PixelWidth;
        public int scaledPixelHeight => Data.PixelHeight;

        /// Width over height of the viewport unless set explicitly (until ResetAspect).
        public float aspect
        {
            get => Data.Aspect;
            set => Data.AspectOverride = value;
        }

        public void ResetAspect() => Data.AspectOverride = null;

        // ----- Matrices -----

        /// OpenGL convention: the camera looks down -Z of its view space.
        public Matrix4x4 worldToCameraMatrix
        {
            get => Data.WorldToCamera;
            set => Data.WorldToCameraOverride = value;
        }

        public Matrix4x4 cameraToWorldMatrix => Data.WorldToCamera.inverse;

        public Matrix4x4 projectionMatrix
        {
            get => Data.Projection;
            set => Data.ProjectionOverride = value;
        }

        public void ResetWorldToCameraMatrix() => Data.WorldToCameraOverride = null;
        public void ResetProjectionMatrix() => Data.ProjectionOverride = null;

        /// What renderers are culled against: projection × world-to-camera, until set; a set
        /// matrix stays as set (the camera moving or resizing does not change it) until
        /// ResetCullingMatrix (measured in the original, UnityProbe's apigaps "misc").
        public Matrix4x4 cullingMatrix
        {
            get => Data.CullingOverride ?? Data.Projection * Data.WorldToCamera;
            set => Data.CullingOverride = value;
        }

        public void ResetCullingMatrix() => Data.CullingOverride = null;

        // ----- Coordinate conversions -----
        // The original's, bit for bit (tools/UnityProbe PROBE_SCENARIO=projection, pj-check.py
        // camera: 1,440 points of 240 cameras), but for the rays' perspective direction
        // (CameraNative.ScreenRay). The screen side maps through the pixel rect rounded to whole
        // pixels (CameraNative.PixelViewport), the viewport side through the pixel rect itself.

        /// Screen pixels to viewport (0..1 across the camera's pixel rect); z unchanged.
        public Vector3 ScreenToViewportPoint(Vector3 position)
        {
            var r = Data.PixelRect;
            return new Vector3((position.x - r.x) / r.width, (position.y - r.y) / r.height, position.z);
        }

        public Vector3 ViewportToScreenPoint(Vector3 position)
        {
            var r = Data.PixelRect;
            return new Vector3(position.x * r.width + r.x, position.y * r.height + r.y, position.z);
        }

        /// Viewport xy, z = distance in front of the camera along its view axis.
        public Vector3 WorldToViewportPoint(Vector3 position)
        {
            var d = Data;
            var s = d.WorldToScreen(position);
            var r = d.PixelRect;
            return new Vector3((s.x - r.x) / r.width, (s.y - r.y) / r.height, s.z);
        }

        public Vector3 WorldToScreenPoint(Vector3 position) => Data.WorldToScreen(position);

        /// The world point at viewport xy whose distance in front of the camera is z.
        public Vector3 ViewportToWorldPoint(Vector3 position) => Data.ScreenToWorld(ViewportToScreenPoint(position));

        public Vector3 ScreenToWorldPoint(Vector3 position) => Data.ScreenToWorld(position);

        /// From the near-plane point under the viewport position, away from the camera.
        public Ray ViewportPointToRay(Vector3 pos) => Data.ScreenRay(ViewportToScreenPoint(pos));

        public Ray ScreenPointToRay(Vector3 pos) => Data.ScreenRay(pos);

        // ----- Cameras -----

        /// The first enabled camera tagged "MainCamera".
        public static Camera main => Cameras.Main;

        /// The camera being rendered (null outside rendering).
        public static Camera current => FrameRenderer.Current;

        public static int allCamerasCount => Cameras.Enabled.Count;

        public static Camera[] allCameras => Cameras.Enabled.ToArray();

        public static int GetAllCameras(Camera[] cameras)
        {
            if (cameras == null) throw new NullReferenceException();
            if (cameras.Length < allCamerasCount) throw new ArgumentException("Passed in array to fill with cameras is to small to hold the number of cameras. Use Camera.allCamerasCount to get the needed size.");
            Cameras.Enabled.CopyTo(cameras);
            return allCamerasCount;
        }

        /// Copies every camera setting, the transform's position/rotation/scale and the layer.
        public void CopyFrom(Camera other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            Data.CopySettings(other.Data);
            var t = transform;
            var o = other.transform;
            t.position = o.position;
            t.rotation = o.rotation;
            t.localScale = o.localScale;
            gameObject.layer = other.gameObject.layer;
        }

        /// Renders the camera now, even when disabled.
        public void Render()
        {
            Threads.AssertMainThread("Render");
            FrameRenderer.RenderCamera(this);
        }

        // ----- Command buffers -----

        public void AddCommandBuffer(CameraEvent evt, CommandBuffer buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            var d = Data;
            if (!d.CommandBuffers.TryGetValue(evt, out var list)) d.CommandBuffers[evt] = list = new List<CommandBuffer>();
            list.Add(buffer);
        }

        public void RemoveCommandBuffer(CameraEvent evt, CommandBuffer buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (Data.CommandBuffers.TryGetValue(evt, out var list)) list.RemoveAll(b => b == buffer);
        }

        public void RemoveCommandBuffers(CameraEvent evt) => Data.CommandBuffers.Remove(evt);

        public void RemoveAllCommandBuffers() => Data.CommandBuffers.Clear();

        public CommandBuffer[] GetCommandBuffers(CameraEvent evt)
            => Data.CommandBuffers.TryGetValue(evt, out var list) ? list.ToArray() : Array.Empty<CommandBuffer>();

        public int commandBufferCount
        {
            get
            {
                int n = 0;
                foreach (var l in Data.CommandBuffers.Values) n += l.Count;
                return n;
            }
        }
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;

    internal sealed class CameraNative : BehaviourNative
    {
        public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
        public Color BackgroundColor = new(0.19215687f, 0.3019608f, 0.4745098f, 0f);
        public Rect Rect = new(0f, 0f, 1f, 1f);
        public float Near = 0.3f;
        public float Far = 1000f;
        public float FieldOfView = 60f;
        public bool Orthographic;
        public float OrthographicSize = 5f;
        public float Depth;
        public int CullingMask = -1;
        public int EventMask = -1;
        public RenderingPath RenderingPath = RenderingPath.UsePlayerSettings;
        public RenderTexture TargetTexture;
        public int TargetDisplay;
        public bool AllowHDR = true;
        public bool AllowMSAA = true;
        public bool OcclusionCulling = true;
        public DepthTextureMode DepthTextureMode;
        public TransparencySortMode TransparencySortMode;
        public Vector3 TransparencySortAxis = new(0f, 0f, 1f);
        public float? AspectOverride;
        public Matrix4x4? WorldToCameraOverride;
        public Matrix4x4? ProjectionOverride;
        public Matrix4x4? CullingOverride;
        public RenderBuffer[] TargetColorBuffers;
        public RenderBuffer TargetDepthBuffer;
        public readonly Dictionary<UnityEngine.Rendering.CameraEvent, List<UnityEngine.Rendering.CommandBuffer>> CommandBuffers = new();

        /// Size of what the camera renders into: its target texture, the first of its target
        /// buffers (SetTargetBuffers), else the screen.
        public Vector2 TargetSize
        {
            get
            {
                if (TargetTexture != null) return new Vector2(TargetTexture.width, TargetTexture.height);
                if (TargetColorBuffers is { Length: > 0 }
                    && OniMoono.Engine.ObjectTable.FindByInstanceId(TargetColorBuffers[0].m_RenderTextureInstanceID) is OniMoono.Engine.Rendering.RenderTextureNative rt)
                    return new Vector2(rt.Width, rt.Height);
                var p = PlatformServices.Current;
                return new Vector2(p.FramebufferWidth, p.FramebufferHeight);
            }
        }

        /// The normalized rect scaled to the target and clipped to it, as the original (240
        /// random rects, and 80 rule cases out of range, NaN, at half pixels and on a target
        /// texture; PROBE_SCENARIO=projection): per axis the start is 0 + x·W and the end start + w·W,
        /// each clamped to [0, W] by comparisons that pass NaN through, and the size end − start,
        /// at least 0.
        public Rect PixelRect
        {
            get
            {
                var s = TargetSize;
                Clip(Rect.x, Rect.width, s.x, out float x, out float w);
                Clip(Rect.y, Rect.height, s.y, out float y, out float h);
                return new Rect(x, y, w, h);
            }
        }

        static void Clip(float start, float size, float target, out float pixelStart, out float pixelSize)
        {
            float a = 0f + start * target, b = a + size * target;
            a = Min(Max(a, 0f), target);
            b = Min(Max(b, 0f), target);
            pixelStart = a;
            pixelSize = Max(b - a, 0f);
        }

        // std::min and std::max: a NaN first argument passes through.
        static float Min(float a, float b) => b < a ? b : a;
        static float Max(float a, float b) => a < b ? b : a;

        /// The pixel rect in whole pixels: the start rounded half up, the end (start + size)
        /// rounded and clamped to the target, the size their difference (so a NaN start gives
        /// int.MinValue, a NaN size 0). pixelWidth and pixelHeight are its size, and the screen
        /// conversions map through it.
        public (int X, int Y, int Width, int Height) PixelViewport
        {
            get
            {
                var r = PixelRect;
                var s = TargetSize;
                int x0 = Round(r.x), y0 = Round(r.y);
                int x1 = Math.Clamp(Round(r.x + r.width), 0, (int)s.x), y1 = Math.Clamp(Round(r.y + r.height), 0, (int)s.y);
                return (x0, y0, unchecked(x1 - x0), unchecked(y1 - y0));
            }
        }

        static int Round(float v) => float.IsNaN(v) ? int.MinValue : (int)MathF.Floor(v + 0.5f);

        public int PixelWidth => PixelViewport.Width;
        public int PixelHeight => PixelViewport.Height;

        /// The set aspect until ResetAspect (any value, kept through rect and projection
        /// changes), else the pixel rect's width over height, 1 for a zero height.
        public float Aspect
        {
            get
            {
                if (AspectOverride is { } a) return a;
                var r = PixelRect;
                return r.height == 0f ? 1f : r.width / r.height;
            }
        }

        /// The original's, bit for bit (240 random cameras, 7 poses and a child of a rotated,
        /// mirrored parent): the world rotation's matrix as the hierarchy builds it
        /// (HierarchyMath), unscaled and transposed; each row's translation
        /// 0 − (r0·p.x + (r1·p.y + r2·p.z)) with p the world position; the third row then
        /// subtracted from 0 (view space looks down −Z), which keeps zeros positive.
        public Matrix4x4 WorldToCamera
        {
            get
            {
                if (WorldToCameraOverride is { } m) return m;
                var t = GameObject.Transform;
                var p = Hierarchy.Position(t);
                var r = HierarchyMath.Trs(Vector3.zero, t.WorldRotation, Vector3.one);
                Matrix4x4 v;
                v.m00 = r.M00; v.m01 = r.M10; v.m02 = r.M20;
                v.m03 = 0f - (r.M00 * p.x + (r.M10 * p.y + r.M20 * p.z));
                v.m10 = r.M01; v.m11 = r.M11; v.m12 = r.M21;
                v.m13 = 0f - (r.M01 * p.x + (r.M11 * p.y + r.M21 * p.z));
                v.m20 = 0f - r.M02; v.m21 = 0f - r.M12; v.m22 = 0f - r.M22;
                v.m23 = 0f - (0f - (r.M02 * p.x + (r.M12 * p.y + r.M22 * p.z)));
                v.m30 = 0f; v.m31 = 0f; v.m32 = 0f; v.m33 = 1f;
                return v;
            }
        }

        public Matrix4x4 Projection
        {
            get
            {
                if (ProjectionOverride is { } m) return m;
                float aspect = Aspect;
                return Orthographic
                    ? Matrix4x4.Ortho(-OrthographicSize * aspect, OrthographicSize * aspect, -OrthographicSize, OrthographicSize, Near, Far)
                    : Matrix4x4.Perspective(FieldOfView, aspect, Near, Far);
            }
        }

        // ----- Screen conversions (measured; every operation rounds to float) -----

        /// a · b, each element summed left to right in float (not Matrix4x4's product, which sums
        /// in double as the managed one does).
        static Matrix4x4 MulFloat(in Matrix4x4 a, in Matrix4x4 b)
        {
            Matrix4x4 r;
            r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
            r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
            r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
            r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
            r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
            r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
            r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
            r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
            r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
            r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
            r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
            r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
            r.m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
            r.m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
            r.m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
            r.m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
            return r;
        }

        static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        // The conversions' matrices, kept while the view and projection stay the same to the bit
        // (each call would otherwise multiply them and invert twice).
        Matrix4x4 cachedView, cachedProjection, cachedViewProjection, cachedInverseViewProjection, cachedCameraToWorld;
        bool cached;

        internal static bool SameBits(in Matrix4x4 a, in Matrix4x4 b)
            => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in a), 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in b), 1)));

        /// What the world-to-screen conversion reads (WorldToScreen): the view, the projection
        /// and the whole-pixel viewport. A raycast's rect tests stand while they do
        /// (UnityEngine.UI.GraphicRaycaster).
        internal struct ScreenMapping
        {
            public Matrix4x4 View, Projection;
            public (int X, int Y, int Width, int Height) Viewport;

            public readonly bool Same(in ScreenMapping o) => Viewport == o.Viewport && SameBits(View, o.View) && SameBits(Projection, o.Projection);
        }

        internal ScreenMapping Mapping => new ScreenMapping { View = WorldToCamera, Projection = Projection, Viewport = PixelViewport };

        void Matrices()
        {
            var view = WorldToCamera;
            var projection = Projection;
            if (cached && SameBits(view, cachedView) && SameBits(projection, cachedProjection)) return;
            cachedView = view;
            cachedProjection = projection;
            cachedViewProjection = MulFloat(projection, view);
            if (!MesaInverse.Invert(cachedViewProjection, out cachedInverseViewProjection)) cachedInverseViewProjection = Matrix4x4.zero;
            if (!MesaInverse.Invert(view, out cachedCameraToWorld)) cachedCameraToWorld = Matrix4x4.zero;
            cached = true;
        }

        /// The camera's position and forward axis as its cameraToWorldMatrix (the inverse of
        /// WorldToCamera) has them: the translation, and column 2 negated.
        static void CameraAxes(in Matrix4x4 c2w, out Vector3 position, out Vector3 forward)
        {
            position = new Vector3(c2w.m03, c2w.m13, c2w.m23);
            forward = new Vector3(-c2w.m02, -c2w.m12, -c2w.m22);
        }

        /// (P · V) · (p, 1) times 1 / w, from [−1, 1] onto the whole-pixel viewport; z is the
        /// distance along the camera's forward axis from its position.
        public Vector3 WorldToScreen(Vector3 p)
        {
            Matrices();
            ref readonly var vp = ref cachedViewProjection;
            float cx = vp.m00 * p.x + vp.m01 * p.y + vp.m02 * p.z + vp.m03;
            float cy = vp.m10 * p.x + vp.m11 * p.y + vp.m12 * p.z + vp.m13;
            float cw = vp.m30 * p.x + vp.m31 * p.y + vp.m32 * p.z + vp.m33;
            float k = 1f / cw;
            var px = PixelViewport;
            float sx = (cx * k + 1f) * 0.5f * px.Width + px.X;
            float sy = (cy * k + 1f) * 0.5f * px.Height + px.Y;
            CameraAxes(cachedCameraToWorld, out var pos, out var fwd);
            float z = -Dot(new Vector3(p.x - pos.x, p.y - pos.y, p.z - pos.z), -fwd);
            return new Vector3(sx, sy, z);
        }

        /// The screen point's clip-space xy at depth 0.95 (found by searching the depth) through
        /// the inverse of P · V: that point, its offset d from the camera and d's length along
        /// the forward axis. False, as the original gives up, when that length is under 1e-6 or
        /// NaN (the boundary found by bisection on the clip planes).
        bool Unproject(Vector3 s, out Vector3 point, out Vector3 pos, out Vector3 fwd, out Vector3 d, out float dist)
        {
            Matrices();
            ref readonly var inv = ref cachedInverseViewProjection;
            var px = PixelViewport;
            float nx = (s.x - px.X) * 2f / px.Width - 1f;
            float ny = (s.y - px.Y) * 2f / px.Height - 1f;
            const float nz = 0.95f;
            float k = 1f / (inv.m30 * nx + inv.m31 * ny + inv.m32 * nz + inv.m33);
            point = new Vector3(
                (inv.m00 * nx + inv.m01 * ny + inv.m02 * nz + inv.m03) * k,
                (inv.m10 * nx + inv.m11 * ny + inv.m12 * nz + inv.m13) * k,
                (inv.m20 * nx + inv.m21 * ny + inv.m22 * nz + inv.m23) * k);
            CameraAxes(cachedCameraToWorld, out pos, out fwd);
            d = new Vector3(point.x - pos.x, point.y - pos.y, point.z - pos.z);
            dist = Dot(d, fwd);
            return MathF.Abs(dist) >= 1e-6f;
        }

        /// The unprojected point moved to distance z along the forward axis: for perspective
        /// along the line from the camera, for orthographic straight.
        Vector3 AtDistance(in Vector3 point, in Vector3 pos, in Vector3 fwd, in Vector3 d, float dist, float z)
        {
            if (Orthographic)
            {
                float back = dist - z;
                return new Vector3(point.x - fwd.x * back, point.y - fwd.y * back, point.z - fwd.z * back);
            }
            float f = z / dist;
            return new Vector3(pos.x + d.x * f, pos.y + d.y * f, pos.z + d.z * f);
        }

        /// Zero where the unprojection gives up.
        public Vector3 ScreenToWorld(Vector3 s)
            => Unproject(s, out var point, out var pos, out var fwd, out var d, out float dist)
                ? AtDistance(point, pos, fwd, d, dist, s.z) : Vector3.zero;

        /// From the screen point on the near plane. Orthographic: along the forward axis,
        /// normalized (the original's, bit for bit). Perspective: toward the screen point at
        /// distance 1000, normalized; the original's direction is within an ulp or two of that
        /// but not reproduced (175 of 720 rays; the other forms tried matched fewer). Where the
        /// unprojection gives up, the camera's position and (0, 0, 1), as the original.
        public Ray ScreenRay(Vector3 s)
        {
            if (!Unproject(s, out var point, out var pos, out var fwd, out var d, out float dist))
                return Ray.Raw(pos, new Vector3(0f, 0f, 1f));
            var origin = AtDistance(point, pos, fwd, d, dist, Near);
            Vector3 dir = fwd;
            if (!Orthographic)
            {
                var far = AtDistance(point, pos, fwd, d, dist, 1000f);
                dir = new Vector3(far.x - pos.x, far.y - pos.y, far.z - pos.z);
            }
            float m = MathF.Sqrt(Dot(dir, dir));
            return Ray.Raw(origin, new Vector3(dir.x / m, dir.y / m, dir.z / m));
        }

        /// World points on the near and far planes under a viewport position.
        public (Vector3 Near, Vector3 Far) ViewportSegment(float vx, float vy)
        {
            var inverse = (Projection * WorldToCamera).inverse;
            float x = vx * 2f - 1f, y = vy * 2f - 1f;
            return (Unproject(inverse, x, y, -1f), Unproject(inverse, x, y, 1f));
        }

        static Vector3 Unproject(Matrix4x4 inverseViewProjection, float x, float y, float z)
        {
            var p = inverseViewProjection * new Vector4(x, y, z, 1f);
            return new Vector3(p.x, p.y, p.z) / p.w;
        }

        public void CopySettings(CameraNative o)
        {
            ClearFlags = o.ClearFlags;
            BackgroundColor = o.BackgroundColor;
            Rect = o.Rect;
            Near = o.Near;
            Far = o.Far;
            FieldOfView = o.FieldOfView;
            Orthographic = o.Orthographic;
            OrthographicSize = o.OrthographicSize;
            Depth = o.Depth;
            CullingMask = o.CullingMask;
            EventMask = o.EventMask;
            RenderingPath = o.RenderingPath;
            TargetTexture = o.TargetTexture;
            TargetDisplay = o.TargetDisplay;
            AllowHDR = o.AllowHDR;
            AllowMSAA = o.AllowMSAA;
            OcclusionCulling = o.OcclusionCulling;
            DepthTextureMode = o.DepthTextureMode;
            TransparencySortMode = o.TransparencySortMode;
            TransparencySortAxis = o.TransparencySortAxis;
            AspectOverride = o.AspectOverride;
            WorldToCameraOverride = o.WorldToCameraOverride;
            ProjectionOverride = o.ProjectionOverride;
            CullingOverride = o.CullingOverride;
            TargetColorBuffers = (RenderBuffer[])o.TargetColorBuffers?.Clone();
            TargetDepthBuffer = o.TargetDepthBuffer;
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            CopySettings((CameraNative)source);
        }

        /// Camera (class 20).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            ClearFlags = (CameraClearFlags)f.Get<uint>("m_ClearFlags");
            var c = f.Struct("m_BackGroundColor");
            BackgroundColor = new Color(c.Float("r"), c.Float("g"), c.Float("b"), c.Float("a"));
            var r = f.Struct("m_NormalizedViewPortRect");
            Rect = new Rect(r.Float("x"), r.Float("y"), r.Float("width"), r.Float("height"));
            Near = f.Float("near clip plane");
            Far = f.Float("far clip plane");
            FieldOfView = f.Float("field of view");
            Orthographic = f.Bool("orthographic");
            OrthographicSize = f.Float("orthographic size");
            Depth = f.Float("m_Depth");
            CullingMask = (int)f.Struct("m_CullingMask").Get<uint>("m_Bits");
            RenderingPath = (RenderingPath)f.Int("m_RenderingPath");
            TargetTexture = resolver.Resolve(f.PPtr("m_TargetTexture")) as RenderTexture;
            TargetDisplay = f.Int("m_TargetDisplay");
            AllowHDR = f.Bool("m_HDR");
            AllowMSAA = f.Bool("m_AllowMSAA");
            OcclusionCulling = f.Bool("m_OcclusionCulling");
        }
    }

    /// Enabled cameras in the order they were enabled.
    internal static class Cameras
    {
        public static readonly List<Camera> Enabled = new();

        public static void Add(Camera c)
        {
            if (!Enabled.Contains(c)) Enabled.Add(c);
        }

        public static void Remove(Camera c) => Enabled.Remove(c);

        public static Camera Main
        {
            get
            {
                foreach (var c in Enabled)
                    if (c.Native is CameraNative n && n.GameObject.Tag == "MainCamera") return c;
                return null;
            }
        }
    }
}
