using System;
using System.Collections.Generic;
using OniMoono.Engine.Video;
using UnityEngine;

namespace OniMoono.Engine.Rendering
{
    /// VideoPlayer output the renderer draws: frames copied into target render textures, and
    /// camera-plane videos (Unity's "Hidden/VideoComposite": _MainTex blended by _AlphaParam).
    internal static class VideoPlanes
    {
        static readonly int MainTex = ShaderPropertyIds.Get("_MainTex");
        static readonly int AlphaParam = ShaderPropertyIds.Get("_AlphaParam");
        /// The frame texture update last copied, per frame texture (weak: the textures belong to
        /// their players).
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, System.Runtime.CompilerServices.StrongBox<uint>> copied = new();
        static Material composite;
        static MeshNative quad;

        static Material Composite => composite != null ? composite : composite = new Material(Shader.Find("Hidden/VideoComposite"));

        /// Copies each RenderTexture-mode player's newly presented frame into its target, fitted
        /// per its aspect mode over a black background.
        public static void CopyRenderTextureVideos()
        {
            foreach (var p in VideoSystem.Players)
            {
                if (p.RenderMode != VideoRenderTarget.RenderTexture || p.TargetTexture == null) continue;
                var frame = VideoOutputs.RenderTextureContent(p.TargetTexture, out var aspect);
                if (frame == null) continue;
                var copiedUpdate = copied.GetValue(frame, _ => new System.Runtime.CompilerServices.StrongBox<uint>(uint.MaxValue));
                if (copiedUpdate.Value == frame.updateCount) continue;
                copiedUpdate.Value = frame.updateCount;
                var target = p.TargetTexture;
                var previous = RenderTexture.active;
                var device = GraphicsDevice.Current;
                FrameRenderer.Bind(device, RenderTargetBinding.Of((RenderTextureNative)target.NativeOrThrow));
                var size = new Vector2(target.width, target.height);
                device.SetViewport(new Rect(0f, 0f, size.x, size.y));
                // Depth too: the composite pass depth-tests, and the target's depth buffer (the
                // game's video render textures have one) is otherwise never cleared.
                device.Clear(true, true, Color.black, 1f);
                device.SetCamera(new CameraConstants(Matrix4x4.identity, Matrix4x4.Ortho(0f, size.x, 0f, size.y, -1f, 1f), Vector3.zero, -1f, 1f, true, size));
                var r = Fit(new Vector2(frame.width, frame.height), new Rect(0f, 0f, size.x, size.y), aspect);
                DrawQuad(frame, 1f, Matrix4x4.TRS(new Vector3(r.x, r.y, 0f), Quaternion.identity, new Vector3(r.width, r.height, 1f)));
                RenderTexture.active = previous;
            }
        }

        // Main thread: reused by every Draw (nothing it calls draws videos).
        static readonly System.Collections.Generic.List<OniMoono.Engine.Video.CameraVideo> cameraVideos = new();

        /// Far-plane videos before the scene, near-plane ones after it, on the camera's plane.
        public static void Draw(Camera camera, CameraNative cam, bool nearPlane)
        {
            foreach (var v in VideoOutputs.CameraVideos(camera, cameraVideos))
            {
                if (v.NearPlane != nearPlane || v.Texture == null) continue;
                var r = Fit(new Vector2(v.Texture.width, v.Texture.height), new Rect(0f, 0f, cam.PixelRect.width, cam.PixelRect.height), v.Aspect);
                float depth = nearPlane ? cam.Near * 1.001f : cam.Far * 0.999f;
                // Viewport-space rectangle → world corners at that distance.
                float u0 = r.xMin / cam.PixelRect.width, u1 = r.xMax / cam.PixelRect.width;
                float v0 = r.yMin / cam.PixelRect.height, v1 = r.yMax / cam.PixelRect.height;
                var bl = ViewportToWorld(cam, u0, v0, depth);
                var br = ViewportToWorld(cam, u1, v0, depth);
                var tl = ViewportToWorld(cam, u0, v1, depth);
                var m = Matrix4x4.identity;
                m.SetColumn(0, br - bl);
                m.SetColumn(1, tl - bl);
                m.SetColumn(2, Vector3.Cross(br - bl, tl - bl).normalized);
                m.SetColumn(3, new Vector4(bl.x, bl.y, bl.z, 1f));
                DrawQuad(v.Texture, v.Alpha, m);
            }
        }

        static Vector3 ViewportToWorld(CameraNative cam, float u, float v, float distance)
        {
            var (near, far) = cam.ViewportSegment(u, v);
            float dn = -cam.WorldToCamera.MultiplyPoint3x4(near).z, df = -cam.WorldToCamera.MultiplyPoint3x4(far).z;
            return Vector3.Lerp(near, far, df == dn ? 0f : (distance - dn) / (df - dn));
        }

        /// The video's rectangle within an area for an aspect mode (VideoAspectRatio).
        static Rect Fit(Vector2 video, Rect area, VideoAspect aspect)
        {
            float sx = area.width / video.x, sy = area.height / video.y;
            Vector2 size = aspect switch
            {
                VideoAspect.NoScaling => video,
                VideoAspect.FitVertically => video * sy,
                VideoAspect.FitHorizontally => video * sx,
                VideoAspect.FitInside => video * Math.Min(sx, sy),
                VideoAspect.FitOutside => video * Math.Max(sx, sy),
                _ => area.size,
            };
            return new Rect(area.x + (area.width - size.x) * 0.5f, area.y + (area.height - size.y) * 0.5f, size.x, size.y);
        }

        static void DrawQuad(Texture texture, float alpha, Matrix4x4 objectToWorld)
        {
            if (quad == null)
            {
                quad = new MeshNative
                {
                    VertexCount = 4,
                    Positions = [new(0f, 0f, 0f), new(0f, 1f, 0f), new(1f, 1f, 0f), new(1f, 0f, 0f)],
                    SubMeshes = new List<SubMeshData> { new SubMeshData { Indices = [0, 1, 2, 0, 2, 3] } },
                };
                quad.Uvs[0] = [new(0f, 0f, 0f, 0f), new(0f, 1f, 0f, 0f), new(1f, 1f, 0f, 0f), new(1f, 0f, 0f, 0f)];
                quad.UvDimensions[0] = 2;
                quad.RecalculateBounds();
            }
            var props = new PropertySheet();
            props.SetTexture(MainTex, texture);
            props.SetFloat(AlphaParam, alpha);
            GraphicsDevice.Current.Draw(new DrawRequest(quad, 0, objectToWorld, Composite, -1, props));
        }
    }
}
