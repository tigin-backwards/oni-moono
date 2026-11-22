// The native part of D3d11Probe (lines "DX nat ..."): on the render thread, through
// GL.IssuePluginEvent and CommandBuffer.IssuePluginEvent with a managed callback.
//
// Direct3D 11 (only when SystemInfo.graphicsDeviceType is Direct3D11): GetNativeTexturePtr is
// documented as an ID3D11Resource (Unity 6000.0 Texture.GetNativeTexturePtr), so only those
// pointers are called into: QueryInterface for IUnknown, ID3D11Resource, ID3D11Texture2D,
// ID3D11ShaderResourceView, ID3D11RenderTargetView and ID3D11DepthStencilView, then the
// texture's D3D11_TEXTURE2D_DESC (or the view's description and its resource's). Other native
// pointers (GetNativeDepthBufferPtr, GetNativeRenderBufferPtr) are only logged, never
// dereferenced: their native types are not documented. From the texture's device: feature level,
// creation flags and D3D11_FEATURE_THREADING. After a camera's draw: the vertex and pixel stages'
// bound constant buffers (VSGetConstantBuffers1/PSGetConstantBuffers1 when the context is a
// ID3D11DeviceContext1, with their constant ranges), each copied into a staging buffer and read
// (a staging buffer prefilled with 0xCD bytes, so a failed copy shows), the bound render target
// and depth views' descriptions and the viewport.
// Vtable slots and GUIDs come from the Windows SDK 10.0.26100.0 headers d3d11.h and d3d11_1.h
// (the C interface's Vtbl structs, counted in declaration order).
//
// OpenGL core: GetNativeTexturePtr is the texture name (same page); glIsTexture and the DSA
// glGetTextureLevelParameteriv give its internal format and size. After a camera's draw: the
// current program (or program pipeline stages), each active uniform with its block, offset and
// value: loose uniforms by glGetUniformfv, block members read from the bound uniform buffer.
// Enum values are from the Khronos registry's gl.xml.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SemanticsProbe
{
    public static class D3d11Native
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RenderEvent(int id);
        static readonly RenderEvent Callback = OnRender;
        static readonly IntPtr CallbackPtr = Marshal.GetFunctionPointerForDelegate(Callback);
        static readonly object Lock = new object();
        static readonly List<string> Out = new List<string>();
        static int doneCount;
        static bool d3d;
        static readonly List<(string Label, IntPtr Ptr)> Textures = new List<(string, IntPtr)>();
        static readonly List<string> Captures = new List<string>();

        static void O(string s) { lock (Lock) Out.Add(s); }
        static void Flush()
        {
            lock (Lock) { foreach (var s in Out) D3d11Probe.L("nat " + s); Out.Clear(); }
        }

        static IEnumerator Wait(int target)
        {
            for (int i = 0; i < 240 && System.Threading.Volatile.Read(ref doneCount) < target; i++) yield return null;
            if (System.Threading.Volatile.Read(ref doneCount) < target) O("timeout waiting for render-thread event " + target);
            Flush();
        }

        static void OnRender(int id)
        {
            try
            {
                if (id == 1) NativeTextures();
                else if (id == 2) DeviceInfo();
                else if (id >= 100) Capture(Captures[id - 100]);
            }
            catch (Exception e) { O("event " + id + " EXCEPTION " + e.GetType().Name + ": " + e.Message); }
            System.Threading.Interlocked.Increment(ref doneCount);
        }

        public static IEnumerator Run(MonoBehaviour host)
        {
            d3d = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
            if (!d3d && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLCore) { D3d11Probe.L("nat skipped: " + SystemInfo.graphicsDeviceType); yield break; }
            int expect = 0;

            // --- what GetNativeTexturePtr is
            var keep = new List<UnityEngine.Object>();
            void Tex(string label, Texture t)
            {
                keep.Add(t);
                IntPtr p = t.GetNativeTexturePtr();
                Textures.Add((label, p));
            }
            Tex("Texture2D RGBA32 4x4", D3d11Probe.Pattern());
            Tex("Texture2D RGBA32 8x8 mips", D3d11Probe.Pattern(8, 8, true));
            foreach (var f in new[] { TextureFormat.RGB24, TextureFormat.Alpha8, TextureFormat.R8, TextureFormat.ARGB32, TextureFormat.BGRA32, TextureFormat.RGB565, TextureFormat.RGBA4444, TextureFormat.RHalf, TextureFormat.RGBAHalf, TextureFormat.RFloat, TextureFormat.DXT1, TextureFormat.DXT5, TextureFormat.BC7 })
            {
                if (!SystemInfo.SupportsTextureFormat(f)) continue;
                var t = new Texture2D(4, 4, f, false);
                t.Apply();
                Tex("Texture2D " + f, t);
            }
            Tex("Texture2D RGBA32 linear", new Texture2D(4, 4, TextureFormat.RGBA32, false, true));
            foreach (var (f, depth) in new[] { (RenderTextureFormat.ARGB32, 0), (RenderTextureFormat.ARGB32, 16), (RenderTextureFormat.ARGB32, 24), (RenderTextureFormat.ARGB32, 32), (RenderTextureFormat.RHalf, 0), (RenderTextureFormat.ARGBHalf, 0), (RenderTextureFormat.ARGBFloat, 0), (RenderTextureFormat.RFloat, 0), (RenderTextureFormat.R8, 0), (RenderTextureFormat.RGB565, 0), (RenderTextureFormat.Depth, 24), (RenderTextureFormat.Shadowmap, 24) })
            {
                if (!SystemInfo.SupportsRenderTextureFormat(f)) continue;
                var rt = new RenderTexture(4, 4, depth, f);
                rt.Create();
                Tex("RenderTexture " + f + " depth " + depth, rt);
                D3d11Probe.L("nat ptrs RenderTexture " + f + " depth " + depth + " GetNativeTexturePtr=0x" + rt.GetNativeTexturePtr().ToString("x")
                    + " GetNativeDepthBufferPtr=0x" + rt.GetNativeDepthBufferPtr().ToString("x")
                    + " colorBuffer.GetNativeRenderBufferPtr=0x" + rt.colorBuffer.GetNativeRenderBufferPtr().ToString("x")
                    + " depthBuffer.GetNativeRenderBufferPtr=0x" + rt.depthBuffer.GetNativeRenderBufferPtr().ToString("x"));
            }
            var msaa = new RenderTexture(new RenderTextureDescriptor(4, 4, RenderTextureFormat.ARGB32, 24) { msaaSamples = 4 });
            msaa.Create();
            Tex("RenderTexture ARGB32 depth 24 msaa 4", msaa);
            foreach (var (l, p) in Textures) D3d11Probe.L("nat ptr " + l + " = 0x" + p.ToString("x"));
            GL.IssuePluginEvent(CallbackPtr, 1); expect++;
            yield return Wait(expect);
            GL.IssuePluginEvent(CallbackPtr, 2); expect++;
            yield return Wait(expect);

            // --- built-in shader values as the program sees them, after a camera's draw
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            var materials = new List<(string, Material)>();
            void Mat(string shader, params string[] keywords)
            {
                var s = Shader.Find(shader);
                if (s == null) { D3d11Probe.L("nat shader " + shader + " not found"); return; }
                var m = new Material(s);
                foreach (var k in keywords) m.EnableKeyword(k);
                materials.Add((shader + (keywords.Length > 0 ? "[" + string.Join("+", keywords) + "]" : ""), m));
            }
            Mat("Hidden/Internal-Colored");
            Mat("Legacy Shaders/Particles/Additive", "SOFTPARTICLES_ON");
            Mat("Hidden/Internal-CombineDepthNormals");
            Mat("Klei/Liquid", "_SAMPLEFILTERING_NONE");
            var go = new GameObject("DxCaptureCamera");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.cullingMask = 0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 200f;
            cam.fieldOfView = 50f;
            cam.orthographicSize = 3f;
            go.transform.position = new Vector3(1, 2, -10);
            var rtCam = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32) { name = "DxCamTarget" };
            rtCam.Create();
            keep.Add(rtCam);
            foreach (bool ortho in new[] { false, true })
            {
                foreach (bool toScreen in new[] { false, true })
                {
                    cam.orthographic = ortho;
                    cam.targetTexture = toScreen ? null : rtCam;
                    cam.depth = 100;
                    if (toScreen) cam.clearFlags = CameraClearFlags.Nothing; else cam.clearFlags = CameraClearFlags.SolidColor;
                    var cb = new CommandBuffer { name = "DxCapture" };
                    string where = (ortho ? "ortho" : "persp") + " " + (toScreen ? "screen" : "rt64x32");
                    foreach (var (name, m) in materials)
                    {
                        cb.DrawMesh(mesh, Matrix4x4.TRS(new Vector3(1, 2, 0), Quaternion.identity, Vector3.one), m, 0, 0);
                        Captures.Add(where + " " + name);
                        cb.IssuePluginEvent(CallbackPtr, 100 + Captures.Count - 1);
                    }
                    cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
                    var proj = cam.projectionMatrix;
                    D3d11Probe.L("nat camera " + where + " projectionMatrix=" + D3d11Probe.M(proj) + " gpu(false)=" + D3d11Probe.M(GL.GetGPUProjectionMatrix(proj, false))
                        + " gpu(true)=" + D3d11Probe.M(GL.GetGPUProjectionMatrix(proj, true)) + " worldToCamera=" + D3d11Probe.M(cam.worldToCameraMatrix)
                        + " pixel=" + cam.pixelWidth + "x" + cam.pixelHeight);
                    if (toScreen)
                    {
                        cam.enabled = true;
                        yield return null;
                        cam.enabled = false;
                    }
                    else cam.Render();
                    expect += materials.Count;
                    yield return Wait(expect);
                    foreach (var n in new[] { "_ProjectionParams", "_ScreenParams", "_ZBufferParams", "unity_OrthoParams" })
                    {
                        var v = Shader.GetGlobalVector(n);
                        D3d11Probe.L("nat camera " + where + " Shader.GetGlobalVector(" + n + ") after=" + D3d11Probe.F(v.x) + "," + D3d11Probe.F(v.y) + "," + D3d11Probe.F(v.z) + "," + D3d11Probe.F(v.w));
                    }
                    cam.RemoveCommandBuffer(CameraEvent.AfterEverything, cb);
                    cb.Release();
                }
            }
            UnityEngine.Object.Destroy(go);
            Flush();
        }

        // ================================================================ Direct3D 11

        static class Iid
        {
            public static Guid Unknown = new Guid("00000000-0000-0000-C000-000000000046");
            public static Guid Resource = new Guid(0xdc8e63f3, 0xd12b, 0x4952, 0xb4, 0x7b, 0x5e, 0x45, 0x02, 0x6a, 0x86, 0x2d);
            public static Guid Buffer = new Guid(0x48570b85, 0xd1ee, 0x4fcd, 0xa2, 0x50, 0xeb, 0x35, 0x07, 0x22, 0xb0, 0x37);
            public static Guid Texture2D = new Guid(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);
            public static Guid Srv = new Guid(0xb0e06fe0, 0x8192, 0x4e1a, 0xb1, 0xca, 0x36, 0xd7, 0x41, 0x47, 0x10, 0xb2);
            public static Guid Rtv = new Guid(0xdfdba067, 0x0b8d, 0x4865, 0x87, 0x5b, 0xd7, 0xb4, 0x51, 0x6c, 0xc1, 0x64);
            public static Guid Dsv = new Guid(0x9fdac92a, 0x1876, 0x48c3, 0xaf, 0xad, 0x25, 0xb9, 0x4f, 0x84, 0xa9, 0xb6);
            public static Guid Device = new Guid(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);
            public static Guid Context1 = new Guid(0xbb2c6faa, 0xb5fb, 0x4082, 0x8e, 0x6b, 0x38, 0x8b, 0x8c, 0xfa, 0x90, 0xe1);
        }

        [StructLayout(LayoutKind.Sequential)] struct Tex2DDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct BufDesc { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)] struct SubresourceData { public IntPtr SysMem; public uint Pitch, SlicePitch; }

        delegate int QiFn(IntPtr self, ref Guid iid, out IntPtr obj);
        delegate uint ReleaseFn(IntPtr self);
        delegate void GetDeviceFn(IntPtr self, out IntPtr device);
        delegate void Tex2DGetDescFn(IntPtr self, out Tex2DDesc desc);
        delegate void BufGetDescFn(IntPtr self, out BufDesc desc);
        delegate void ViewGetDescFn(IntPtr self, IntPtr desc);
        delegate void ViewGetResourceFn(IntPtr self, out IntPtr res);
        delegate int GetFeatureLevelFn(IntPtr self);
        delegate uint GetCreationFlagsFn(IntPtr self);
        delegate int CheckFeatureSupportFn(IntPtr self, int feature, IntPtr data, uint size);
        delegate void GetImmediateContextFn(IntPtr self, out IntPtr ctx);
        delegate int CreateBufferFn(IntPtr self, ref BufDesc desc, ref SubresourceData init, out IntPtr buf);
        delegate void GetCBsFn(IntPtr self, uint start, uint num, IntPtr bufs);
        delegate void GetCBs1Fn(IntPtr self, uint start, uint num, IntPtr bufs, IntPtr first, IntPtr count);
        delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);
        delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out Mapped m);
        delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
        delegate void OMGetRenderTargetsFn(IntPtr self, uint num, IntPtr rtvs, out IntPtr dsv);
        delegate void RSGetViewportsFn(IntPtr self, ref uint num, IntPtr vps);
        delegate void GetTypeFn(IntPtr self, out int type);

        static T Fn<T>(IntPtr obj, int slot) where T : class
            => Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T;

        static void Rel(IntPtr p) { if (p != IntPtr.Zero) Fn<ReleaseFn>(p, 2)(p); }

        static IntPtr Qi(IntPtr p, Guid iid, out int hr)
        {
            hr = Fn<QiFn>(p, 0)(p, ref iid, out IntPtr o);
            return o;
        }

        static string TexDesc(IntPtr tex)
        {
            Fn<Tex2DGetDescFn>(tex, 10)(tex, out var d);
            return "Texture2D " + d.Width + "x" + d.Height + " mips=" + d.MipLevels + " array=" + d.ArraySize + " DXGI_FORMAT=" + d.Format + " samples=" + d.SampleCount + "/" + d.SampleQuality
                + " usage=" + d.Usage + " bind=0x" + d.BindFlags.ToString("x") + " cpu=0x" + d.CPUAccessFlags.ToString("x") + " misc=0x" + d.MiscFlags.ToString("x");
        }

        static string ViewDesc(IntPtr view)
        {
            var buf = Marshal.AllocHGlobal(64);
            try
            {
                for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(buf, i, 0);
                Fn<ViewGetDescFn>(view, 8)(view, buf);
                var sb = new StringBuilder("DXGI_FORMAT=" + Marshal.ReadInt32(buf, 0) + " dim=" + Marshal.ReadInt32(buf, 4) + " rest=");
                for (int i = 8; i < 24; i += 4) sb.Append(i == 8 ? "" : ",").Append(Marshal.ReadInt32(buf, i));
                Fn<ViewGetResourceFn>(view, 7)(view, out IntPtr res);
                if (res != IntPtr.Zero)
                {
                    IntPtr t = Qi(res, Iid.Texture2D, out int hr);
                    sb.Append(" resource=").Append(hr == 0 ? TexDesc(t) : "not a Texture2D hr=0x" + hr.ToString("x"));
                    Rel(t); Rel(res);
                }
                return sb.ToString();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void NativeTextures()
        {
            foreach (var (label, p) in Textures)
            {
                if (p == IntPtr.Zero) { O("tex " + label + " null"); continue; }
                if (!d3d) { GlTexture(label, p); continue; }
                var sb = new StringBuilder("tex " + label + " QI:");
                foreach (var (n, g) in new[] { ("IUnknown", Iid.Unknown), ("ID3D11Resource", Iid.Resource), ("ID3D11Texture2D", Iid.Texture2D), ("ID3D11ShaderResourceView", Iid.Srv), ("ID3D11RenderTargetView", Iid.Rtv), ("ID3D11DepthStencilView", Iid.Dsv), ("ID3D11Buffer", Iid.Buffer) })
                {
                    IntPtr o = Qi(p, g, out int hr);
                    sb.Append(" ").Append(n).Append("=").Append(hr == 0 ? (o == p ? "same" : "0x" + o.ToString("x")) : "hr0x" + hr.ToString("x8"));
                    Rel(o);
                }
                IntPtr tex = Qi(p, Iid.Texture2D, out int thr);
                if (thr == 0)
                {
                    sb.Append(" | ").Append(TexDesc(tex));
                    Rel(tex);
                }
                else
                {
                    IntPtr res = Qi(p, Iid.Resource, out int rhr);
                    if (rhr == 0) { Fn<GetTypeFn>(res, 7)(res, out int type); sb.Append(" | resource dimension=" + type); Rel(res); }
                }
                O(sb.ToString());
            }
        }

        static IntPtr DeviceFromTexture()
        {
            foreach (var (label, p) in Textures)
            {
                if (p == IntPtr.Zero) continue;
                Fn<GetDeviceFn>(p, 3)(p, out IntPtr dev);
                if (dev != IntPtr.Zero) return dev;
            }
            return IntPtr.Zero;
        }

        static void DeviceInfo()
        {
            if (!d3d) { GlInfo(); return; }
            IntPtr dev = DeviceFromTexture();
            if (dev == IntPtr.Zero) { O("device none"); return; }
            int fl = Fn<GetFeatureLevelFn>(dev, 37)(dev);
            uint flags = Fn<GetCreationFlagsFn>(dev, 38)(dev);
            var data = Marshal.AllocHGlobal(64);
            try
            {
                for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(data, i, -1);
                int hr = Fn<CheckFeatureSupportFn>(dev, 33)(dev, 0, data, 8);
                O("device featureLevel=0x" + fl.ToString("x") + " creationFlags=0x" + flags.ToString("x") + " THREADING hr=0x" + hr.ToString("x")
                    + " DriverConcurrentCreates=" + Marshal.ReadInt32(data, 0) + " DriverCommandLists=" + Marshal.ReadInt32(data, 4));
            }
            finally { Marshal.FreeHGlobal(data); }
            IntPtr ctx1 = IntPtr.Zero;
            Fn<GetImmediateContextFn>(dev, 40)(dev, out IntPtr ctx);
            if (ctx != IntPtr.Zero) { ctx1 = Qi(ctx, Iid.Context1, out int hr1); O("device immediate context ID3D11DeviceContext1 QI hr=0x" + hr1.ToString("x")); Rel(ctx1); Rel(ctx); }
            Rel(dev);
        }

        static string Floats(byte[] b, int offset, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && offset + i * 4 + 4 <= b.Length; i++)
                sb.Append(i == 0 ? "" : ",").Append(D3d11Probe.F(BitConverter.ToSingle(b, offset + i * 4)));
            return sb.ToString();
        }

        static byte[] ReadBuffer(IntPtr dev, IntPtr ctx, IntPtr buf, out BufDesc desc)
        {
            Fn<BufGetDescFn>(buf, 10)(buf, out desc);
            var sd = new BufDesc { ByteWidth = desc.ByteWidth, Usage = 3, BindFlags = 0, CPUAccessFlags = 0x20000, MiscFlags = 0, StructureByteStride = 0 };
            var fill = Marshal.AllocHGlobal((int)desc.ByteWidth);
            try
            {
                for (int i = 0; i < desc.ByteWidth; i++) Marshal.WriteByte(fill, i, 0xCD);
                var init = new SubresourceData { SysMem = fill, Pitch = desc.ByteWidth, SlicePitch = 0 };
                int hr = Fn<CreateBufferFn>(dev, 3)(dev, ref sd, ref init, out IntPtr staging);
                if (hr != 0) { O("  CreateBuffer staging hr=0x" + hr.ToString("x")); return null; }
                Fn<CopyResourceFn>(ctx, 47)(ctx, staging, buf);
                hr = Fn<MapFn>(ctx, 14)(ctx, staging, 0, 1, 0, out var m);
                byte[] bytes = null;
                if (hr == 0)
                {
                    bytes = new byte[desc.ByteWidth];
                    Marshal.Copy(m.Data, bytes, 0, bytes.Length);
                    Fn<UnmapFn>(ctx, 15)(ctx, staging, 0);
                }
                else O("  Map hr=0x" + hr.ToString("x"));
                Rel(staging);
                return bytes;
            }
            finally { Marshal.FreeHGlobal(fill); }
        }

        static void Capture(string label)
        {
            if (!d3d) { GlCapture(label); return; }
            IntPtr dev = DeviceFromTexture();
            Fn<GetImmediateContextFn>(dev, 40)(dev, out IntPtr ctx);
            IntPtr ctx1 = Qi(ctx, Iid.Context1, out int hr1);
            O("capture " + label);
            var bufs = Marshal.AllocHGlobal(14 * IntPtr.Size);
            var first = Marshal.AllocHGlobal(14 * 4);
            var count = Marshal.AllocHGlobal(14 * 4);
            try
            {
                foreach (var (stage, slot1, slot) in new[] { ("VS", 125, 72), ("PS", 129, 77) })
                {
                    for (int i = 0; i < 14; i++) { Marshal.WriteIntPtr(bufs, i * IntPtr.Size, IntPtr.Zero); Marshal.WriteInt32(first, i * 4, 0); Marshal.WriteInt32(count, i * 4, 0); }
                    if (hr1 == 0) Fn<GetCBs1Fn>(ctx1, slot1)(ctx1, 0, 14, bufs, first, count);
                    else Fn<GetCBsFn>(ctx, slot)(ctx, 0, 14, bufs);
                    for (int i = 0; i < 14; i++)
                    {
                        IntPtr b = Marshal.ReadIntPtr(bufs, i * IntPtr.Size);
                        if (b == IntPtr.Zero) continue;
                        int fc = Marshal.ReadInt32(first, i * 4), nc = Marshal.ReadInt32(count, i * 4);
                        var bytes = ReadBuffer(dev, ctx, b, out var desc);
                        O("  " + stage + " b" + i + " byteWidth=" + desc.ByteWidth + " usage=" + desc.Usage + " bind=0x" + desc.BindFlags.ToString("x") + " cpu=0x" + desc.CPUAccessFlags.ToString("x")
                            + (hr1 == 0 ? " firstConstant=" + fc + " numConstants=" + nc : ""));
                        if (bytes != null)
                        {
                            int start = hr1 == 0 ? fc * 16 : 0;
                            int len = hr1 == 0 && nc > 0 ? Math.Min(nc * 16, bytes.Length - start) : bytes.Length - start;
                            for (int v = 0; v < len / 16 && v < 64; v++)
                                O("    " + stage + " b" + i + " @" + (v * 16) + " = " + Floats(bytes, start + v * 16, 4));
                        }
                        Rel(b);
                    }
                }
                var rtvs = Marshal.AllocHGlobal(8 * IntPtr.Size);
                for (int i = 0; i < 8; i++) Marshal.WriteIntPtr(rtvs, i * IntPtr.Size, IntPtr.Zero);
                Fn<OMGetRenderTargetsFn>(ctx, 89)(ctx, 8, rtvs, out IntPtr dsv);
                for (int i = 0; i < 8; i++)
                {
                    IntPtr v = Marshal.ReadIntPtr(rtvs, i * IntPtr.Size);
                    if (v == IntPtr.Zero) continue;
                    O("  RTV" + i + " " + ViewDesc(v));
                    Rel(v);
                }
                Marshal.FreeHGlobal(rtvs);
                if (dsv != IntPtr.Zero) { O("  DSV " + ViewDesc(dsv)); Rel(dsv); }
                else O("  DSV none");
                uint nvp = 16;
                var vps = Marshal.AllocHGlobal(16 * 24);
                Fn<RSGetViewportsFn>(ctx, 95)(ctx, ref nvp, vps);
                for (int i = 0; i < nvp; i++)
                {
                    var f = new float[6];
                    Marshal.Copy(vps + i * 24, f, 0, 6);
                    O("  viewport" + i + " TopLeftX=" + D3d11Probe.F(f[0]) + " TopLeftY=" + D3d11Probe.F(f[1]) + " Width=" + D3d11Probe.F(f[2]) + " Height=" + D3d11Probe.F(f[3]) + " MinDepth=" + D3d11Probe.F(f[4]) + " MaxDepth=" + D3d11Probe.F(f[5]));
                }
                Marshal.FreeHGlobal(vps);
            }
            finally
            {
                Marshal.FreeHGlobal(bufs); Marshal.FreeHGlobal(first); Marshal.FreeHGlobal(count);
                Rel(ctx1); Rel(ctx); Rel(dev);
            }
        }

        // ================================================================ OpenGL core

        [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] static extern void glGetIntegerv(uint pname, int[] data);
        [DllImport("opengl32.dll")] static extern byte glIsTexture(uint tex);
        [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);

        delegate void GetProgramivFn(uint program, uint pname, out int value);
        delegate void GetActiveUniformFn(uint program, uint index, int bufSize, out int length, out int size, out uint type, StringBuilder name);
        delegate void GetActiveUniformsivFn(uint program, int count, ref uint indices, uint pname, out int value);
        delegate int GetUniformLocationFn(uint program, string name);
        delegate void GetUniformfvFn(uint program, int location, float[] values);
        delegate void GetActiveUniformBlockivFn(uint program, uint block, uint pname, out int value);
        delegate void GetActiveUniformBlockNameFn(uint program, uint block, int bufSize, out int length, StringBuilder name);
        delegate void GetIntegeri_vFn(uint target, uint index, long[] data);
        delegate void GetInteger64i_vFn(uint target, uint index, long[] data);
        delegate void BindBufferFn(uint target, uint buffer);
        delegate void GetBufferSubDataFn(uint target, IntPtr offset, IntPtr size, float[] data);
        delegate void GetTextureLevelParameterivFn(uint tex, int level, uint pname, out int value);
        delegate void GetProgramPipelineivFn(uint pipeline, uint pname, out int value);

        static T Gl<T>(string name) where T : class
        {
            IntPtr p = wglGetProcAddress(name);
            long v = p.ToInt64();
            if (v == 0 || v == 1 || v == 2 || v == 3 || v == -1) return null;
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static void GlInfo()
        {
            O("gl vendor=\"" + Marshal.PtrToStringAnsi(glGetString(0x1F00)) + "\" renderer=\"" + Marshal.PtrToStringAnsi(glGetString(0x1F01)) + "\" version=\"" + Marshal.PtrToStringAnsi(glGetString(0x1F02)) + "\"");
            var i = new int[4];
            glGetIntegerv(0x8CA6, i); O("gl DRAW_FRAMEBUFFER_BINDING=" + i[0]);
        }

        static void GlTexture(string label, IntPtr p)
        {
            uint name = (uint)p.ToInt64();
            bool isTex = glIsTexture(name) != 0;
            var sb = new StringBuilder("tex " + label + " name=" + name + " glIsTexture=" + isTex);
            var get = Gl<GetTextureLevelParameterivFn>("glGetTextureLevelParameteriv");
            if (isTex && get != null)
            {
                get(name, 0, 0x1003, out int fmt); get(name, 0, 0x1000, out int w); get(name, 0, 0x1001, out int h); get(name, 0, 0x9106, out int samples);
                sb.Append(" internalFormat=0x" + fmt.ToString("x") + " " + w + "x" + h + " samples=" + samples);
            }
            O(sb.ToString());
        }

        static void GlCapture(string label)
        {
            O("capture " + label);
            var iv = new int[4];
            glGetIntegerv(0x8B8D, iv);
            uint prog = (uint)iv[0];
            glGetIntegerv(0x825A, iv);
            uint pipe = (uint)iv[0];
            glGetIntegerv(0x8CA6, iv);
            int fbo = iv[0];
            glGetIntegerv(0x0BA2, iv);
            O("  CURRENT_PROGRAM=" + prog + " PROGRAM_PIPELINE_BINDING=" + pipe + " DRAW_FRAMEBUFFER=" + fbo + " VIEWPORT=" + iv[0] + "," + iv[1] + "," + iv[2] + "," + iv[3]);
            var programs = new List<(string, uint)>();
            if (prog != 0) programs.Add(("program", prog));
            else if (pipe != 0)
            {
                var pp = Gl<GetProgramPipelineivFn>("glGetProgramPipelineiv");
                if (pp != null)
                {
                    pp(pipe, 0x8B31, out int vs); pp(pipe, 0x8B30, out int fs);
                    programs.Add(("vs", (uint)vs)); programs.Add(("fs", (uint)fs));
                }
            }
            var getProg = Gl<GetProgramivFn>("glGetProgramiv");
            var getActive = Gl<GetActiveUniformFn>("glGetActiveUniform");
            var getActiveIv = Gl<GetActiveUniformsivFn>("glGetActiveUniformsiv");
            var getLoc = Gl<GetUniformLocationFn>("glGetUniformLocation");
            var getFv = Gl<GetUniformfvFn>("glGetUniformfv");
            var blockIv = Gl<GetActiveUniformBlockivFn>("glGetActiveUniformBlockiv");
            var blockName = Gl<GetActiveUniformBlockNameFn>("glGetActiveUniformBlockName");
            var geti = Gl<GetInteger64i_vFn>("glGetInteger64i_v");
            var bind = Gl<BindBufferFn>("glBindBuffer");
            var sub = Gl<GetBufferSubDataFn>("glGetBufferSubData");
            if (getProg == null || getActive == null || getActiveIv == null) { O("  GL entry points missing"); return; }
            foreach (var (stage, p) in programs)
            {
                getProg(p, 0x8A36, out int nBlocks);
                var blockBinding = new Dictionary<int, (long Buf, long Start, int Binding, string Name, int Size)>();
                for (int b = 0; b < nBlocks; b++)
                {
                    var nm = new StringBuilder(256);
                    blockName(p, (uint)b, 256, out _, nm);
                    blockIv(p, (uint)b, 0x8A3F, out int binding);
                    blockIv(p, (uint)b, 0x8A40, out int size);
                    var l = new long[1];
                    geti(0x8A28, (uint)binding, l); long buf = l[0];
                    geti(0x8A29, (uint)binding, l); long start = l[0];
                    blockBinding[b] = (buf, start, binding, nm.ToString(), size);
                    O("  " + stage + " block " + b + " " + nm + " binding=" + binding + " size=" + size + " buffer=" + buf + " start=" + start);
                }
                getProg(p, 0x8B86, out int n);
                for (uint u = 0; u < n; u++)
                {
                    var nm = new StringBuilder(256);
                    getActive(p, u, 256, out _, out int size, out uint type, nm);
                    uint idx = u;
                    getActiveIv(p, 1, ref idx, 0x8A3A, out int block);
                    idx = u;
                    getActiveIv(p, 1, ref idx, 0x8A3B, out int offset);
                    int floats = type == 0x8B5C ? 16 : type == 0x8B52 ? 4 : type == 0x8B51 ? 3 : type == 0x8B50 ? 2 : type == 0x1406 ? 1 : 0;
                    string val = "";
                    if (floats > 0)
                    {
                        // A loose array uniform (size > 1, such as hlslcc_mtx4x4unity_MatrixVP[0], a
                        // vec4[4] matrix) is read element by element, each by its own location.
                        int count = block < 0 && size > 1 ? size : 1;
                        var f = new float[16 * count];
                        if (block < 0)
                        {
                            string baseName = nm.ToString();
                            if (baseName.EndsWith("[0]")) baseName = baseName.Substring(0, baseName.Length - 3);
                            var one = new float[16];
                            for (int e = 0; e < count; e++)
                            {
                                int loc = getLoc(p, count > 1 ? baseName + "[" + e + "]" : nm.ToString());
                                getFv(p, loc, one);
                                Array.Copy(one, 0, f, e * floats, floats);
                            }
                            floats *= count;
                        }
                        else if (blockBinding.TryGetValue(block, out var bb) && bb.Buf != 0 && bind != null && sub != null)
                        {
                            glGetIntegerv(0x8F36, iv);
                            int prev = iv[0];
                            bind(0x8F36, (uint)bb.Buf);
                            sub(0x8F36, new IntPtr(bb.Start + offset), new IntPtr(floats * 4), f);
                            bind(0x8F36, (uint)prev);
                        }
                        var sb = new StringBuilder();
                        for (int k = 0; k < floats; k++) sb.Append(k == 0 ? "" : ",").Append(D3d11Probe.F(f[k]));
                        val = " = " + sb;
                    }
                    O("  " + stage + " uniform " + nm + " type=0x" + type.ToString("x") + " size=" + size + " block=" + block + " offset=" + offset + val);
                }
            }
        }
    }
}
