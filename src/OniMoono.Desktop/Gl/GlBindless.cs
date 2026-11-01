using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Brutal.GlfwApi;
using Brutal.OpenGlApi;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// ARB_bindless_texture, where the driver has it: sampler uniforms are set to resident
    /// handles of (texture, sampler object) pairs instead of texture units, so draws never change
    /// texture bindings. NVIDIA's OpenGL driver serializes draws whose vertex stage reads
    /// textures other than the previous draw's (the game's animation batches each have their
    /// own data textures: ~37 µs a draw, most of a large colony's frame); with handles there are
    /// no binding changes. The programs' sampler uniforms are declared bindless when compiled
    /// (`layout(bindless_sampler) uniform;`). A texture with a handle keeps its parameters and
    /// image layout fixed (sampling state lives in sampler objects, GlSamplers), so a texture
    /// whose image is specified again is recreated under a new name (GlTextures).
    internal static unsafe class GlBindless
    {
        /// Diagnostics (--no-bindless): the binding path even where bindless textures exist.
        public static bool Disabled;

        /// --bindless: bindless textures even on a driver that binds by default (SlowOnDriver).
        public static bool Forced;

        public static bool Enabled { get; private set; }

        static delegate* unmanaged[Cdecl]<uint, uint, ulong> getTextureSamplerHandle;
        static delegate* unmanaged[Cdecl]<ulong, void> makeResident, makeNonResident;
        static delegate* unmanaged[Cdecl]<uint, int, ulong, void> programUniformHandle;

        /// AMD's Windows driver (GL_VENDOR "ATI Technologies Inc."; Mesa's AMD driver reports
        /// "AMD") advertises the extension, but there every draw and every texture upload costs a
        /// flat ~0.25 ms of GPU time with handles resident, against 0.001-0.009 ms bound to units:
        /// a loaded colony's frame took 270-570 ms of GPU time on an RX 9070 XT, 26-30 ms with
        /// --no-bindless (issue #27, measured with --gpu-profile; likely also issue #21's collapse,
        /// the same card, worsening as a colony's resident textures grow). Binding to units has
        /// no such cost there, so that driver binds.
        ///
        /// Mesa's AMD driver (radeonsi, GL_VENDOR "AMD") binds too, as AMD's Windows driver does:
        /// it adds every resident handle's buffer to each command submission
        /// (si_resident_buffers_add_all_to_bo_list), so the cost grows with the textures ever
        /// sampled. A save's colony preview samples about 4.5 times a normal frame's textures, and
        /// on an RX 9070 a colony that ran at 40-50 fps stayed at 20-25 after its first save,
        /// paused or not (issue #43). --bindless turns them on there anyway.
        public static bool SlowOnDriver(string vendor)
            => vendor.Contains("ATI Technologies", StringComparison.Ordinal) || vendor == "AMD";

        /// Why bindless textures are off, or null when they are on.
        public static string Reason { get; private set; }

        /// Loads the entry points (the rendering context is current).
        public static void Initialize(string vendor)
        {
            if (Disabled) { Reason = "--no-bindless"; return; }
            if (!GlPrograms.Extensions.Contains("GL_ARB_bindless_texture")) { Reason = "no ARB_bindless_texture"; return; }
            if (SlowOnDriver(vendor) && !Forced) { Reason = "AMD's driver, where resident handles slow every draw (--bindless turns them on)"; return; }
            getTextureSamplerHandle = (delegate* unmanaged[Cdecl]<uint, uint, ulong>)Glfw.GetProcAddress("glGetTextureSamplerHandleARB"u8);
            makeResident = (delegate* unmanaged[Cdecl]<ulong, void>)Glfw.GetProcAddress("glMakeTextureHandleResidentARB"u8);
            makeNonResident = (delegate* unmanaged[Cdecl]<ulong, void>)Glfw.GetProcAddress("glMakeTextureHandleNonResidentARB"u8);
            programUniformHandle = (delegate* unmanaged[Cdecl]<uint, int, ulong, void>)Glfw.GetProcAddress("glProgramUniformHandleui64ARB"u8);
            // The extension string above is what says the functions are there. These null checks
            // catch a missing one only where the loader returns null for it, as wglGetProcAddress
            // does; glXGetProcAddress and eglGetProcAddress may return an address for a function
            // the context lacks (GLX 1.4 section 3.3.12, EGL 1.5 section 3.11).
            Enabled = getTextureSamplerHandle != null && makeResident != null && makeNonResident != null && programUniformHandle != null;
            if (!Enabled) Reason = "entry points missing";
        }

        static readonly Regex FirstUniform = new(@"^[ \t]*(UNITY_LOCATION\(\d+\)[ \t]*)?uniform\b", RegexOptions.Multiline);

        /// A program stage's text with its sampler uniforms declared bindless (the default
        /// qualifier goes before the first uniform declaration), and the extension it requires.
        public static string Prepare(string body, out string extension)
        {
            extension = "#extension GL_ARB_bindless_texture : require\n";
            var m = FirstUniform.Match(body);
            if (!m.Success) return body;
            return body[..m.Index] + "layout(bindless_sampler) uniform;\n" + body[m.Index..];
        }

        /// The resident handle of a texture with its current sampler object, or 0 when GL gives
        /// it none. GetTextureSamplerHandleARB returns 0, with an error, for a texture name that
        /// has no texture object or a texture that is not complete; 0 is never a valid handle,
        /// and a shader sampling through one that is not a valid resident handle has undefined
        /// results that may include program termination (ARB_bindless_texture, section
        /// 3.9.X and its edits to section 2.14.6, Samplers). A 0 is neither made resident
        /// nor kept, so a texture that becomes complete gets its handle then. The caller samples
        /// a default texture instead, as a unit with an incomplete texture bound reads
        /// (0, 0, 0, 1) (OpenGL 4.6 section 11.1.3.5).
        public static ulong Handle(DeviceTexture t)
        {
            uint sampler = t.Sampler != 0 ? t.Sampler : GlSamplers.Default;
            if (t.Handles != null && t.Handles.TryGetValue(sampler, out var handle)) return handle;
            handle = t.Id == 0 ? 0 : getTextureSamplerHandle(t.Id, sampler);
            if (handle == 0)
            {
                if (!loggedNoHandle)
                {
                    loggedNoHandle = true;
                    OniMoono.Engine.EngineLog.Engine($"bindless: no handle for texture {t.Id} ({t.Width}x{t.Height}, target 0x{t.Target:X}, sampler {sampler}): not a texture or not complete; sampling the default texture instead (logged once)");
                }
                return 0;
            }
            makeResident(handle);
            (t.Handles ??= new Dictionary<uint, ulong>())[sampler] = handle;
            return handle;
        }

        static bool loggedNoHandle;

        public static bool HasHandles(DeviceTexture t) => t.Handles is { Count: > 0 };

        /// Makes the texture's handles non-resident (before the texture is deleted or replaced).
        public static void Release(DeviceTexture t)
        {
            if (t.Handles == null) return;
            foreach (var handle in t.Handles.Values) makeNonResident(handle);
            t.Handles.Clear();
        }

        public static void SetSampler(uint program, int location, ulong handle) => programUniformHandle(program, location, handle);
    }

    /// Sampler objects by sampling state (bindless handles pair a texture with one).
    internal static class GlSamplers
    {
        static readonly Dictionary<(GlTextures.SamplerSettings Settings, bool Mips, uint Target), uint> samplers = new();
        static uint defaultSampler;

        /// The state of a texture that no sampler settings have been applied to: linear
        /// filtering and repeat, as the device's default textures have. Repeat and a linear
        /// magnification filter are GL's defaults; its minification default is
        /// NEAREST_MIPMAP_LINEAR (OpenGL 4.6 section 8.22, table 23.18), set here to LINEAR.
        public static uint Default
        {
            get
            {
                if (defaultSampler == 0)
                {
                    defaultSampler = Create();
                    Set(defaultSampler, TextureMinFilter, (int)Linear);
                    Set(defaultSampler, TextureMagFilter, (int)Linear);
                }
                return defaultSampler;
            }
        }

        public static uint Get(GlTextures.SamplerSettings s, bool mips, uint target)
        {
            var key = (s, mips, target);
            if (samplers.TryGetValue(key, out var id)) return id;
            id = Create();
            uint min = s.FilterMode switch
            {
                UnityEngine.FilterMode.Point => mips ? NearestMipmapNearest : Nearest,
                UnityEngine.FilterMode.Bilinear => mips ? LinearMipmapNearest : Linear,
                _ => mips ? LinearMipmapLinear : Linear,
            };
            Set(id, TextureMinFilter, (int)min);
            Set(id, TextureMagFilter, (int)(s.FilterMode == UnityEngine.FilterMode.Point ? Nearest : Linear));
            Set(id, TextureWrapS, (int)GlTextures.Wrap(s.WrapU));
            Set(id, TextureWrapT, (int)GlTextures.Wrap(s.WrapV));
            Set(id, TextureWrapR, (int)GlTextures.Wrap(s.WrapW));
            SetF(id, TextureLodBias, s.MipBias);
            if (GlDevice.MaxAnisotropy > 1f)
                SetF(id, TextureMaxAnisotropy, s.FilterMode == UnityEngine.FilterMode.Point ? 1f : Math.Clamp(s.Aniso, 1, GlDevice.MaxAnisotropy));
            samplers[key] = id;
            return id;
        }

        static unsafe uint Create()
        {
            GlSampler id = default;
            N.glCreateSamplers(1, &id);
            return id.Id;
        }

        static unsafe void Set(uint sampler, uint name, int value) => N.glSamplerParameteri(new GlSampler(sampler), name, value);
        static unsafe void SetF(uint sampler, uint name, float value) => N.glSamplerParameterf(new GlSampler(sampler), name, value);
    }
}
